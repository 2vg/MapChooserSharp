using CounterStrikeSharp.API.Core;
using TNCSSPluginFoundation;
using MenuSystemSharp.API;
using Microsoft.Extensions.Logging;

namespace MapChooserSharp.Modules.McsMenu;

public static class MenuSystemSharpHelper
{
    /// <summary>
    /// 直接MenuSystemのAPIを使用してメニューを作成
    /// </summary>
    /// <param name="title">メニューのタイトル</param>
    /// <param name="plugin">プラグインインスタンス</param>
    /// <param name="position">位置（互換性のため残しているが使用されない）</param>
    /// <returns>作成されたメニューインスタンス</returns>
    public static IMenuInstance? CreateMenu(string title, TncssPluginBase plugin, string position = "center")
    {
        var menuSystem = MenuSystem.Instance;
        if (menuSystem == null || !menuSystem.IsAvailable)
        {
            plugin.Logger.LogError("MenuSystem is not available");
            return null;
        }

        var menu = menuSystem.CreateMenu();
        menu.Title = title;
        return menu;
    }
}
