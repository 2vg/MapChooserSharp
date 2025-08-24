using CounterStrikeSharp.API.Core;
using CS2MenuManager.API;
using CS2MenuManager.API.Class;
using TNCSSPluginFoundation;

namespace MapChooserSharp.Modules.McsMenu;

public static class MenuSystemSharpHelper
{
    // TODO: Back to MenuSystemSharpMenu when fixed mms2-menu_system
    public static CS2MenuManager.API.Menu.MenuSystemSharpMenu CreateMenu(string title, TncssPluginBase plugin, string position = "center")
    {
        // TODO: Back to MenuSystemSharpMenu when fixed mms2-menu_system
        return MenuManager.CreateMenu<CS2MenuManager.API.Menu.MenuSystemSharpMenu>(title, plugin);
    }
}