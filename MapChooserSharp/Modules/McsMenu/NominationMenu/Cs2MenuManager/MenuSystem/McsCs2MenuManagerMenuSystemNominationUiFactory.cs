using CounterStrikeSharp.API.Core;
using MapChooserSharp.Modules.McsMenu.NominationMenu.Interfaces;

namespace MapChooserSharp.Modules.McsMenu.NominationMenu.Cs2MenuManager.MenuSystem;

public class McsCs2MenuManagerMenuSystemNominationUiFactory(IServiceProvider provider): IMcsNominationUiFactory
{
    public IMcsNominationUserInterface Create(CCSPlayerController player)
    {
        return new McsCs2MenuManagerMenuSystemNominationUi(player, provider);
    }
}