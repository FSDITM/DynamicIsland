using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DynamicIsland.Platform;

/// <summary>Тёмная рамка окна: светлая полоса заголовка над тёмным содержимым выглядит чужеродно.</summary>
internal static class DarkTitleBar
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window, int colorRef = 0x000B0C0D)
    {
        void Set()
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                var dark = 1;
                DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));          // тёмный режим заголовка
                DwmSetWindowAttribute(hwnd, 19, ref dark, sizeof(int));          // то же для старых сборок
                var caption = colorRef;
                DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));       // цвет заголовка (Windows 11)
                var text = 0x00E6EDF0;
                DwmSetWindowAttribute(hwnd, 36, ref text, sizeof(int));          // цвет текста заголовка
            }
            catch { /* необязательное украшение */ }
        }

        if (new WindowInteropHelper(window).Handle != 0) Set();
        else window.SourceInitialized += (_, _) => Set();
    }
}
