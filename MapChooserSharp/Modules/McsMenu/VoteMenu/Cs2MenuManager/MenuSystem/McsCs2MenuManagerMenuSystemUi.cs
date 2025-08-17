using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Translations;
using CS2MenuManager.API;
using CS2MenuManager.API.Class;
using CS2MenuManager.API.Enum;
using MapChooserSharp.API.MapVoteController;
using MapChooserSharp.Modules.MapVote.Interfaces;
using MapChooserSharp.Modules.McsMenu.Interfaces;
using MapChooserSharp.Modules.McsMenu.VoteMenu.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TNCSSPluginFoundation;
using TNCSSPluginFoundation.Interfaces;
using ZLinq;

namespace MapChooserSharp.Modules.McsMenu.VoteMenu.Cs2MenuManager.MenuSystem;

public class McsCs2MenuManagerMenuSystemUi(CCSPlayerController playerController, IServiceProvider provider): IMcsMapVoteUserInterface
{
    private IMcsGeneralMenuOption? _mcsGeneralMenuOption;
    
    private List<IMcsVoteOption> _voteOptions = new();

    private bool IsMenuShuffleEnabled { get; set; }

    private readonly TncssPluginBase _plugin = provider.GetRequiredService<TncssPluginBase>();
    
    private readonly IDebugLogger _debugLogger = provider.GetRequiredService<IDebugLogger>();
    
    private readonly IMcsInternalMapVoteControllerApi _voteController = provider.GetRequiredService<IMcsInternalMapVoteControllerApi>();
    
    private readonly Dictionary<int, List<MenuSystemOption>> _cachedMenuOptions = new();

    public McsSupportedMenuType McsMenuType { get; } = McsSupportedMenuType.Cs2MenuManagerMenuSystem;

    public int VoteOptionCount => _voteOptions.Count;
    
    public void OpenMenu()
    {
        if (_voteController.CurrentVoteState != McsMapVoteState.Voting && _voteController.CurrentVoteState != McsMapVoteState.RunoffVoting)
            return;

        // Unused variable, but it required to decide what language should use in menu.
        using var tempLang = new WithTemporaryCulture(playerController.GetLanguage());
        
        StringBuilder menuTitle = new();

        if (_mcsGeneralMenuOption != null && _mcsGeneralMenuOption.MenuTitle != string.Empty)
        {
            // TODO() If map countdown setting is verbose, then use verbose title.
            menuTitle.Append(_plugin.LocalizeStringForPlayer(playerController, _mcsGeneralMenuOption.MenuTitle + ".Html"));
        }
        else
        {
            menuTitle.Append(_plugin.LocalizeStringForPlayer(playerController,"General.Menu.Title" + ".Html"));
        }
        
        _debugLogger.LogTrace($"[Player {playerController.PlayerName}] Creating vote menu");
        var menu = MenuManager.CreateMenu<CS2MenuManager.API.Menu.MenuSystemSharpMenu>(menuTitle.ToString(), _plugin);

        // If menu option is already exists (this is intended for !revote feature)
        if (_cachedMenuOptions.TryGetValue(playerController.Slot, out var menuOps))
        {
            _debugLogger.LogTrace($"[Player {playerController.PlayerName}] vote menu menu is already cached, reusing...");
            foreach (var option in menuOps)
            {
                menu.AddItem(option.Text, (player, menuOption) => option.Callback(player, option));
            }
            DisplayMenu(playerController, menu);
            return;
        }

        _debugLogger.LogTrace($"[Player {playerController.PlayerName}] has no cached menu, creating menu...");
        List<MenuSystemOption> menuOptions = new();

        foreach (var (option, index) in _voteOptions.Select((value, i) => (value, i)))
        {
            string optionText = option.OptionText
                // If string contains Extend placeholder, then replace it.
                .Replace(_voteController.PlaceHolderExtendMap,
                    _plugin.LocalizeStringForPlayer(playerController, "Word.ExtendMap"))
                // If string contains Don't change placeholder, then replace it.
                .Replace(_voteController.PlaceHolderDontChangeMap,
                    _plugin.LocalizeStringForPlayer(playerController, "Word.DontChangeMap"));
            
            var menuOption = new MenuSystemOption(optionText, (player, menuOpt) =>
            {
                _voteOptions[(byte)index].VoteCallback.Invoke(playerController, (byte)index);
            });
            
            menuOptions.Add(menuOption);
        }

        if (IsMenuShuffleEnabled)
        {
            _debugLogger.LogTrace($"[Player {playerController.PlayerName}] Shuffling enabled...");
            Random random = Random.Shared;
            menuOptions = menuOptions.OrderBy(x => random.Next()).ToList();
        }
        
        foreach (var option in menuOptions)
        {
            menu.AddItem(option.Text, (player, menuOption) => option.Callback(player, option));
        }
        
        _cachedMenuOptions.TryAdd(playerController.Slot, menuOptions);
        
        _debugLogger.LogTrace($"[Player {playerController.PlayerName}] Menu init completed, opening...");
        
        DisplayMenu(playerController, menu);
    }

    public void CloseMenu()
    {
        // Because CS2MenuManager doesn't have foolproof so check here
        // Also playerController.IsValid is crashes server, so we'll use try catch
        try
        {
            MenuManager.CloseActiveMenu(playerController);
        }
        catch (Exception e)
        {
            _plugin.Logger.LogError($"CS2MenuManager MenuSystem UI method {nameof(CloseMenu)} has thrown an exception: {e}");
        }
    }

    public void SetVoteOptions(List<IMcsVoteOption> voteOptions)
    {
        _voteOptions = voteOptions;
    }

    public void SetMenuOption(IMcsGeneralMenuOption option)
    {
        _mcsGeneralMenuOption = option;
    }

    // We don't implement this feature in this type, due to lack of UX
    public void RefreshTitleCountdown(int count) {}

    public void SetRandomShuffle(bool enableShuffle)
    {
        IsMenuShuffleEnabled = enableShuffle;
    }

    private void DisplayMenu(CCSPlayerController player, CS2MenuManager.API.Menu.MenuSystemSharpMenu menu)
    {
        menu.Display(playerController, _voteController.VoteEndTime);
    }
}

// Helper class for menu options
public class MenuSystemOption
{
    public string Text { get; }
    public Action<CCSPlayerController, MenuSystemOption> Callback { get; }
    
    public MenuSystemOption(string text, Action<CCSPlayerController, MenuSystemOption> callback)
    {
        Text = text;
        Callback = callback;
    }
}