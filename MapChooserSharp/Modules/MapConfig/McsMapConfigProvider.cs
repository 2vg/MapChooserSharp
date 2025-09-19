using MapChooserSharp.API.MapConfig;
using MapChooserSharp.Modules.MapConfig.Interfaces;
using ZLinq;

namespace MapChooserSharp.Modules.MapConfig;

public sealed class McsMapConfigProvider: IMcsInternalMapConfigProviderApi
{
    private readonly Dictionary<string, IMapConfig> _mapConfigs;
    private readonly Dictionary<string, IMapGroupSettings> _groupConfigs;
    
    public McsMapConfigProvider(Dictionary<string, IMapConfig> mapConfigs, Dictionary<string, IMapGroupSettings> groupConfigs)
    {
        _mapConfigs = mapConfigs
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        _groupConfigs = groupConfigs
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }
    
    public IReadOnlyDictionary<string, IMapGroupSettings> GetGroupSettings()
    {
        return _groupConfigs;
    }

    public IReadOnlyDictionary<string, IMapConfig> GetMapConfigs()
    {
        return _mapConfigs;
    }

    public IMapConfig? GetMapConfig(string mapName)
    {
        _mapConfigs.TryGetValue(mapName, out var mapConfig);
        return mapConfig;
    }

    public IMapConfig? GetMapConfig(long workshopId)
    {
        if (workshopId <= 0)
            return null;
        
        foreach (var (key, value) in _mapConfigs)
        {
            if (value.WorkshopId == workshopId)
                return value;
        }

        return null;
    }

    public string GetMapName(IMapConfig mapConfig)
    {
        if (mapConfig.MapNameAlias != string.Empty)
            return mapConfig.MapNameAlias;
        
        return mapConfig.MapName;
    }
    // Thread-safety for live reload updates
    private readonly object _syncRoot = new();

    // Internal: replace entire map/group dictionaries in-place to keep existing references valid
    internal void ReplaceAll(Dictionary<string, IMapConfig> newMaps, Dictionary<string, IMapGroupSettings> newGroups)
    {
        // Normalize ordering and comparer like ctor
        var normMaps = newMaps
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        var normGroups = newGroups
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        lock (_syncRoot)
        {
            // Clear and repopulate to ensure readers holding the instance see updated content
            _mapConfigs.Clear();
            foreach (var kv in normMaps)
                _mapConfigs[kv.Key] = kv.Value;

            _groupConfigs.Clear();
            foreach (var kv in normGroups)
                _groupConfigs[kv.Key] = kv.Value;
        }
    }

    // Internal: replace a single map by its dictionary key (map name); also refresh group entries that are present in provided groups
    internal bool ReplaceMapByName(string mapNameKey, IMapConfig updatedMap, Dictionary<string, IMapGroupSettings> possiblyUpdatedGroups)
    {
        lock (_syncRoot)
        {
            if (!_mapConfigs.ContainsKey(mapNameKey))
                return false;

            _mapConfigs[mapNameKey] = updatedMap;

            // Update group dictionary entries that came along with reparse result to reflect latest group settings for consumers of GetGroupSettings()
            foreach (var (k, v) in possiblyUpdatedGroups)
            {
                _groupConfigs[k] = v;
            }

            return true;
        }
    }

    // Internal: replace a single map by WorkshopId; also refresh groups
    internal bool ReplaceMapByWorkshopId(long workshopId, IMapConfig updatedMap, Dictionary<string, IMapGroupSettings> possiblyUpdatedGroups)
    {
        if (workshopId <= 0)
            return false;

        lock (_syncRoot)
        {
            // Find existing key by walking current dictionary to keep key casing/aliasing
            string? existingKey = null;
            foreach (var (k, v) in _mapConfigs)
            {
                if (v.WorkshopId == workshopId)
                {
                    existingKey = k;
                    break;
                }
            }

            if (existingKey == null)
                return false;

            _mapConfigs[existingKey] = updatedMap;

            foreach (var (k, v) in possiblyUpdatedGroups)
            {
                _groupConfigs[k] = v;
            }

            return true;
        }
    }
}