using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.IO.Path;
using ArrowPath = System.Windows.Shapes.Path;
using DynamicIsland.Configuration;
using DynamicIsland.Features;
using DynamicIsland.Interop;
using DynamicIsland.Platform;
using DynamicIsland.Ui.Tabs;

namespace DynamicIsland.Ui;

/// <summary>Редактор снимка: стрелки, рамки, маркер, текст, размытие личных данных.</summary>
internal static class MarkupHost
{
    public static void Open(BitmapSource image, Settings settings, ClipboardService clipboard, Action<string, byte[]> saved) =>
        UiThread.Post(() => new MarkupWindow(image, settings, clipboard, saved).ShowEditor());
}

internal sealed class MarkupWindow : Window
{
    private enum Tool { Arrow, Box, Marker, Text, Blur }

    private static readonly Brush Bg = Freeze(new SolidColorBrush(Color.FromRgb(0x10, 0x0F, 0x0D)));
    private static readonly Brush Edge = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush Primary = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0xED, 0xE6)));
    private static readonly Brush ChipBg = Freeze(new SolidColorBrush(Color.FromArgb(0x22, 0xF0, 0xED, 0xE6)));
    private static readonly Brush Active = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0xED, 0xE6)));
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private static T Freeze<T>(T b) where T : Freezable { b.Freeze(); return b; }

    private static readonly Color[] Palette =
    [
        Color.FromRgb(0xFF, 0x45, 0x3A), Color.FromRgb(0xFF, 0xD6, 0x0A), Color.FromRgb(0x30, 0xD1, 0x58),
        Color.FromRgb(0x0A, 0x84, 0xFF), Colors.White, Colors.Black,
    ];

    private readonly BitmapSource _image;
    private readonly Settings _settings;
    private readonly ClipboardService _clipboard;
    private readonly Action<string, byte[]> _saved;
    private readonly Canvas _canvas = new();
    private readonly List<UIElement> _undo = [];
    private readonly Dictionary<Tool, Button> _toolButtons = [];
    private readonly List<Border> _swatches = [];

    private Tool _tool = Tool.Arrow;
    private Color _color = Palette[0];
    private Point _origin;
    private UIElement? _current;
    private bool _drawing;
    private bool _finished;

    private double Unit => Math.Clamp(_image.PixelWidth / 900.0, 1.0, 3.0);

    public MarkupWindow(BitmapSource image, Settings settings, ClipboardService clipboard, Action<string, byte[]> saved)
    {
        _image = image;
        _settings = settings;
        _clipboard = clipboard;
        _saved = saved;

        Title = "Снимок — DynamicIsland";
        DarkTitleBar.Apply(this);
        Background = Bg;
        Foreground = Primary;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var area = SystemParameters.WorkArea;
        Width = Math.Min(Math.Max(image.PixelWidth + 40, 860), area.Width * 0.92);
        Height = Math.Min(image.PixelHeight + 130, area.Height * 0.92);

        _canvas.Width = image.PixelWidth;
        _canvas.Height = image.PixelHeight;
        _canvas.Background = new ImageBrush(image) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
        _canvas.Cursor = Cursors.Cross;
        _canvas.MouseLeftButtonDown += OnDown;
        _canvas.MouseMove += OnMove;
        _canvas.MouseLeftButtonUp += OnUp;

        var bar = new WrapPanel { Margin = new Thickness(12, 10, 12, 8) };
        AddTool(bar, Tool.Arrow, "", "Стрелка");
        AddTool(bar, Tool.Box, "", "Рамка");
        AddTool(bar, Tool.Marker, "", "Маркер");
        AddTool(bar, Tool.Text, "", "Текст");
        AddTool(bar, Tool.Blur, "", "Размыть личное");
        bar.Children.Add(new Border { Width = 14 });

        foreach (var c in Palette)
        {
            var sw = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = new SolidColorBrush(c),
                BorderThickness = new Thickness(2), Margin = new Thickness(0, 4, 5, 0), Cursor = Cursors.Hand, Tag = c,
            };
            sw.MouseLeftButtonUp += (_, _) => { _color = c; RefreshState(); };
            _swatches.Add(sw);
            bar.Children.Add(sw);
        }

        bar.Children.Add(new Border { Width = 14 });
        bar.Children.Add(MakeButton("", "Отменить (Ctrl+Z)", UndoLast));
        bar.Children.Add(new Border { Width = 14 });
        bar.Children.Add(TextButton("Копировать", () => Export(copy: true, save: false, close: false)));
        bar.Children.Add(TextButton("Сохранить", () => Export(copy: false, save: true, close: false)));
        bar.Children.Add(TextButton("Готово", () => Export(copy: true, save: true, close: true), primary: true));

        var viewbox = new Viewbox { Child = _canvas, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(12, 0, 12, 12) };

        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);
        dock.Children.Add(viewbox);
        Content = dock;

        RefreshState();

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && Keyboard.FocusedElement is not TextBox) Close();
            else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) UndoLast();
            else if (e.Key == Key.Enter && Keyboard.FocusedElement is not TextBox) Export(true, true, true);
        };
    }

    public void ShowEditor()
    {
        Show();
        Activate();
        Native.ForceForeground(new WindowInteropHelper(this).Handle);
    }

    private Button MakeButton(string glyph, string tip, Action click)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 15, Foreground = Primary },
            Width = 36, Height = 30, Margin = new Thickness(0, 0, 4, 0), ToolTip = tip,
            Background = ChipBg, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, Focusable = false,
        };
        b.Click += (_, _) => click();
        return b;
    }

    private Button TextButton(string text, Action click, bool primary = false)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = primary ? Brushes.Black : Primary, FontFamily = new FontFamily("Segoe UI") },
            Height = 30, Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(0, 0, 4, 0),
            Background = primary ? Active : ChipBg, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, Focusable = false,
        };
        b.Click += (_, _) => click();
        return b;
    }

    private void AddTool(Panel host, Tool tool, string glyph, string tip)
    {
        var b = MakeButton(glyph, tip, () => { _tool = tool; RefreshState(); });
        _toolButtons[tool] = b;
        host.Children.Add(b);
    }

    private void RefreshState()
    {
        foreach (var (tool, button) in _toolButtons)
            button.Background = tool == _tool ? new SolidColorBrush(Color.FromArgb(0x50, 0xF0, 0xED, 0xE6)) : ChipBg;
        foreach (var sw in _swatches)
            sw.BorderBrush = (Color)sw.Tag == _color ? Brushes.White : Brushes.Transparent;
    }

    private Brush ColorBrush(double opacity = 1) => new SolidColorBrush(_color) { Opacity = opacity };

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (_finished) return;
        _origin = e.GetPosition(_canvas);

        if (_tool == Tool.Text)
        {
            PlaceTextBox(_origin);
            e.Handled = true;
            return;
        }

        _drawing = true;
        _canvas.CaptureMouse();

        switch (_tool)
        {
            case Tool.Arrow:
                _current = new ArrowPath { Stroke = ColorBrush(), Fill = ColorBrush(), StrokeThickness = 4 * Unit, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round };
                break;
            case Tool.Box:
                _current = new Rectangle { Stroke = ColorBrush(), StrokeThickness = 3.5 * Unit, RadiusX = 3 * Unit, RadiusY = 3 * Unit };
                break;
            case Tool.Marker:
                _current = new Rectangle { Fill = ColorBrush(0.38), RadiusX = 2, RadiusY = 2 };
                break;
            case Tool.Blur:
                _current = new Image { Stretch = Stretch.Fill };
                RenderOptions.SetBitmapScalingMode((Image)_current, BitmapScalingMode.NearestNeighbor);
                break;
        }

        if (_current is not null) _canvas.Children.Add(_current);
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_drawing || _current is null) return;
        var p = e.GetPosition(_canvas);
        p = new Point(Math.Clamp(p.X, 0, _canvas.Width), Math.Clamp(p.Y, 0, _canvas.Height));

        if (_current is ArrowPath arrow)
        {
            arrow.Data = ArrowGeometry(_origin, p, 14 * Unit);
            return;
        }

        var rect = new Rect(_origin, p);
        Canvas.SetLeft(_current, rect.Left);
        Canvas.SetTop(_current, rect.Top);
        if (_current is FrameworkElement fe)
        {
            fe.Width = rect.Width;
            fe.Height = rect.Height;
        }
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_drawing) return;
        _drawing = false;
        _canvas.ReleaseMouseCapture();

        if (_current is null) return;
        var p = e.GetPosition(_canvas);
        var tiny = Math.Abs(p.X - _origin.X) < 5 && Math.Abs(p.Y - _origin.Y) < 5;

        if (tiny)
        {
            _canvas.Children.Remove(_current);
            _current = null;
            return;
        }

        if (_current is Image blur)
        {
            var rect = new Int32Rect(
                (int)Math.Clamp(Math.Min(_origin.X, p.X), 0, _image.PixelWidth - 2),
                (int)Math.Clamp(Math.Min(_origin.Y, p.Y), 0, _image.PixelHeight - 2),
                (int)Math.Max(2, Math.Abs(p.X - _origin.X)), (int)Math.Max(2, Math.Abs(p.Y - _origin.Y)));
            rect.Width = Math.Min(rect.Width, _image.PixelWidth - rect.X);
            rect.Height = Math.Min(rect.Height, _image.PixelHeight - rect.Y);
            blur.Source = Pixelate(rect);
        }

        _undo.Add(_current);
        _current = null;
    }

    /// <summary>Пикселизация области: уменьшаем и растягиваем без сглаживания.</summary>
    private BitmapSource Pixelate(Int32Rect rect)
    {
        var crop = new CroppedBitmap(_image, rect);
        var block = Math.Max(8, (int)(10 * Unit));
        var k = 1.0 / block;
        var small = new TransformedBitmap(crop, new ScaleTransform(k, k));
        small.Freeze();
        return small;
    }

    private static Geometry ArrowGeometry(Point from, Point to, double head)
    {
        var dir = to - from;
        if (dir.Length < 1) return Geometry.Empty;
        dir.Normalize();
        var normal = new Vector(-dir.Y, dir.X);
        var tip = to;
        var baseCenter = to - dir * head;

        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(from, false, false);
            ctx.LineTo(baseCenter, true, true);
            ctx.BeginFigure(tip, true, true);
            ctx.LineTo(baseCenter + normal * head * 0.55, true, true);
            ctx.LineTo(baseCenter - normal * head * 0.55, true, true);
        }
        g.Freeze();
        return g;
    }

    private void PlaceTextBox(Point at)
    {
        var size = Math.Max(18, _image.PixelHeight / 28.0);
        var box = new TextBox
        {
            MinWidth = 60, FontSize = size, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(_color),
            CaretBrush = new SolidColorBrush(_color), Background = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)),
            BorderThickness = new Thickness(1), BorderBrush = Active, Padding = new Thickness(2),
            AcceptsReturn = false,
        };
        Canvas.SetLeft(box, at.X);
        Canvas.SetTop(box, at.Y - size / 2);
        _canvas.Children.Add(box);

        void Commit()
        {
            if (!_canvas.Children.Contains(box)) return;
            _canvas.Children.Remove(box);
            if (string.IsNullOrWhiteSpace(box.Text)) return;

            var label = new TextBlock
            {
                Text = box.Text, FontSize = size, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(_color),
                FontFamily = new FontFamily("Segoe UI"),
            };
            Canvas.SetLeft(label, at.X + 3);
            Canvas.SetTop(label, at.Y - size / 2 + 2);
            _canvas.Children.Add(label);
            _undo.Add(label);
        }

        box.LostKeyboardFocus += (_, _) => Commit();
        _pendingText = Commit;
        box.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape) { Commit(); _canvas.Focus(); e.Handled = true; }
        };
        box.Loaded += (_, _) => box.Focus();
    }

    private void UndoLast()
    {
        if (_undo.Count == 0) return;
        _canvas.Children.Remove(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
    }

    /// <summary>Надпись, которую ещё печатают: перед сохранением её надо дописать в картинку.</summary>
    private Action? _pendingText;

    private byte[] Render()
    {
        // Canvas фокус не принимает, и раньше открытое поле надписи попадало в PNG как есть —
        // с рамкой и полупрозрачным фоном. Завершаем его явно.
        _pendingText?.Invoke();
        _pendingText = null;
        _canvas.Measure(new Size(_canvas.Width, _canvas.Height));
        _canvas.Arrange(new Rect(0, 0, _canvas.Width, _canvas.Height));

        var rtb = new RenderTargetBitmap(_image.PixelWidth, _image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(_canvas);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private void Export(bool copy, bool save, bool close)
    {
        var png = Render();
        string? path = null;

        if (copy) _clipboard.SetImage(png);

        if (save)
        {
            try
            {
                path = Path.Combine(ScreenshotPaths.Folder(_settings), $"Скриншот {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png");
                File.WriteAllBytes(path, png);
            }
            catch (Exception ex) { Log.Write("Снимок не сохранён: " + ex.Message); path = null; }
        }

        _saved(path ?? "", png);
        if (close) { _finished = true; Close(); }
    }
}
