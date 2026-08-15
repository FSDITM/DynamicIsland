using System.IO;
using System.Runtime.InteropServices;
using DynamicIsland.Interop;

namespace DynamicIsland.Platform;

/// <summary>
/// Иконка в области уведомлений — на голом Shell_NotifyIcon, без WinForms.
/// Тянуть ради одной иконки весь System.Windows.Forms с его собственным
/// насосом сообщений в приложение, которое борется за миллисекунды, незачем.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private Win32.NOTIFYICONDATAW _data;
    private bool _added;
    private nint _icon;

    /// <summary>Путь к иконке приложения рядом с exe.</summary>
    public static string IconPath { get; } =
        Path.Combine(AppContext.BaseDirectory, "Resources", "AppIcon.ico");

    /// <summary>
    /// Иконка приложения нужного размера, либо системная, если файла нет.
    /// Размер трея зависит от масштаба экрана, поэтому просим системный
    /// «маленький» размер, а не жёсткие 16 пикселей.
    /// </summary>
    private static nint LoadAppIcon()
    {
        if (File.Exists(IconPath))
        {
            var icon = Win32.LoadImageW(0, IconPath, Win32.IMAGE_ICON, 0, 0,
                Win32.LR_LOADFROMFILE | Win32.LR_DEFAULTSIZE);
            if (icon != 0) return icon;

            Log.Write("Иконка трея не загрузилась: " + IconPath);
        }

        return Win32.LoadIconW(0, Win32.IDI_APPLICATION);
    }

    public void Add(nint hwnd, string tooltip)
    {
        _data = new Win32.NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<Win32.NOTIFYICONDATAW>(),
            hWnd = hwnd,
            uID = 1,
            uFlags = Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP,
            uCallbackMessage = Win32.WM_TRAYICON,
            hIcon = _icon = LoadAppIcon(),
            szTip = tooltip,
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };

        _added = Win32.Shell_NotifyIconW(Win32.NIM_ADD, ref _data);
    }

    public void SetTooltip(string tooltip)
    {
        if (!_added) return;
        _data.szTip = tooltip;
        _data.uFlags = Win32.NIF_TIP;
        Win32.Shell_NotifyIconW(Win32.NIM_MODIFY, ref _data);
    }

    /// <summary>
    /// Показывает контекстное меню у курсора и возвращает id выбранного пункта
    /// (0 — закрыли без выбора).
    /// </summary>
    public static int ShowMenu(nint hwnd, IReadOnlyList<(int Id, string Text, bool Checked)> items)
    {
        var menu = Win32.CreatePopupMenu();
        if (menu == 0) return 0;

        try
        {
            foreach (var (id, text, isChecked) in items)
            {
                if (id == 0) Win32.AppendMenuW(menu, Win32.MF_SEPARATOR, 0, null);
                else Win32.AppendMenuW(menu, Win32.MF_STRING | (isChecked ? Win32.MF_CHECKED : 0), (nuint)id, text);
            }

            Win32.GetCursorPos(out var pt);

            // Без этого меню не закроется по клику мимо него — требование TrackPopupMenu.
            Win32.SetForegroundWindow(hwnd);

            return Win32.TrackPopupMenu(menu, Win32.TPM_RIGHTBUTTON | Win32.TPM_RETURNCMD,
                pt.X, pt.Y, 0, hwnd, 0);
        }
        finally
        {
            Win32.DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (!_added) return;
        Win32.Shell_NotifyIconW(Win32.NIM_DELETE, ref _data);
        _added = false;

        if (_icon != 0)
        {
            Win32.DestroyIcon(_icon);
            _icon = 0;
        }
    }
}
