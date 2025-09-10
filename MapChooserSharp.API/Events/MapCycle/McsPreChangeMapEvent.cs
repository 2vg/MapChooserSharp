using MapChooserSharp.API.MapConfig;

namespace MapChooserSharp.API.Events.MapCycle;

/// <summary>
/// This event will be called before map change
/// </summary>
/// <param name="modulePrefix">Module Prefix</param>
/// <param name="nextMap">The next map that will be loaded</param>
public class McsPreChangeMapEvent(string modulePrefix, IMapConfig nextMap): McsEventParam(modulePrefix), IMcsEventWithResult
{
    /// <summary>
    /// The next map that will be loaded.
    /// </summary>
    public IMapConfig NextMap { get; } = nextMap;
}