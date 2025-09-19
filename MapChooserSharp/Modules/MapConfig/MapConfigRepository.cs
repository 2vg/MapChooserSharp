using System;
using System.Collections.Generic;
using System.Linq;
using MapChooserSharp.Modules.MapConfig.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using TNCSSPluginFoundation.Models.Plugin;
using ZLinq;

namespace MapChooserSharp.Modules.MapConfig;

internal sealed class MapConfigRepository(IServiceProvider serviceProvider): PluginModuleBase(serviceProvider)
{
    public override string PluginModuleName => "MapConfigRepository";
    public override string ModuleChatPrefix => "unused";
    protected override bool UseTranslationKeyInModuleChatPrefix => false;

    private IMcsInternalMapConfigProviderApi _mcsInternalMapConfigProviderApi = null!;
    
    private string _mapConfigLocation = null!;
    
    
    protected override void OnInitialize()
    {
        ReloadMapConfiguration();
    }

    protected override void OnUnloadModule()
    {
    }

    public override void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton(this);
        services.AddSingleton(_mcsInternalMapConfigProviderApi);
    }

    // Reload all map configurations into in-memory cache
    public void ReloadMapConfiguration()
    {
        _mapConfigLocation = Path.Combine(Plugin.ModuleDirectory, "config", "maps.toml");

        var parsedProvider = new MapConfigParser(_mapConfigLocation).Load();

        if (_mcsInternalMapConfigProviderApi == null!)
        {
            // First load (initialization path)
            _mcsInternalMapConfigProviderApi = parsedProvider;
            return;
        }

        // Live-reload path: mutate existing provider to keep DI references valid
        if (_mcsInternalMapConfigProviderApi is McsMapConfigProvider targetProvider)
        {
            var newMaps = new Dictionary<string, API.MapConfig.IMapConfig>(parsedProvider.GetMapConfigs(), StringComparer.OrdinalIgnoreCase);
            var newGroups = new Dictionary<string, API.MapConfig.IMapGroupSettings>(parsedProvider.GetGroupSettings(), StringComparer.OrdinalIgnoreCase);
            targetProvider.ReplaceAll(newMaps, newGroups);
        }
        else
        {
            // Fallback: replace reference (should not happen in normal flow)
            _mcsInternalMapConfigProviderApi = parsedProvider;
        }
    }

    // Reload only a specific map by map name (case-insensitive). Returns false if not found.
    public bool ReloadMapConfiguration(string mapName)
    {
        if (string.IsNullOrWhiteSpace(mapName))
            return false;

        if (_mcsInternalMapConfigProviderApi is not McsMapConfigProvider targetProvider)
            return false;

        var parsedProvider = new MapConfigParser(_mapConfigLocation).Load();

        // Find updated map from fresh parse
        var updatedEntry = parsedProvider.GetMapConfigs()
            .FirstOrDefault(kv => kv.Key.Equals(mapName, StringComparison.OrdinalIgnoreCase));

        if (updatedEntry.Key == null)
            return false;

        // Find existing key as stored in current provider to preserve key casing
        var existingKey = _mcsInternalMapConfigProviderApi.GetMapConfigs()
            .Keys.FirstOrDefault(k => k.Equals(mapName, StringComparison.OrdinalIgnoreCase));

        if (existingKey == null)
            return false;

        var updatedGroups = new Dictionary<string, API.MapConfig.IMapGroupSettings>(parsedProvider.GetGroupSettings(), StringComparer.OrdinalIgnoreCase);
        return targetProvider.ReplaceMapByName(existingKey, updatedEntry.Value, updatedGroups);
    }

    // Reload only a specific map by workshop id. Returns false if not found or id <= 0.
    public bool ReloadMapConfiguration(long workshopId)
    {
        if (workshopId <= 0)
            return false;

        if (_mcsInternalMapConfigProviderApi is not McsMapConfigProvider targetProvider)
            return false;

        var parsedProvider = new MapConfigParser(_mapConfigLocation).Load();

        // Locate updated map by workshop id from fresh parse
        var updatedMap = parsedProvider.GetMapConfigs()
            .Select(kv => kv.Value)
            .FirstOrDefault(v => v.WorkshopId == workshopId);

        if (updatedMap == null)
            return false;

        var updatedGroups = new Dictionary<string, API.MapConfig.IMapGroupSettings>(parsedProvider.GetGroupSettings(), StringComparer.OrdinalIgnoreCase);
        return targetProvider.ReplaceMapByWorkshopId(workshopId, updatedMap, updatedGroups);
    }
}