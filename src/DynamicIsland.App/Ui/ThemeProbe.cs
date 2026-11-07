using Microsoft.Win32;

namespace DynamicIsland.Ui;

/// <summary>Какая тема приложений сейчас в Windows. Читается из реестра раз в несколько секунд.</summary>
internal static class ThemeProbe
{
    private static bool _light;
    private static long _checkedAt = -10_000;

    public static bool AppsUseLightTheme()
    {
        var now = Environment.TickCount64;
        if (now - _checkedAt < 3000) return _light;
        _checkedAt = now;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            _light = key?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch { _light = false; }
        return _light;
    }
}
