using CounterStrikeSharp.API.Core;
using MapChooserSharp.Modules.McsMenu.VoteMenu.Interfaces;

namespace MapChooserSharp.Modules.McsMenu.VoteMenu.Cs2MenuManager.MenuSystem;

public class McsCs2MenuManagerMenuSystemUiFactory(IServiceProvider provider) : IMcsMapVoteUiFactory
{
    public int MaxMenuElements { get; } = 7;
    
    public IMcsMapVoteUserInterface Create(CCSPlayerController player)
    {
        return new McsCs2MenuManagerMenuSystemUi(player, provider);
    }
}