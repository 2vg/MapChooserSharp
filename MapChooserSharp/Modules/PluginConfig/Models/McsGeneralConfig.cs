using MapChooserSharp.Modules.PluginConfig.Interfaces;
using MapChooserSharp.Modules.RockTheVote;

namespace MapChooserSharp.Modules.PluginConfig.Models;

public class McsGeneralConfig(bool shouldUseAliasMapNameIfAvailable, bool verboseCooldownPrint, string[] workshopCollectionIds, bool shouldAutoFixMapName, IMcsSqlConfig sqlConfig, MapTransitionMethod mapTransitionMethod) : IMcsGeneralConfig
{
    public bool ShouldUseAliasMapNameIfAvailable { get; } = shouldUseAliasMapNameIfAvailable;
    public bool VerboseCooldownPrint { get; } = verboseCooldownPrint;
    public string[] WorkshopCollectionIds { get; } = workshopCollectionIds;
    public bool ShouldAutoFixMapName { get; } = shouldAutoFixMapName;
    public IMcsSqlConfig SqlConfig { get; } = sqlConfig;
    public MapTransitionMethod MapTransitionMethod { get; } = mapTransitionMethod;
}