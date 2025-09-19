using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using Microsoft.Extensions.DependencyInjection;
using TNCSSPluginFoundation.Models.Plugin;

namespace MapChooserSharp.Modules.MapConfig;

internal sealed class McsMapConfigCommands(IServiceProvider serviceProvider) : PluginModuleBase(serviceProvider)
{
    public override string PluginModuleName => "McsMapConfigCommands";
    public override string ModuleChatPrefix => "unused";
    protected override bool UseTranslationKeyInModuleChatPrefix => false;

    private MapConfigRepository _mapConfigRepository = null!;

    protected override void OnAllPluginsLoaded()
    {
        _mapConfigRepository = ServiceProvider.GetRequiredService<MapConfigRepository>();
        Plugin.AddCommand("mcs_reloadmapconfig", "Reload map config cache. Usage: mcs_reloadmapconfig [mapName|workshopId]", CommandReloadMapConfig);
    }

    protected override void OnUnloadModule()
    {
        Plugin.RemoveCommand("mcs_reloadmapconfig", CommandReloadMapConfig);
    }

    [RequiresPermissions(@"css/root")]
    private void CommandReloadMapConfig(CCSPlayerController? player, CommandInfo info)
    {
        // No args: reload all
        if (info.ArgCount < 2)
        {
            _mapConfigRepository.ReloadMapConfiguration();
            PrintMessageToServerOrPlayerChat(player, "[MCS] Map config cache reloaded (all).");
            return;
        }

        string arg = info.ArgByIndex(1);

        // Numeric - treat as workshop id
        if (long.TryParse(arg, out long workshopId) && workshopId > 0)
        {
            bool ok = _mapConfigRepository.ReloadMapConfiguration(workshopId);
            if (ok)
                PrintMessageToServerOrPlayerChat(player, $"[MCS] Map config cache reloaded (workshopId={workshopId}).");
            else
                PrintMessageToServerOrPlayerChat(player, $"[MCS] Map config not found for workshopId={workshopId}.");
            return;
        }

        // Otherwise - treat as map name (case-insensitive)
        bool result = _mapConfigRepository.ReloadMapConfiguration(arg);
        if (result)
            PrintMessageToServerOrPlayerChat(player, $"[MCS] Map config cache reloaded (map=\"{arg}\").");
        else
            PrintMessageToServerOrPlayerChat(player, $"[MCS] Map config not found for map=\"{arg}\".");
    }
}