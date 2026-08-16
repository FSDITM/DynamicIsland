using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicIsland.Interop;

namespace DynamicIsland.Configuration;

/// <summary>Пункт меню в трее. Разделитель — запись с пустой подписью.</summary>
internal sealed record TrayMenuItem(string Text, Action? Action = null, bool? Checked = null, bool Danger = false)
{
    public static readonly TrayMenuItem Separator = new("");
    public bool IsSeparator => Text.Length == 0;
}

/// <summary>
/// Меню в трее, нарисованное самим приложением.
///
/// Системное меню Win32 всегда следует теме оформления Windows и рядом
/// с тёмным островком выглядит чужеродно: светлая плашка с прямыми углами.
/// Здесь то же оформление, что в настройках, — тёмная панель со скруглением,
/// подсветка под курсором и мягкая тень.
/// </summary>
internal static class TrayMenu
{
    private static readonly SolidColorBrush Panel = Freeze(0x1E, 0x1E, 0x25);
    private static readonly SolidColorBrush Hover = Freeze(0x2E, 0x2E, 0x38);
    private static readonly SolidColorBrush Text = Freeze(0xF2, 0xF2, 0xF5);
    private static readonly SolidColorBrush Muted = Freeze(0x9A, 0x9A, 0xA6);
    private static readonly SolidColorBrush Accent = Freeze(0x0A, 0x84, 0xFF);
    private static readonly SolidColorBrush Danger = Freeze(0xFF, 0x6B, 0x6B);
    private static readonly SolidColorBrush Line = Freeze(0x33, 0x33, 0x3D);

    private static SolidColorBrush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Window? _open;

    /// <summary>
    /// Показывает меню у курсора. Вызывать можно из любого потока — окно
    /// создаётся на потоке WPF.
    /// </summary>
    public static void Show(IReadOnlyList<TrayMenuItem> items)
    {
        var thread = new Thread(() =>
        {
            try
            {
                var window = Build(items);
                _open = window;
                window.Closed += (_, _) =>
                {
                    _open = null;
                    Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                };
                window.Show();
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                Log.Write("Меню в трее не открылось: " + ex);
                _open = null;
            }
        })
        {
            IsBackground = true,
            Name = "DynamicIsland.TrayMenu",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>Меню без показа у курсора — для снимка.</summary>
    public static Window BuildForPreview()
    {
        var window = Build(
        [
            new TrayMenuItem("Настройки…"),
            TrayMenuItem.Separator,
            new TrayMenuItem("Запускать при входе в систему", Checked: true),
            new TrayMenuItem("Показывать метрики", Checked: false),
            TrayMenuItem.Separator,
            new TrayMenuItem("Выход", Danger: true),
        ]);

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.ShowInTaskbar = false;
        return window;
    }

    public static void CloseIfOpen()
    {
        var window = _open;
        try { window?.Dispatcher.Invoke(window.Close, DispatcherPriority.Normal); }
        catch { /* поток мог уже завершиться */ }
    }

    private static Window Build(IReadOnlyList<TrayMenuItem> items)
    {
        var stack = new StackPanel { Margin = new Thickness(6) };

        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ResizeMode = ResizeMode.NoResize,
        };

        TextElement.SetForeground(window, Text);
        TextOptions.SetTextRenderingMode(window, TextRenderingMode.ClearType);

        foreach (var item in items)
        {
            if (item.IsSeparator)
            {
                stack.Children.Add(new Border
                {
                    Height = 1,
                    Background = Line,
                    Margin = new Thickness(12, 5, 12, 5),
                });
                continue;
            }

            stack.Children.Add(BuildRow(window, item));
        }

        // Тень рисуем сами: у окна без рамки её нет, а без неё панель
        // сливается с тем, что под ней.
        var panel = new Border
        {
            Background = Panel,
            CornerRadius = new CornerRadius(12),
            Child = stack,
            Margin = new Thickness(14),
            Effect = new DropShadowEffect
            {
                BlurRadius = 22,
                ShadowDepth = 4,
                Opacity = 0.55,
                Color = Colors.Black,
            },
        };

        window.Content = panel;

        // Меню закрывается, как только внимание ушло, — как системное.
        window.Deactivated += (_, _) => window.Close();
        window.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) window.Close(); };

        window.SourceInitialized += (_, _) => PlaceAtCursor(window);
        window.ContentRendered += (_, _) => { PlaceAtCursor(window); window.Activate(); };

        return window;
    }

    private static UIElement BuildRow(Window window, TrayMenuItem item)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Галочка занимает своё место всегда, иначе подписи прыгали бы
        // по горизонтали при переключении.
        if (item.Checked == true)
        {
            var check = new Path
            {
                Data = Geometry.Parse("M 0 4 L 3.4 7.4 L 9.5 0.8"),
                Stroke = Accent,
                StrokeThickness = 2,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(check, 0);
            row.Children.Add(check);
        }

        var label = new TextBlock
        {
            Text = item.Text,
            // Выключенный пункт остаётся обычного цвета: приглушённый читается
            // как недоступный, а он просто не отмечен. Галочки достаточно.
            Foreground = item.Danger ? Danger : Text,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);

        var cell = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(8, 9, 18, 9),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = row,
        };

        cell.MouseEnter += (_, _) => cell.Background = Hover;
        cell.MouseLeave += (_, _) => cell.Background = Brushes.Transparent;
        cell.MouseLeftButtonUp += (_, _) =>
        {
            window.Close();
            item.Action?.Invoke();
        };

        return cell;
    }

    /// <summary>
    /// Ставит меню у курсора так, чтобы оно не вылезло за край экрана.
    /// Иконка трея обычно внизу справа, поэтому меню раскрывается вверх и влево.
    /// </summary>
    private static void PlaceAtCursor(Window window)
    {
        if (!Win32.GetCursorPos(out var cursor)) return;

        var monitor = Win32.MonitorFromPoint(cursor, Win32.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!Win32.GetMonitorInfoW(monitor, ref info)) return;

        var source = PresentationSource.FromVisual(window);
        var toDevice = source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var scaleX = toDevice.M11 == 0 ? 1 : toDevice.M11;
        var scaleY = toDevice.M22 == 0 ? 1 : toDevice.M22;

        var widthPx = window.ActualWidth * scaleX;
        var heightPx = window.ActualHeight * scaleY;

        // Курсор — правый нижний угол панели; тень занимает поля Margin,
        // поэтому её ширину прибавляем обратно.
        var left = cursor.X - widthPx + 14 * scaleX;
        var top = cursor.Y - heightPx + 14 * scaleY;

        left = Math.Clamp(left, info.rcWork.Left, Math.Max(info.rcWork.Left, info.rcWork.Right - widthPx));
        top = Math.Clamp(top, info.rcWork.Top, Math.Max(info.rcWork.Top, info.rcWork.Bottom - heightPx));

        window.Left = left / scaleX;
        window.Top = top / scaleY;
    }
}
