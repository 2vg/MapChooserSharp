using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Translations;
using CS2MenuManager.API;
using CS2MenuManager.API.Class;
using CS2MenuManager.API.Enum;
using MapChooserSharp.API.MapVoteController;
using MapChooserSharp.Modules.MapConfig.Interfaces;
using MapChooserSharp.Modules.MapVote.Interfaces;
using MapChooserSharp.Modules.McsMenu.Interfaces;
using MapChooserSharp.Modules.McsMenu.NominationMenu.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using TNCSSPluginFoundation;
using TNCSSPluginFoundation.Interfaces;
using ZLinq;

namespace MapChooserSharp.Modules.McsMenu.NominationMenu.Cs2MenuManager.MenuSystem;

public class McsCs2MenuManagerMenuSystemNominationUi(CCSPlayerController playerController, IServiceProvider provider): IMcsNominationUserInterface
{
    private IMcsGeneralMenuOption? _generalMenuOption;
    
    private List<IMcsNominationMenuOption> _nominationMenuOptions = new();

    private readonly TncssPluginBase _plugin = provider.GetRequiredService<TncssPluginBase>();
    
    private readonly IMcsInternalMapConfigProviderApi _mcsInternalMapConfigProviderApi = provider.GetRequiredService<IMcsInternalMapConfigProviderApi>();
    
    private readonly IDebugLogger _debugLogger = provider.GetRequiredService<IDebugLogger>();
    
    private readonly IMcsInternalMapVoteControllerApi _voteController = provider.GetRequiredService<IMcsInternalMapVoteControllerApi>();
    
    public int NominationOptionCount => _nominationMenuOptions.Count;
    
    public void OpenMenu()
    {
        if (_voteController.CurrentVoteState != McsMapVoteState.NoActiveVote)
            return;

        // Unused variable, but it required to decide what language should use in menu.
        using var tempLang = new WithTemporaryCulture(playerController.GetLanguage());
        
        
        _debugLogger.LogTrace($"[Player {playerController.PlayerName}] Creating nomination menu");
        
        StringBuilder menuTitle = new();
        
        if (_generalMenuOption != null && _generalMenuOption.MenuTitle != string.Empty)
        {
            menuTitle.Append(_plugin.LocalizeStringForPlayer(playerController, _generalMenuOption.MenuTitle + ".Html"));
        }
        else
        {
            menuTitle.Append(_plugin.LocalizeStringForPlayer(playerController, "General.Menu.Title.Html"));
        }
        
        var menu = MenuManager.CreateMenu<CS2MenuManager.API.Menu.MenuSystemSharpMenu>(menuTitle.ToString(), _plugin);
        
        foreach (var (option, index) in _nominationMenuOptions.Select((value, i) => (value, i)))
        {
            StringBuilder builder = new();
            
            // TODO() Use Alias name if enabled and available
            // TODO() Truncate MapName if too long
            builder.Append(_mcsInternalMapConfigProviderApi.GetMapName(option.NominationOption.MapConfig));
            
            menu.AddItem(builder.ToString(), (player, menuOption) =>
            {
                _nominationMenuOptions[index].SelectionCallback.Invoke(playerController, option.NominationOption);
            }, option.MenuDisabled ? DisableOption.DisableHideNumber : DisableOption.None);
        }
        
        _debugLogger.LogTrace($"[Player {playerController.PlayerName}] Menu init completed, opening...");
        
        menu.Display(playerController, 30);
    }

    public void CloseMenu()
    {
        MenuManager.CloseActiveMenu(playerController);
    }

    public void SetNominationOption(List<IMcsNominationMenuOption> mcsNominationMenuOptions)
    {
        _nominationMenuOptions = mcsNominationMenuOptions;
    }

    public void SetMenuOption(IMcsGeneralMenuOption generalMenuOption)
    {
        _generalMenuOption = generalMenuOption;
    }
}