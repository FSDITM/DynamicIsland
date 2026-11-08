using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DynamicIsland.Configuration;

/// <summary>
/// Выбор цвета: квадрат насыщенности и яркости, полоса оттенка, поле #RRGGBB и готовая палитра.
/// Открывается по нажатию на образец цвета и применяет выбор сразу, как и остальные настройки.
/// </summary>
internal sealed class ColorPickerPopup
{
    private const double AreaWidth = 224;
    private const double AreaHeight = 150;

    /// <summary>Палитра подобрана под тёмный островок: светлые, приглушённые и несколько ярких.</summary>
    private static readonly uint[] Presets =
    [
        0xFFF0EDE6, 0xFFFFFFFF, 0xFFA7A39A, 0xFF5A564E, 0xFF1A1816, 0xFF0A0A0C, 0xFFFF5A52, 0xFFFF9F43,
        0xFFFFD166, 0xFF7BD88F, 0xFF2EC4B6, 0xFF2AABEE, 0xFF5B7CFA, 0xFFB388FF, 0xFFFF7EB6, 0xFFE8D5B5,
    ];

    private readonly Popup _popup;
    private readonly Action<uint> _apply;
    private readonly GradientStop _hueStop = new(Colors.Red, 1);
    private readonly Ellipse _areaMarker;
    private readonly Border _hueMarker;
    private readonly TextBox _hex;

    private double _h;
    private double _s = 1;
    private double _v = 1;
    private bool _syncing;
    private long _closedAt;

    private static Brush Solid(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));

    private ColorPickerPopup(FrameworkElement target, Action<uint> apply, Func<uint> current, bool allowAuto)
    {
        _apply = apply;

        // ---- квадрат насыщенности и яркости ----
        var area = new Grid { Width = AreaWidth, Height = AreaHeight, Cursor = Cursors.Cross, Background = Brushes.Transparent };
        var clip = new Border { CornerRadius = new CornerRadius(4), ClipToBounds = true };
        var layers = new Grid();
        layers.Children.Add(new Rectangle
        {
            Fill = new LinearGradientBrush(
                new GradientStopCollection { new(Colors.White, 0), _hueStop },
                new Point(0, 0), new Point(1, 0)),
        });
        layers.Children.Add(new Rectangle
        {
            Fill = new LinearGradientBrush(
                new GradientStopCollection { new(Colors.Transparent, 0), new(Colors.Black, 1) },
                new Point(0, 0), new Point(0, 1)),
        });
        clip.Child = layers;
        area.Children.Add(clip);

        _areaMarker = new Ellipse
        {
            Width = 14, Height = 14, Stroke = Brushes.White, StrokeThickness = 2,
            Fill = Brushes.Transparent, IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 3, ShadowDepth = 0, Opacity = 0.7 },
        };
        area.Children.Add(_areaMarker);
        Drag(area, p =>
        {
            _s = Math.Clamp(p.X / AreaWidth, 0, 1);
            _v = 1 - Math.Clamp(p.Y / AreaHeight, 0, 1);
            Changed();
        });

        // ---- полоса оттенка ----
        var hue = new Grid { Width = AreaWidth, Height = 16, Margin = new Thickness(0, 10, 0, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent };
        var rainbow = new GradientStopCollection();
        for (var i = 0; i <= 6; i++) rainbow.Add(new GradientStop(HsvToColor(i * 60, 1, 1), i / 6.0));
        hue.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = new LinearGradientBrush(rainbow, new Point(0, 0), new Point(1, 0)),
        });
        _hueMarker = new Border
        {
            Width = 6, Background = Brushes.White, CornerRadius = new CornerRadius(3), IsHitTestVisible = false,
            BorderBrush = Solid(0x20, 0x1E, 0x1B), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        hue.Children.Add(_hueMarker);
        Drag(hue, p =>
        {
            _h = Math.Clamp(p.X / AreaWidth, 0, 1) * 360;
            Changed();
        });

        // ---- поле с кодом цвета ----
        _hex = new TextBox
        {
            Width = 130, Padding = new Thickness(8, 5, 8, 5), FontSize = 13,
            Background = Solid(0x22, 0x20, 0x1C), Foreground = Solid(0xF0, 0xED, 0xE6), MaxLength = 9,
        };
        _hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitHex(); };
        _hex.LostFocus += (_, _) => CommitHex();

        var hexRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        hexRow.Children.Add(_hex);
        if (allowAuto)
        {
            var auto = new Button
            {
                Content = "Авто", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 5, 12, 5), FontSize = 13,
                Cursor = Cursors.Hand,
            };
            if (target.TryFindResource("Action") is Style style) auto.Style = style;
            auto.Click += (_, _) => { _apply(0); _popup!.IsOpen = false; };
            hexRow.Children.Add(auto);
        }

        // ---- палитра ----
        var palette = new WrapPanel { Width = AreaWidth, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var color in Presets)
        {
            var c = color;
            var chip = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 4, 4),
                Background = new SolidColorBrush(ToColor(c)), Cursor = Cursors.Hand,
                BorderBrush = Solid(0x55, 0x50, 0x48), BorderThickness = new Thickness(1),
            };
            chip.MouseLeftButtonDown += (_, _) => { SetFrom(c); _apply(c); };
            palette.Children.Add(chip);
        }

        var stack = new StackPanel { Margin = new Thickness(12) };
        stack.Children.Add(area);
        stack.Children.Add(hue);
        stack.Children.Add(hexRow);
        stack.Children.Add(palette);

        _popup = new Popup
        {
            PlacementTarget = target,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = new Border
            {
                Background = Solid(0x17, 0x15, 0x13), CornerRadius = new CornerRadius(6), Margin = new Thickness(8, 6, 14, 14),
                BorderBrush = Solid(0x2A, 0x27, 0x23), BorderThickness = new Thickness(1),
                Child = stack,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.5, Color = Colors.Black },
            },
        };
        System.Windows.Documents.TextElement.SetForeground(_popup.Child, Solid(0xF0, 0xED, 0xE6));
        _popup.Closed += (_, _) => _closedAt = Environment.TickCount64;

        target.Cursor = Cursors.Hand;
        target.MouseLeftButtonUp += (_, _) =>
        {
            // Нажатие на образец при открытом окне закрывает его само; без этой проверки оно тут же открылось бы снова.
            if (_popup.IsOpen || Environment.TickCount64 - _closedAt < 200) return;
            SetFrom(current());
            _popup.IsOpen = true;
        };
    }

    /// <summary>Подключает выбор цвета к образцу; всплывающее окно кладётся рядом, чтобы получить стили окна.</summary>
    public static void Attach(Border target, Panel host, Action<uint> apply, Func<uint> current, bool allowAuto)
    {
        var picker = new ColorPickerPopup(target, apply, current, allowAuto);
        host.Children.Add(picker._popup);
    }

    private static void Drag(UIElement element, Action<Point> move)
    {
        element.MouseLeftButtonDown += (_, e) => { element.CaptureMouse(); move(e.GetPosition(element)); };
        element.MouseMove += (_, e) => { if (element.IsMouseCaptured) move(e.GetPosition(element)); };
        element.MouseLeftButtonUp += (_, _) => element.ReleaseMouseCapture();
    }

    private void Changed()
    {
        Refresh();
        _apply(ToArgb());
    }

    private void CommitHex()
    {
        if (_syncing) return;
        var text = _hex.Text.Trim().TrimStart('#');
        if (text.Length == 6) text = "FF" + text;
        if (text.Length == 8 && uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var value))
        {
            SetFrom(value);
            _apply(value);
        }
        else
        {
            Refresh();
        }
    }

    private void SetFrom(uint argb)
    {
        // Ноль — «авто»: в палитре показываем белый, чтобы было с чего начать.
        var color = argb == 0 ? Colors.White : ToColor(argb);
        RgbToHsv(color, out _h, out _s, out _v);
        Refresh();
    }

    private void Refresh()
    {
        _syncing = true;
        _hueStop.Color = HsvToColor(_h, 1, 1);
        _areaMarker.Margin = new Thickness(_s * AreaWidth - 7, (1 - _v) * AreaHeight - 7, 0, 0);
        _hueMarker.Margin = new Thickness(Math.Clamp(_h / 360 * AreaWidth - 3, 0, AreaWidth - 6), -2, 0, -2);
        _hex.Text = "#" + ToArgb().ToString("X8");
        _syncing = false;
    }

    private uint ToArgb()
    {
        var c = HsvToColor(_h, _s, _v);
        return 0xFF000000u | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
    }

    private static Color ToColor(uint v) => Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);

    private static Color HsvToColor(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360;
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    private static void RgbToHsv(Color color, out double h, out double s, out double v)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        v = max;
        s = max == 0 ? 0 : d / max;
        if (d == 0) h = 0;
        else if (max == r) h = 60 * (((g - b) / d % 6 + 6) % 6);
        else if (max == g) h = 60 * ((b - r) / d + 2);
        else h = 60 * ((r - g) / d + 4);
    }
}
