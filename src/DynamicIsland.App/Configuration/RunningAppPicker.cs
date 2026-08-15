using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DynamicIsland.Interop;

namespace DynamicIsland.Configuration;

/// <summary>
/// Выбор приложения из запущенных.
///
/// Вводить имя процесса руками — значит знать, что Edge это msedge, а
/// Яндекс.Браузер — browser. Проще показать список того, что открыто прямо
/// сейчас, вместе с заголовками окон.
/// </summary>
internal static class RunningAppPicker
{
    private sealed record Entry(string Process, string Title)
    {
        public override string ToString() => Title.Length > 0 ? $"{Process} — {Title}" : Process;
    }

    /// <summary>Возвращает имя процесса или null, если выбор отменён.</summary>
    public static string? Pick(Window owner)
    {
        var entries = Collect();

        var list = new ListBox
        {
            Background = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x2E)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF3)),
            BorderThickness = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 12),
        };
        foreach (var entry in entries) list.Items.Add(entry);
        if (list.Items.Count > 0) list.SelectedIndex = 0;

        string? result = null;

        var ok = new Button { Content = "Добавить", Width = 110, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Отмена", Width = 110, Height = 30 };

        var dialog = new Window
        {
            Title = "Запущенные приложения",
            Width = 520,
            Height = 460,
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x24)),
        };

        ok.Click += (_, _) =>
        {
            result = (list.SelectedItem as Entry)?.Process;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        list.MouseDoubleClick += (_, _) =>
        {
            if (list.SelectedItem is Entry e) { result = e.Process; dialog.Close(); }
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { ok, cancel },
        };

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(list, 0);
        Grid.SetRow(buttons, 1);
        grid.Children.Add(list);
        grid.Children.Add(buttons);

        dialog.Content = grid;
        dialog.ShowDialog();

        return result;
    }

    private static List<Entry> Collect()
    {
        var found = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        Win32.EnumWindows((window, _) =>
        {
            if (!Win32.IsWindowVisible(window)) return true;

            var length = Win32.GetWindowTextLengthW(window);
            if (length == 0) return true;

            var buffer = new StringBuilder(length + 1);
            Win32.GetWindowTextW(window, buffer, buffer.Capacity);
            var title = buffer.ToString().Trim();
            if (title.Length == 0) return true;

            try
            {
                Win32.GetWindowThreadProcessId(window, out var pid);
                if (pid == 0) return true;

                using var process = Process.GetProcessById((int)pid);
                var name = process.ProcessName.ToLowerInvariant();

                // Себя и служебные оболочки в списке показывать незачем.
                if (name is "dynamicisland" or "explorer" or "applicationframehost" or "textinputhost")
                    return true;

                // Одно приложение — одна строка, берём первое встреченное окно.
                found.TryAdd(name, new Entry(name, title));
            }
            catch { /* процесс мог закрыться прямо сейчас */ }

            return true;
        }, 0);

        return found.Values.OrderBy(e => e.Process, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
