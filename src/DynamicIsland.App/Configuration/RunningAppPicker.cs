using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
        /// <summary>
        /// Заголовок окна нужен только чтобы узнать приложение в лицо, поэтому
        /// он подрезается. Заодно это уберегает от лишнего: в заголовках бывают
        /// адреса почты и имена личных файлов, а этот список могут показать
        /// на скриншоте.
        /// </summary>
        private const int TitleLimit = 42;

        public override string ToString()
        {
            if (Title.Length == 0) return Process;

            var title = Title.Length > TitleLimit
                ? string.Concat(Title.AsSpan(0, TitleLimit).TrimEnd(), "…")
                : Title;

            return $"{Process} — {title}";
        }
    }

    /// <summary>Диалог без показа — нужен, чтобы снять с него снимок для проверки.</summary>
    public static Window BuildForPreview() => Build(null, out _);

    /// <summary>Возвращает имя процесса или null, если выбор отменён.</summary>
    public static string? Pick(Window owner)
    {
        var dialog = Build(owner, out var result);
        dialog.ShowDialog();
        return result.Value;
    }

    private static Window Build(Window? owner, out StrongBox<string?> outcome)
    {
        var entries = Collect();

        var list = new ListBox
        {
            Background = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x2E)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF3)),
            BorderThickness = new Thickness(0),
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 12),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        foreach (var entry in entries) list.Items.Add(entry);
        if (list.Items.Count > 0) list.SelectedIndex = 0;

        var result = new StrongBox<string?>(null);
        outcome = result;

        var ok = DarkButton("Добавить", accent: true);
        var cancel = DarkButton("Отмена", accent: false);

        var dialog = new Window
        {
            Title = "Запущенные приложения",
            Width = 520,
            Height = 460,
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x24)),
        };

        // Диалог — отдельное окно и ресурсы настроек не наследует, поэтому
        // цвет текста задаётся здесь: иначе он остался бы чёрным на тёмном фоне.
        TextElement.SetForeground(dialog, new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF5)));
        dialog.Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF5));
        TextOptions.SetTextFormattingMode(dialog, TextFormattingMode.Display);

        ok.Click += (_, _) =>
        {
            result.Value = (list.SelectedItem as Entry)?.Process;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        list.MouseDoubleClick += (_, _) =>
        {
            if (list.SelectedItem is Entry e) { result.Value = e.Process; dialog.Close(); }
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
        return dialog;
    }

    /// <summary>Кнопка в тон окну: стандартная в тёмном диалоге выглядит чужеродно.</summary>
    private static Button DarkButton(string caption, bool accent)
    {
        var background = accent
            ? Color.FromRgb(0x0A, 0x84, 0xFF)
            : Color.FromRgb(0x2A, 0x2A, 0x33);

        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new SolidColorBrush(background));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        template.VisualTree = border;

        return new Button
        {
            Content = caption,
            Width = 120,
            Height = 34,
            Margin = new Thickness(8, 0, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF5)),
            FontSize = 14,
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = template,
        };
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
