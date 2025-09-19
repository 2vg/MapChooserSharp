using System.Text;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;
using MapChooserSharp.API.MapConfig;
using MapChooserSharp.Modules.MapConfig;
using MapChooserSharp.Modules.MapConfig.Interfaces;
using MapChooserSharp.Modules.PluginConfig.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TNCSSPluginFoundation.Models.Plugin;
using Tomlyn;
using Tomlyn.Model;

namespace MapChooserSharp.Modules.WorkshopSync;

internal class McsWorkshopMapSynchronizer(IServiceProvider serviceProvider) : PluginModuleBase(serviceProvider)
{
    public override string PluginModuleName => "McsWorkshopMapSynchronizer";
    public override string ModuleChatPrefix => $" {ChatColors.Purple}[MCS WS]{ChatColors.Default}";
    protected override bool UseTranslationKeyInModuleChatPrefix => false;

    private IMcsPluginConfigProvider _configProvider = null!;
    private IMcsInternalMapConfigProviderApi _mapConfigProvider = null!;
    private HttpClient _httpClient = null!;
    private MapConfigRepository _mapConfigRepository = null!;
    private HashSet<string> _defaultKeys = new(StringComparer.OrdinalIgnoreCase);

    private nint? _pMultiAddonManager = null;

    private VirtualFunctionWithReturn<nint, string, bool, bool, bool>? _mamDownloadAddon;
    private Action<nint, bool>? _mamRefreshAddons;
    private VirtualFunctionWithReturn<nint, bool>? _mamHasUgcConnection;

    private const int UgcRetryMaxAttempts = 10;
    private const int UgcRetryDelayMs = 3000;

    protected override void OnAllPluginsLoaded()
    {
        _configProvider = ServiceProvider.GetRequiredService<IMcsPluginConfigProvider>();
        _mapConfigProvider = ServiceProvider.GetRequiredService<IMcsInternalMapConfigProviderApi>();
        _mapConfigRepository = ServiceProvider.GetRequiredService<MapConfigRepository>();
        _httpClient = new HttpClient();

        var workshopCollectionIds = _configProvider.PluginConfig.GeneralConfig.WorkshopCollectionIds;
        if (workshopCollectionIds != null && workshopCollectionIds.Length > 0)
        {
            Logger.LogInformation("[MCS WS] Found Workshop Collection IDs in config. Starting sync process...");
            SyncWorkshopCollections(workshopCollectionIds);
        }
        else
        {
            Logger.LogInformation("[MCS WS] No Workshop Collection IDs found in config. Skipping sync.");
        }

        var downloadOnlyCollections = _configProvider.PluginConfig.GeneralConfig.WorkshopDownloadOnlyCollectionIds;
        if (downloadOnlyCollections != null && downloadOnlyCollections.Length > 0)
        {
            Logger.LogInformation($"[MCS WS] Found {downloadOnlyCollections.Length} WorkshopDownloadOnlyCollectionIds. Will download addons without generating configs.");
            foreach (var colId in downloadOnlyCollections)
            {
                _ = QueueDownloadOnlyCollection(colId);
            }
        }

        Plugin.RegisterListener<Listeners.OnMetamodAllPluginsLoaded>(() =>
        {
            if (!_configProvider.PluginConfig.GeneralConfig.EnableWorkshopAutoDownload)
            {
                Logger.LogInformation("[MCS WS] Automatic Workshop downloads are disabled by config");
                return;
            }

            try
            {
                InitializeMultiAddonManager();
                if (_pMultiAddonManager.HasValue && _mamDownloadAddon != null)
                {
                    TryStartWorkshopDownloadsWithUgcRetry();
                }
                else
                {
                    Logger.LogInformation("[MCS WS] MultiAddonManager not available. Skipping automatic workshop downloads.");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"[MCS WS] Exception during MultiAddonManager initialization/downloads: {ex.Message}");
            }
        });
    }

    protected override void OnUnloadModule()
    {
        _httpClient.Dispose();
    }

    private void SyncWorkshopCollections(string[] collectionIds)
    {
        if (collectionIds.Length == 0)
            return;

        Logger.LogInformation($"[MCS WS] Syncing {collectionIds.Length} workshop collections.");

        foreach (string collectionId in collectionIds)
        {
            _ = SyncWorkshopCollectionAsync(collectionId.Trim());
        }
    }

    private async Task QueueDownloadOnlyCollection(string collectionId)
    {
        var trimmed = collectionId?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return;

        try
        {
            var ids = await FetchWorkshopIdsFromCollectionAsync(trimmed);
            if (ids.Count == 0)
            {
                Logger.LogWarning($"[MCS WS] No downloadable items found in WorkshopDownloadOnlyCollection: {trimmed}");
                return;
            }

            // ensure MultiAddonManager + UGC ready then download specific ids
            Plugin.RegisterListener<Listeners.OnMetamodAllPluginsLoaded>(() =>
            {
                if (!_configProvider.PluginConfig.GeneralConfig.EnableWorkshopAutoDownload)
                    return;

                try
                {
                    InitializeMultiAddonManager();
                    if (_pMultiAddonManager.HasValue && _mamDownloadAddon != null)
                    {
                        TryDownloadSpecificIdsWithUgcRetry(ids);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError($"[MCS WS] Failed to queue download-only collection {trimmed}: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Logger.LogError($"[MCS WS] Error loading download-only collection {trimmed}: {ex.Message}");
        }
    }

    private async Task<HashSet<string>> FetchWorkshopIdsFromCollectionAsync(string collectionId)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(collectionId))
            return result;

        if (!long.TryParse(collectionId, out _))
        {
            Logger.LogWarning($"[MCS WS] Invalid Workshop Collection ID format for download-only: '{collectionId}'. Skipping.");
            return result;
        }

        string url = $"https://steamcommunity.com/sharedfiles/filedetails/?id={collectionId}";
        Logger.LogInformation($"[MCS WS] (DL-Only) Fetching Workshop collection: {url}");

        string pageSource;
        using (var response = await _httpClient.GetAsync(url))
        {
            if (!response.IsSuccessStatusCode)
            {
                Logger.LogError($"[MCS WS] (DL-Only) Failed to fetch Workshop collection {collectionId}. Status code: {response.StatusCode}");
                return result;
            }
            pageSource = await response.Content.ReadAsStringAsync();
        }

        var pattern = new Regex(@"<a href=""https://steamcommunity.com/sharedfiles/filedetails/\?id=(\d+)"">", RegexOptions.Singleline);
        var matches = pattern.Matches(pageSource);
        foreach (Match m in matches)
        {
            var id = m.Groups[1].Value;
            if (!string.IsNullOrWhiteSpace(id))
                result.Add(id);
        }

        Logger.LogInformation($"[MCS WS] (DL-Only) Extracted {result.Count} items from collection {collectionId}");
        return result;
    }

    private void TryDownloadSpecificIdsWithUgcRetry(HashSet<string> ids)
    {
        if (ids.Count == 0)
            return;

        if (!_pMultiAddonManager.HasValue)
        {
            Logger.LogInformation("[MCS WS] MultiAddonManager handle is null. Skip retry for specific IDs.");
            return;
        }

        if (_mamHasUgcConnection == null)
        {
            Logger.LogWarning("[MCS WS] HasUGCConnection function not available. Proceeding without pre-check for specific IDs.");
            Server.NextFrame(() => DownloadSpecificIds(ids));
            return;
        }

        _ = Task.Run(async () =>
        {
            for (int attempt = 1; attempt <= UgcRetryMaxAttempts; attempt++)
            {
                bool connected = false;
                try
                {
                    connected = _mamHasUgcConnection.Invoke(_pMultiAddonManager.Value);
                }
                catch (Exception ex)
                {
                    Logger.LogError($"[MCS WS] HasUGCConnection threw: {ex.Message}");
                }

                if (connected)
                {
                    Logger.LogInformation($"[MCS WS] UGC connection available. Downloading {ids.Count} items (attempt {attempt}/{UgcRetryMaxAttempts}).");
                    Server.NextFrame(() => DownloadSpecificIds(ids));
                    return;
                }

                if (attempt < UgcRetryMaxAttempts)
                {
                    Logger.LogWarning($"[MCS WS] UGC not connected yet (specific IDs). Retrying in {UgcRetryDelayMs/1000.0:F1}s... (attempt {attempt}/{UgcRetryMaxAttempts})");
                    await Task.Delay(UgcRetryDelayMs);
                }
            }

            Logger.LogError("[MCS WS] UGC connection was not established after retries (specific IDs). Skipping downloads.");
        });
    }

    private void DownloadSpecificIds(HashSet<string> ids)
    {
        if (!_pMultiAddonManager.HasValue || _mamDownloadAddon == null)
            return;

        int success = 0, total = 0;
        foreach (var id in ids)
        {
            total++;
            bool started = false;
            try
            {
                started = _mamDownloadAddon.Invoke(_pMultiAddonManager.Value, id, false, true);
            }
            catch (Exception ex)
            {
                Logger.LogError($"[MCS WS] (DL-Only) DownloadAddon threw for {id}: {ex.Message}");
            }

            if (started)
            {
                success++;
                Logger.LogDebug($"[MCS WS] (DL-Only) DownloadAddon queued/already present: {id}");
            }
            else
            {
                Logger.LogWarning($"[MCS WS] (DL-Only) DownloadAddon returned false for {id}");
            }
        }

        Logger.LogInformation($"[MCS WS] (DL-Only) Requested workshop downloads: {success}/{total} (success/total)");

        try
        {
            _mamRefreshAddons?.Invoke(_pMultiAddonManager.Value, false);
            Logger.LogInformation("[MCS WS] (DL-Only) Invoked RefreshAddons(bReloadMap: false).");
        }
        catch (Exception ex)
        {
            Logger.LogError($"[MCS WS] (DL-Only) RefreshAddons failed: {ex.Message}");
        }
    }

    private void InitializeMultiAddonManager()
    {
        try
        {
            _pMultiAddonManager = Utilities.MetaFactory("MultiAddonManager003");
            if (!_pMultiAddonManager.HasValue)
            {
                _pMultiAddonManager = Utilities.MetaFactory("MultiAddonManager002");
            }

            if (!_pMultiAddonManager.HasValue)
            {
                Logger.LogInformation("[MCS WS] MultiAddonManager not found via MetaFactory.");
                return;
            }

            // vtable index assumptions based on provided header order:
            // 0 AddAddon, 1 RemoveAddon, 2 IsAddonMounted, 3 DownloadAddon, 4 RefreshAddons, 5 ClearAddons, 6 HasUGCConnection, 7..9 client functions
            _mamDownloadAddon = new(_pMultiAddonManager.Value, 3);
            _mamRefreshAddons = VirtualFunction.CreateVoid<nint, bool>(_pMultiAddonManager.Value, 4);
            _mamHasUgcConnection = new(_pMultiAddonManager.Value, 6);

            Logger.LogInformation("[MCS WS] MultiAddonManager resolved and delegates bound successfully.");
        }
        catch (Exception ex)
        {
            Logger.LogError($"[MCS WS] Failed to initialize MultiAddonManager: {ex.Message}");
            _pMultiAddonManager = null;
            _mamDownloadAddon = null;
            _mamRefreshAddons = null;
            _mamHasUgcConnection = null;
        }
    }

    private void TryStartWorkshopDownloadsWithUgcRetry()
    {
        if (!_pMultiAddonManager.HasValue)
        {
            Logger.LogInformation("[MCS WS] MultiAddonManager handle is null. Skip retry.");
            return;
        }

        if (_mamHasUgcConnection == null)
        {
            Logger.LogWarning("[MCS WS] HasUGCConnection function not available. Proceeding without pre-check.");
            Server.NextFrame(StartWorkshopDownloads);
            return;
        }

        _ = Task.Run(async () =>
        {
            for (int attempt = 1; attempt <= UgcRetryMaxAttempts; attempt++)
            {
                bool connected = false;
                try
                {
                    connected = _mamHasUgcConnection.Invoke(_pMultiAddonManager.Value);
                }
                catch (Exception ex)
                {
                    Logger.LogError($"[MCS WS] HasUGCConnection threw: {ex.Message}");
                }

                if (connected)
                {
                    Logger.LogInformation($"[MCS WS] UGC connection available. Starting workshop downloads (attempt {attempt}/{UgcRetryMaxAttempts}).");
                    Server.NextFrame(StartWorkshopDownloads);
                    return;
                }

                if (attempt < UgcRetryMaxAttempts)
                {
                    Logger.LogWarning($"[MCS WS] UGC not connected yet. Retrying in {UgcRetryDelayMs/1000.0:F1}s... (attempt {attempt}/{UgcRetryMaxAttempts})");
                    await Task.Delay(UgcRetryDelayMs);
                }
            }

            Logger.LogError("[MCS WS] UGC connection was not established after retries. Skipping automatic workshop downloads.");
        });
    }

    private void StartWorkshopDownloads()
    {
        if (!_pMultiAddonManager.HasValue || _mamDownloadAddon == null)
            return;

        var ids = CollectAllWorkshopIds();
        if (ids.Count == 0)
        {
            Logger.LogInformation("[MCS WS] No workshop IDs found in current map configurations. Skipping downloads.");
            return;
        }

        int success = 0, total = 0;
        foreach (var id in ids)
        {
            total++;
            bool started = false;
            try
            {
                started = _mamDownloadAddon.Invoke(_pMultiAddonManager.Value, id, false, true);
            }
            catch (Exception ex)
            {
                Logger.LogError($"[MCS WS] DownloadAddon threw for {id}: {ex.Message}");
            }

            if (started)
            {
                success++;
                Logger.LogDebug($"[MCS WS] DownloadAddon queued/already present: {id}");
            }
            else
            {
                Logger.LogWarning($"[MCS WS] DownloadAddon returned false for {id}");
            }
        }

        Logger.LogInformation($"[MCS WS] Requested workshop downloads: {success}/{total} (success/total)");

        try
        {
            _mamRefreshAddons?.Invoke(_pMultiAddonManager.Value, false);
            Logger.LogInformation("[MCS WS] Invoked RefreshAddons(bReloadMap: false).");
        }
        catch (Exception ex)
        {
            Logger.LogError($"[MCS WS] RefreshAddons failed: {ex.Message}");
        }
    }

    private HashSet<string> CollectAllWorkshopIds()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var all = _mapConfigProvider.GetMapConfigs().Values;
            foreach (var cfg in all)
            {
                if (cfg.WorkshopId > 0)
                {
                    ids.Add(cfg.WorkshopId.ToString());
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"[MCS WS] Failed to collect workshop IDs: {ex.Message}");
        }
        return ids;
    }

    private async Task<int> SyncWorkshopCollectionAsync(string collectionId)
    {
        if (string.IsNullOrWhiteSpace(collectionId) || !long.TryParse(collectionId, out _))
        {
            Logger.LogWarning($"[MCS WS] Invalid Workshop Collection ID format: '{collectionId}'. Skipping.");
            return 0;
        }

        try
        {
            string url = $"https://steamcommunity.com/sharedfiles/filedetails/?id={collectionId}";
            Logger.LogInformation($"[MCS WS] Fetching Workshop collection: {url}");

            string pageSource;
            using (var response = await _httpClient.GetAsync(url))
            {
                if (!response.IsSuccessStatusCode)
                {
                    Logger.LogError($"[MCS WS] Failed to fetch Workshop collection {collectionId}. Status code: {response.StatusCode}");
                    return 0;
                }
                pageSource = await response.Content.ReadAsStringAsync();
            }

            var pattern = new Regex(@"<a href=""https://steamcommunity.com/sharedfiles/filedetails/\?id=(\d+)"">.*?<div class=""workshopItemTitle"">(.*?)</div>", RegexOptions.Singleline);
            var matches = pattern.Matches(pageSource);

            if (matches.Count == 0)
            {
                Logger.LogWarning($"[MCS WS] No maps found in Workshop collection {collectionId}. The collection might be empty, private, or the page structure might have changed.");
                return 0;
            }

            Logger.LogInformation($"[MCS WS] Found {matches.Count} potential maps in Workshop collection {collectionId}.");

            int newMapsAdded = 0;

            Server.NextFrame(() =>
            {
                int processedInThisFrame = 0;
                foreach (Match match in matches)
                {
                    string currentWorkshopIdStr = match.Groups[1].Value;
                    string mapTitle = match.Groups[2].Value.Trim();

                    if (!long.TryParse(currentWorkshopIdStr, out long currentWorkshopId))
                    {
                        Logger.LogWarning($"[MCS WS] Failed to parse workshop ID '{currentWorkshopIdStr}' for map '{mapTitle}'. Skipping.");
                        continue;
                    }

                    string validMapName = CreateValidMapName(mapTitle, currentWorkshopIdStr);

                    if (_mapConfigProvider.GetMapConfig(currentWorkshopId) != null)
                    {
                        Logger.LogDebug($"[MCS WS] Map '{mapTitle}' (ID: {currentWorkshopId}) already exists in map settings. Skipping.");
                        continue;
                    }

                    var defaultConfig = _configProvider.PluginConfig.GeneralConfig; // For fallback values if needed
                    var defaultMapCycleConfig = _configProvider.PluginConfig.MapCycleConfig;

                    var defaultMapConfig = GetDefaultMapConfig();

                    if (defaultMapConfig == null)
                    {
                        Logger.LogWarning($"[MCS WS] Default map config not found. Skipping map '{mapTitle}' (ID: {currentWorkshopId}). Please ensure config/default.toml exists.");
                        continue;
                    }

                    Logger.LogInformation($"[MCS WS] Default config loaded: MapNameAlias='{defaultMapConfig.MapNameAlias}', IsDisabled={defaultMapConfig.IsDisabled}, WorkshopId={defaultMapConfig.WorkshopId}");
                    
                    var newMapConfig = new NullableMapConfig
                    {
                        MapName = validMapName,
                        MapNameAlias = defaultMapConfig.MapNameAlias,
                        MapDescription = _defaultKeys.Contains("MapDescription") ? $"Workshop map: {mapTitle}" : null,
                        IsDisabled = defaultMapConfig.IsDisabled,
                        WorkshopId = currentWorkshopId,
                        OnlyNomination = defaultMapConfig.OnlyNomination,
                        MaxExtends = defaultMapConfig.MaxExtends,
                        MaxExtCommandUses = defaultMapConfig.MaxExtCommandUses,
                        MapTime = defaultMapConfig.MapTime,
                        ExtendTimePerExtends = defaultMapConfig.ExtendTimePerExtends,
                        MapRounds = defaultMapConfig.MapRounds,
                        ExtendRoundsPerExtends = defaultMapConfig.ExtendRoundsPerExtends,
                        Cooldown = defaultMapConfig.Cooldown,
                        RequiredPermissions = defaultMapConfig.RequiredPermissions != null ? new List<string>(defaultMapConfig.RequiredPermissions) : new List<string>(),
                        RestrictToAllowedUsersOnly = defaultMapConfig.RestrictToAllowedUsersOnly,
                        AllowedSteamIds = defaultMapConfig.AllowedSteamIds != null ? new List<ulong>(defaultMapConfig.AllowedSteamIds) : new List<ulong>(),
                        DisallowedSteamIds = defaultMapConfig.DisallowedSteamIds != null ? new List<ulong>(defaultMapConfig.DisallowedSteamIds) : new List<ulong>(),
                        MaxPlayers = defaultMapConfig.MaxPlayers,
                        MinPlayers = defaultMapConfig.MinPlayers,
                        ProhibitAdminNomination = defaultMapConfig.ProhibitAdminNomination,
                        DaysAllowed = defaultMapConfig.DaysAllowed != null ? new List<DayOfWeek>(defaultMapConfig.DaysAllowed) : new List<DayOfWeek>(),
                        AllowedTimeRanges = defaultMapConfig.AllowedTimeRanges != null ? new List<ITimeRange>(defaultMapConfig.AllowedTimeRanges) : new List<ITimeRange>(),
                        GroupSettingsArray = defaultMapConfig.GroupSettingsArray != null ? new List<string>(defaultMapConfig.GroupSettingsArray) : new List<string>(),
                        GroupSettings = new List<IMapGroupSettings>(),
                        ExtraConfiguration = new Dictionary<string, Dictionary<string, string>>()
                    };
                    
                    try
                    {
                        string tomlContent = ConvertMapConfigToTomlString(newMapConfig, _defaultKeys);
                        Logger.LogInformation($"[MCS WS] Generated TOML content for '{mapTitle}':\n{tomlContent}");

                        if (AddMapConfigToSystem(newMapConfig, validMapName))
                        {
                            Logger.LogInformation($"[MCS WS] Created map settings for '{mapTitle}' (ID: {currentWorkshopId})");
                            newMapsAdded++;
                            processedInThisFrame++;
                        }
                        else
                        {
                            Logger.LogWarning($"[MCS WS] Failed to add map settings for '{mapTitle}' (ID: {currentWorkshopId})");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"[MCS WS] Error creating map settings file for '{mapTitle}' (ID: {currentWorkshopId}): {ex.Message}");
                    }
                }

                if (processedInThisFrame > 0)
                {
                    Logger.LogInformation($"[MCS WS] Processed {processedInThisFrame} new maps from collection {collectionId}.");
                    Logger.LogInformation("[MCS WS] Reloading map configurations for changes to take effect...");

                    _mapConfigRepository.ReloadMapConfiguration();

                    Logger.LogInformation("[MCS WS] Map configurations reloaded successfully.");
                }
                else if (matches.Count > 0)
                {
                    Logger.LogInformation($"[MCS WS] All {matches.Count} maps from collection {collectionId} already exist or were skipped.");
                }
            });

            return newMapsAdded;
        }
        catch (HttpRequestException httpEx)
        {
            Logger.LogError($"[MCS WS] HTTP request error syncing Workshop collection {collectionId}: {httpEx.Message}");
            return 0;
        }
        catch (Exception ex)
        {
            Logger.LogError($"[MCS WS] General error syncing Workshop collection {collectionId}: {ex.Message}");
            Logger.LogError($"[MCS WS] StackTrace: {ex.StackTrace}");
            return 0;
        }
    }

    private NullableMapConfig? GetDefaultMapConfig()
    {
        string configDir = Path.Combine(Plugin.ModuleDirectory, "config");
        string mapsTomlPath = Path.Combine(configDir, "maps.toml");

        Logger.LogInformation($"[MCS WS] Checking for config files in: {configDir}");
        Logger.LogInformation($"[MCS WS] maps.toml exists: {File.Exists(mapsTomlPath)}");

        // Check if using unified configuration (maps.toml exists)
        if (File.Exists(mapsTomlPath))
        {
            return GetDefaultMapConfigFromUnifiedFile(mapsTomlPath);
        }
        else
        {
            // Check for default.toml in split configuration
            return GetDefaultMapConfigFromSplitFile(configDir);
        }
    }

    private NullableMapConfig? GetDefaultMapConfigFromUnifiedFile(string mapsTomlPath)
    {
        try
        {
            string tomlContent = File.ReadAllText(mapsTomlPath);
            var toml = Toml.ToModel(tomlContent);

            // Look for MapChooserSharpSettings.Default section
            if (toml.TryGetValue("MapChooserSharpSettings", out var settingsObj) && settingsObj is TomlTable settingsTable)
            {
                if (settingsTable.TryGetValue("Default", out var defaultObj) && defaultObj is TomlTable defaultTable)
                {
                    _defaultKeys = new HashSet<string>(defaultTable.Keys, StringComparer.OrdinalIgnoreCase);
                    return ParseDefaultConfigFromTomlTable(defaultTable);
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Logger.LogError($"[MCS WS] Error reading default config from unified file: {ex.Message}");
            return null;
        }
    }

    private NullableMapConfig? GetDefaultMapConfigFromSplitFile(string configDir)
    {
        string defaultConfigPath = Path.Combine(configDir, "default.toml");
        Logger.LogInformation($"[MCS WS] Checking for default.toml at: {defaultConfigPath}");
        Logger.LogInformation($"[MCS WS] default.toml exists: {File.Exists(defaultConfigPath)}");

        if (!File.Exists(defaultConfigPath))
        {
            return null;
        }

        try
        {
            string tomlContent = File.ReadAllText(defaultConfigPath);
            Logger.LogInformation($"[MCS WS] default.toml content:\n{tomlContent}");
            var toml = Toml.ToModel(tomlContent);

            // Look for MapChooserSharpSettings.Default section
            if (toml.TryGetValue("MapChooserSharpSettings", out var settingsObj) && settingsObj is TomlTable settingsTable)
            {
                if (settingsTable.TryGetValue("Default", out var defaultObj) && defaultObj is TomlTable defaultTable)
                {
                    _defaultKeys = new HashSet<string>(defaultTable.Keys, StringComparer.OrdinalIgnoreCase);
                    var result = ParseDefaultConfigFromTomlTable(defaultTable);
                    Logger.LogInformation($"[MCS WS] Parsed default config: MapNameAlias='{result?.MapNameAlias}', IsDisabled={result?.IsDisabled}, WorkshopId={result?.WorkshopId}");
                    return result;
                }
            }

            Logger.LogWarning($"[MCS WS] MapChooserSharpSettings.Default section not found in default.toml");
            return null;
        }
        catch (Exception ex)
        {
            Logger.LogError($"[MCS WS] Error reading default config from split file: {ex.Message}");
            return null;
        }
    }

    private NullableMapConfig ParseDefaultConfigFromTomlTable(TomlTable toml)
    {
        var defaultConfig = new NullableMapConfig();

        if (toml.TryGetValue("MapNameAlias", out var mapNameAliasObj) && mapNameAliasObj is string mapNameAlias)
            defaultConfig.MapNameAlias = mapNameAlias;

        if (toml.TryGetValue("MapDescription", out var mapDescriptionObj) && mapDescriptionObj is string mapDescription)
            defaultConfig.MapDescription = mapDescription;

        if (toml.TryGetValue("IsDisabled", out var isDisabledObj) && isDisabledObj is bool isDisabled)
            defaultConfig.IsDisabled = isDisabled;

        if (toml.TryGetValue("WorkshopId", out var workshopIdObj) && workshopIdObj is long workshopId)
            defaultConfig.WorkshopId = workshopId;

        if (toml.TryGetValue("OnlyNomination", out var onlyNominationObj) && onlyNominationObj is bool onlyNomination)
            defaultConfig.OnlyNomination = onlyNomination;

        if (toml.TryGetValue("MaxExtends", out var maxExtendsObj) && maxExtendsObj is long maxExtends)
            defaultConfig.MaxExtends = (int)maxExtends;

        if (toml.TryGetValue("MaxExtCommandUses", out var maxExtCommandUsesObj) && maxExtCommandUsesObj is long maxExtCommandUses)
            defaultConfig.MaxExtCommandUses = (int)maxExtCommandUses;

        if (toml.TryGetValue("MapTime", out var mapTimeObj) && mapTimeObj is long mapTime)
            defaultConfig.MapTime = (int)mapTime;

        if (toml.TryGetValue("ExtendTimePerExtends", out var extendTimePerExtendsObj) && extendTimePerExtendsObj is long extendTimePerExtends)
            defaultConfig.ExtendTimePerExtends = (int)extendTimePerExtends;

        if (toml.TryGetValue("MapRounds", out var mapRoundsObj) && mapRoundsObj is long mapRounds)
            defaultConfig.MapRounds = (int)mapRounds;

        if (toml.TryGetValue("ExtendRoundsPerExtends", out var extendRoundsPerExtendsObj) && extendRoundsPerExtendsObj is long extendRoundsPerExtends)
            defaultConfig.ExtendRoundsPerExtends = (int)extendRoundsPerExtends;

        if (toml.TryGetValue("Cooldown", out var cooldownObj) && cooldownObj is long cooldown)
            defaultConfig.Cooldown = (int)cooldown;

        if (toml.TryGetValue("RestrictToAllowedUsersOnly", out var restrictToAllowedUsersOnlyObj) && restrictToAllowedUsersOnlyObj is bool restrictToAllowedUsersOnly)
            defaultConfig.RestrictToAllowedUsersOnly = restrictToAllowedUsersOnly;

        if (toml.TryGetValue("MaxPlayers", out var maxPlayersObj) && maxPlayersObj is long maxPlayers)
            defaultConfig.MaxPlayers = (int)maxPlayers;

        if (toml.TryGetValue("MinPlayers", out var minPlayersObj) && minPlayersObj is long minPlayers)
            defaultConfig.MinPlayers = (int)minPlayers;

        if (toml.TryGetValue("ProhibitAdminNomination", out var prohibitAdminNominationObj) && prohibitAdminNominationObj is bool prohibitAdminNomination)
            defaultConfig.ProhibitAdminNomination = prohibitAdminNomination;

        return defaultConfig;
    }

    private bool AddMapConfigToSystem(NullableMapConfig mapConfig, string validMapName)
    {
        try
        {
            string configDir = Path.Combine(Plugin.ModuleDirectory, "config");
            string mapsTomlPath = Path.Combine(configDir, "maps.toml");
            
            Directory.CreateDirectory(configDir); // Ensure directory exists
            
            // Check if maps.toml exists (unified configuration mode)
            if (File.Exists(mapsTomlPath))
            {
                return AddMapConfigToUnifiedFile(mapConfig, validMapName, mapsTomlPath);
            }
            else
            {
                // Split configuration mode - create individual file
                return AddMapConfigToSplitFile(mapConfig, validMapName, configDir);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"[MCS WS] Error adding map config to system: {ex.Message}");
            return false;
        }
    }

    private bool AddMapConfigToUnifiedFile(NullableMapConfig mapConfig, string validMapName, string mapsTomlPath)
    {
        try
        {
            string existingContent = File.ReadAllText(mapsTomlPath);
            string newMapSection = ConvertMapConfigToTomlSectionString(mapConfig, validMapName, _defaultKeys);
            
            // Append the new map section to the existing file
            string updatedContent = existingContent.TrimEnd() + Environment.NewLine + Environment.NewLine + newMapSection;
            
            File.WriteAllText(mapsTomlPath, updatedContent);
            Logger.LogDebug($"[MCS WS] Added map '{validMapName}' to unified maps.toml file");
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError($"[MCS WS] Error adding map to unified file: {ex.Message}");
            return false;
        }
    }

    private bool AddMapConfigToSplitFile(NullableMapConfig mapConfig, string validMapName, string configDir)
    {
        try
        {
            string mapConfigToml = ConvertMapConfigToTomlSectionString(mapConfig, validMapName, _defaultKeys);
            
            // Individual map files are typically stored in a 'maps' subdirectory within 'config'
            string mapsDir = Path.Combine(configDir, "synced_workshopmaps");
            Directory.CreateDirectory(mapsDir);
            string filePath = Path.Combine(mapsDir, $"{validMapName}.toml");

            File.WriteAllText(filePath, mapConfigToml);
            Logger.LogDebug($"[MCS WS] Created individual map file: {filePath}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError($"[MCS WS] Error creating individual map file: {ex.Message}");
            return false;
        }
    }

    private string ConvertMapConfigToTomlSectionString(NullableMapConfig mapConfig, string validMapName, HashSet<string> allowedKeys)
    {
        var sb = new StringBuilder();
        
        // Add section header for unified file
        sb.AppendLine($"[{validMapName}]");
        
        // Add the map configuration content based only on keys present in default
        sb.Append(ConvertMapConfigToTomlString(mapConfig, allowedKeys));
        
        return sb.ToString();
    }

    private string ConvertMapConfigToTomlString(NullableMapConfig mapConfig)
    {
        var sb = new StringBuilder();

        // ---- string ----
        if (mapConfig.MapNameAlias != null)
            sb.AppendLine($"MapNameAlias = \"{TomlEncode(mapConfig.MapNameAlias)}\"");
        if (mapConfig.MapDescription != null)
            sb.AppendLine($"MapDescription = \"{TomlEncode(mapConfig.MapDescription)}\"");

        // ---- bool ----
        if (mapConfig.IsDisabled.HasValue)
            sb.AppendLine($"IsDisabled = {mapConfig.IsDisabled.Value.ToString().ToLowerInvariant()}");
        if (mapConfig.OnlyNomination.HasValue)
            sb.AppendLine($"OnlyNomination = {mapConfig.OnlyNomination.Value.ToString().ToLowerInvariant()}");
        if (mapConfig.RestrictToAllowedUsersOnly.HasValue)
            sb.AppendLine($"RestrictToAllowedUsersOnly = {mapConfig.RestrictToAllowedUsersOnly.Value.ToString().ToLowerInvariant()}");
        if (mapConfig.ProhibitAdminNomination.HasValue)
            sb.AppendLine($"ProhibitAdminNomination = {mapConfig.ProhibitAdminNomination.Value.ToString().ToLowerInvariant()}");

        // ---- numeric ----
        if (mapConfig.WorkshopId.HasValue && mapConfig.WorkshopId.Value > 0)
            sb.AppendLine($"WorkshopId = {mapConfig.WorkshopId.Value}");
        if (mapConfig.MaxExtends.HasValue)
            sb.AppendLine($"MaxExtends = {mapConfig.MaxExtends.Value}");
        if (mapConfig.MaxExtCommandUses.HasValue)
            sb.AppendLine($"MaxExtCommandUses = {mapConfig.MaxExtCommandUses.Value}");
        if (mapConfig.MapTime.HasValue)
            sb.AppendLine($"MapTime = {mapConfig.MapTime.Value}");
        if (mapConfig.ExtendTimePerExtends.HasValue)
            sb.AppendLine($"ExtendTimePerExtends = {mapConfig.ExtendTimePerExtends.Value}");
        if (mapConfig.MapRounds.HasValue)
            sb.AppendLine($"MapRounds = {mapConfig.MapRounds.Value}");
        if (mapConfig.ExtendRoundsPerExtends.HasValue)
            sb.AppendLine($"ExtendRoundsPerExtends = {mapConfig.ExtendRoundsPerExtends.Value}");
        if (mapConfig.Cooldown.HasValue)
            sb.AppendLine($"Cooldown = {mapConfig.Cooldown.Value}");
        if (mapConfig.MaxPlayers.HasValue)
            sb.AppendLine($"MaxPlayers = {mapConfig.MaxPlayers.Value}");
        if (mapConfig.MinPlayers.HasValue)
            sb.AppendLine($"MinPlayers = {mapConfig.MinPlayers.Value}");

        // ---- collections ----
        if (mapConfig.RequiredPermissions is { Count: > 0 })
            sb.AppendLine($"RequiredPermissions = [{string.Join(", ", mapConfig.RequiredPermissions.Select(p => $"\"{TomlEncode(p)}\""))}]");

        if (mapConfig.AllowedSteamIds is { Count: > 0 })
            sb.AppendLine($"AllowedSteamIds = [{string.Join(", ", mapConfig.AllowedSteamIds)}]");
        if (mapConfig.DisallowedSteamIds is { Count: > 0 })
            sb.AppendLine($"DisallowedSteamIds = [{string.Join(", ", mapConfig.DisallowedSteamIds)}]");

        if (mapConfig.DaysAllowed is { Count: > 0 })
            sb.AppendLine($"DaysAllowed = [{string.Join(", ", mapConfig.DaysAllowed.Select(d => $"\"{d}\""))}]");

        if (mapConfig.AllowedTimeRanges is { Count: > 0 })
        {
            var timeRangesStr = mapConfig.AllowedTimeRanges.Select(tr =>
                $"{{ Start = \"{tr.StartTime:hh\\:mm}\", End = \"{tr.EndTime:hh\\:mm}\" }}"
            );
            sb.AppendLine($"AllowedTimeRanges = [{string.Join(", ", timeRangesStr)}]");
        }

        if (mapConfig.GroupSettingsArray is { Count: > 0 })
            sb.AppendLine($"GroupSettings = [{string.Join(", ", mapConfig.GroupSettingsArray.Select(g => $"\"{TomlEncode(g)}\""))}]");

        return sb.ToString();
    }

    // Overload: write only keys that exist in default.toml
    private string ConvertMapConfigToTomlString(NullableMapConfig mapConfig, HashSet<string> allowedKeys)
    {
        var sb = new StringBuilder();

        // ---- string ----
        if (allowedKeys.Contains("MapNameAlias"))
            sb.AppendLine($"MapNameAlias = \"{TomlEncode(mapConfig.MapNameAlias ?? string.Empty)}\"");
        if (allowedKeys.Contains("MapDescription") && mapConfig.MapDescription != null)
            sb.AppendLine($"MapDescription = \"{TomlEncode(mapConfig.MapDescription)}\"");

        // ---- bool ----
        if (allowedKeys.Contains("IsDisabled") && mapConfig.IsDisabled.HasValue)
            sb.AppendLine($"IsDisabled = {mapConfig.IsDisabled.Value.ToString().ToLowerInvariant()}");
        if (allowedKeys.Contains("OnlyNomination") && mapConfig.OnlyNomination.HasValue)
            sb.AppendLine($"OnlyNomination = {mapConfig.OnlyNomination.Value.ToString().ToLowerInvariant()}");
        if (allowedKeys.Contains("RestrictToAllowedUsersOnly") && mapConfig.RestrictToAllowedUsersOnly.HasValue)
            sb.AppendLine($"RestrictToAllowedUsersOnly = {mapConfig.RestrictToAllowedUsersOnly.Value.ToString().ToLowerInvariant()}");
        if (allowedKeys.Contains("ProhibitAdminNomination") && mapConfig.ProhibitAdminNomination.HasValue)
            sb.AppendLine($"ProhibitAdminNomination = {mapConfig.ProhibitAdminNomination.Value.ToString().ToLowerInvariant()}");

        // ---- numeric ----
        if (allowedKeys.Contains("WorkshopId") && mapConfig.WorkshopId.HasValue && mapConfig.WorkshopId.Value > 0)
            sb.AppendLine($"WorkshopId = {mapConfig.WorkshopId.Value}");
        if (allowedKeys.Contains("MaxExtends") && mapConfig.MaxExtends.HasValue)
            sb.AppendLine($"MaxExtends = {mapConfig.MaxExtends.Value}");
        if (allowedKeys.Contains("MaxExtCommandUses") && mapConfig.MaxExtCommandUses.HasValue)
            sb.AppendLine($"MaxExtCommandUses = {mapConfig.MaxExtCommandUses.Value}");
        if (allowedKeys.Contains("MapTime") && mapConfig.MapTime.HasValue)
            sb.AppendLine($"MapTime = {mapConfig.MapTime.Value}");
        if (allowedKeys.Contains("ExtendTimePerExtends") && mapConfig.ExtendTimePerExtends.HasValue)
            sb.AppendLine($"ExtendTimePerExtends = {mapConfig.ExtendTimePerExtends.Value}");
        if (allowedKeys.Contains("MapRounds") && mapConfig.MapRounds.HasValue)
            sb.AppendLine($"MapRounds = {mapConfig.MapRounds.Value}");
        if (allowedKeys.Contains("ExtendRoundsPerExtends") && mapConfig.ExtendRoundsPerExtends.HasValue)
            sb.AppendLine($"ExtendRoundsPerExtends = {mapConfig.ExtendRoundsPerExtends.Value}");
        if (allowedKeys.Contains("Cooldown") && mapConfig.Cooldown.HasValue)
            sb.AppendLine($"Cooldown = {mapConfig.Cooldown.Value}");
        if (allowedKeys.Contains("MaxPlayers") && mapConfig.MaxPlayers.HasValue)
            sb.AppendLine($"MaxPlayers = {mapConfig.MaxPlayers.Value}");
        if (allowedKeys.Contains("MinPlayers") && mapConfig.MinPlayers.HasValue)
            sb.AppendLine($"MinPlayers = {mapConfig.MinPlayers.Value}");

        // ---- collections ----
        if (allowedKeys.Contains("RequiredPermissions"))
        {
            if (mapConfig.RequiredPermissions is { Count: > 0 })
                sb.AppendLine($"RequiredPermissions = [{string.Join(", ", mapConfig.RequiredPermissions.Select(p => $"\"{TomlEncode(p)}\""))}]");
            else
                sb.AppendLine("RequiredPermissions = []");
        }

        if (allowedKeys.Contains("AllowedSteamIds"))
        {
            if (mapConfig.AllowedSteamIds is { Count: > 0 })
                sb.AppendLine($"AllowedSteamIds = [{string.Join(", ", mapConfig.AllowedSteamIds)}]");
            else
                sb.AppendLine("AllowedSteamIds = []");
        }

        if (allowedKeys.Contains("DisallowedSteamIds"))
        {
            if (mapConfig.DisallowedSteamIds is { Count: > 0 })
                sb.AppendLine($"DisallowedSteamIds = [{string.Join(", ", mapConfig.DisallowedSteamIds)}]");
            else
                sb.AppendLine("DisallowedSteamIds = []");
        }

        if (allowedKeys.Contains("DaysAllowed"))
        {
            if (mapConfig.DaysAllowed is { Count: > 0 })
                sb.AppendLine($"DaysAllowed = [{string.Join(", ", mapConfig.DaysAllowed.Select(d => $"\"{d}\""))}]");
            else
                sb.AppendLine("DaysAllowed = []");
        }

        if (allowedKeys.Contains("AllowedTimeRanges"))
        {
            if (mapConfig.AllowedTimeRanges is { Count: > 0 })
            {
                var timeRangesStr = mapConfig.AllowedTimeRanges.Select(tr =>
                    $"{{ Start = \"{tr.StartTime:hh\\:mm}\", End = \"{tr.EndTime:hh\\:mm}\" }}"
                );
                sb.AppendLine($"AllowedTimeRanges = [{string.Join(", ", timeRangesStr)}]");
            }
            else
            {
                sb.AppendLine("AllowedTimeRanges = []");
            }
        }

        if (allowedKeys.Contains("GroupSettings"))
        {
            if (mapConfig.GroupSettingsArray is { Count: > 0 })
                sb.AppendLine($"GroupSettings = [{string.Join(", ", mapConfig.GroupSettingsArray.Select(g => $"\"{TomlEncode(g)}\""))}]");
            else
                sb.AppendLine("GroupSettings = []");
        }

        return sb.ToString();
    }
    private string TomlEncode(string value)
    {
        if (value == null) return string.Empty;
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    // Prevent invalid characters in map names
    private string CreateValidMapName(string workshopTitle, string workshopId)
    {
        string validName = Regex.Replace(workshopTitle, @"[^a-zA-Z0-9_\-\.]", "_");

        if (string.IsNullOrWhiteSpace(validName))
        {
            validName = "workshop_map_" + workshopId;
        }
        else if (validName.Length > 0 && !char.IsLetterOrDigit(validName[0]) && validName[0] != '_')
        {
            // Ensure it starts with a letter, digit, or underscore if it's not already.
            // Prepending "map_" might be too aggressive if the name is already somewhat valid.
            // Let's allow names starting with digits or underscores.
            // If it starts with something truly problematic (e.g. '-'), then prepend.
            if (validName.StartsWith("-") || validName.StartsWith(".")) // Example problematic starts
            {
                validName = "map_" + validName;
            }
        }
        // Max filename length considerations (usually not an issue for modern systems but good to keep in mind)
        // if (validName.Length > 100) validName = validName.Substring(0, 100);

        return validName.ToLowerInvariant();
    }
}
