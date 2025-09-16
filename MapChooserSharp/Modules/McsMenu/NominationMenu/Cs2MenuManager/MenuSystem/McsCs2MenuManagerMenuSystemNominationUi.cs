using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Translations;
using MapChooserSharp.API.MapVoteController;
using MapChooserSharp.Modules.MapConfig.Interfaces;
using MapChooserSharp.Modules.MapVote.Interfaces;
using MapChooserSharp.Modules.McsMenu.Interfaces;
using MapChooserSharp.Modules.McsMenu.NominationMenu.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using TNCSSPluginFoundation;
using TNCSSPluginFoundation.Interfaces;
using ZLinq;
using MenuSystemSharp.API;
using Microsoft.Extensions.Logging;

namespace MapChooserSharp.Modules.McsMenu.NominationMenu.Cs2MenuManager.MenuSystem;

public class McsCs2MenuManagerMenuSystemNominationUi(CCSPlayerController playerController, IServiceProvider provider) : IMcsNominationUserInterface
{
    private IMcsGeneralMenuOption? _generalMenuOption;

    private List<IMcsNominationMenuOption> _nominationMenuOptions = new();

    private readonly TncssPluginBase _plugin = provider.GetRequiredService<TncssPluginBase>();

    private readonly IMcsInternalMapConfigProviderApi _mcsInternalMapConfigProviderApi = provider.GetRequiredService<IMcsInternalMapConfigProviderApi>();

    private readonly IDebugLogger _debugLogger = provider.GetRequiredService<IDebugLogger>();

    private readonly IMcsInternalMapVoteControllerApi _voteController = provider.GetRequiredService<IMcsInternalMapVoteControllerApi>();

    private IMenuInstance? _currentMenuInstance;

    public int NominationOptionCount => _nominationMenuOptions.Count;

    public void OpenMenu()
    {
        if (_voteController.CurrentVoteState != McsMapVoteState.NoActiveVote)
            return;

        if (_currentMenuInstance != null)
        {
            try
            {
                _currentMenuInstance.Close();
            }
            catch (Exception e)
            {
                _plugin.Logger.LogError($"Error closing existing nomination menu: {e}");
            }
            finally
            {
                _currentMenuInstance = null;
            }
        }

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

        var menuSystem = MenuSystemSharp.API.MenuSystem.Instance;
        if (menuSystem == null || !menuSystem.IsAvailable)
        {
            _plugin.Logger.LogError("MenuSystem is not available");
            return;
        }

        var menu = menuSystem.CreateMenu();
        menu.Title = menuTitle.ToString();
        menu.ItemControls = MenuItemControlFlags.Panel | MenuItemControlFlags.Next | MenuItemControlFlags.Back | MenuItemControlFlags.Exit;

        _currentMenuInstance = menu;

        foreach (var (option, index) in _nominationMenuOptions.Select((value, i) => (value, i)))
        {
            StringBuilder builder = new();

            // TODO() Use Alias name if enabled and available
            // TODO() Truncate MapName if too long
            builder.Append(_mcsInternalMapConfigProviderApi.GetMapName(option.NominationOption.MapConfig));

            var styleFlags = option.MenuDisabled ? MenuItemStyleFlags.Disabled | MenuItemStyleFlags.HasNumber : (MenuItemStyleFlags.Active | MenuItemStyleFlags.HasNumber);

            menu.AddItem(styleFlags, builder.ToString(), (menuInstance, player, itemPosition, itemOnPage, data) =>
            {
                _nominationMenuOptions[index].SelectionCallback.Invoke(playerController, option.NominationOption);
            });
        }

        _debugLogger.LogTrace($"[Player {playerController.PlayerName}] Menu init completed, opening...");

        menu.DisplayToPlayer(playerController, 0, 30);
    }

    public void CloseMenu()
    {
        if (_currentMenuInstance != null)
        {
            try
            {
                _currentMenuInstance.Close();
            }
            catch (Exception e)
            {
                _plugin.Logger.LogError($"MenuSystem Nomination UI method {nameof(CloseMenu)} has thrown an exception: {e}");
            }
            finally
            {
                _currentMenuInstance = null;
            }
        }
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