using MapChooserSharp.API.MapConfig;

namespace MapChooserSharp.API.Events.MapCycle;

/// <summary>
/// This event will be called after map change
/// </summary>
/// <param name="modulePrefix">Module Prefix</param>
/// <param name="previousMap">The map that was active before the change</param>
/// <param name="newMap">The new map that was loaded after the change</param>
public class McsPostChangeMapEvent(string modulePrefix, IMapConfig previousMap, IMapConfig newMap): McsEventParam(modulePrefix), IMcsEventNoResult
{
    /// <summary>
    /// The map that was active before the change.
    /// </summary>
    public IMapConfig PreviousMap { get; } = previousMap;

    /// <summary>
    /// The new map that was loaded after the change.
    /// </summary>
    public IMapConfig NewMap { get; } = newMap;
}