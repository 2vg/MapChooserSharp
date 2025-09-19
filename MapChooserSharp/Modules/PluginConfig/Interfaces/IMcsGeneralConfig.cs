using MapChooserSharp.Modules.RockTheVote;

namespace MapChooserSharp.Modules.PluginConfig.Interfaces;

internal interface IMcsGeneralConfig
{
    internal bool ShouldUseAliasMapNameIfAvailable { get; }
    
    internal bool VerboseCooldownPrint { get; }
    
    internal string[] WorkshopCollectionIds { get; }
    internal string[] WorkshopDownloadOnlyCollectionIds { get; }

    internal bool EnableWorkshopAutoDownload { get; }

    internal bool ShouldAutoFixMapName { get; }
    
    internal IMcsSqlConfig SqlConfig { get; }
    
    internal MapTransitionMethod MapTransitionMethod { get; }
}