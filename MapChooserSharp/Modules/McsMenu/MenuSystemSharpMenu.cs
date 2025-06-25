using CounterStrikeSharp.API.Core;
using CS2MenuManager.API;
using CS2MenuManager.API.Class;
using TNCSSPluginFoundation;

namespace MapChooserSharp.Modules.McsMenu;

public static class MenuSystemSharpHelper
{
    public static CS2MenuManager.API.Menu.MenuSystemSharpMenu CreateMenu(string title, TncssPluginBase plugin, string position = "center")
    {
        return MenuManager.CreateMenu<CS2MenuManager.API.Menu.MenuSystemSharpMenu>(title, plugin);
    }
}