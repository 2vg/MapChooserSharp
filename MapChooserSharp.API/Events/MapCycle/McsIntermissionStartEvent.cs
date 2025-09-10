using MapChooserSharp.API.MapConfig;

namespace MapChooserSharp.API.Events.MapCycle;

/// <summary>
/// This event will be called when intermission starts (before map change during Cs2EndMatchScreen)
/// </summary>
/// <param name="modulePrefix">Module Prefix</param>
/// <param name="nextMap">The next map that will be loaded after intermission</param>
public class McsIntermissionStartEvent(string modulePrefix, IMapConfig nextMap): McsEventParam(modulePrefix), IMcsEventWithResult
{
    /// <summary>
    /// The next map that will be loaded after intermission.
    /// </summary>
    public IMapConfig NextMap { get; } = nextMap;
}