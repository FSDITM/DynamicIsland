using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using DynamicIsland.Features;
using DynamicIsland.Interop;
using DynamicIsland.Platform;

namespace DynamicIsland.Ui;

internal sealed record RegionResult(MonitorShot Shot, Int32Rect Rect);

/// <summary>Затемнённый «замороженный» экран: выделение области и пипетка. По окну на каждый монитор.</summary>
internal static class CaptureOverlay
{
    public static Task<RegionResult?> SelectRegionAsync(List<MonitorShot> shots, string hint = "Выделите область · Esc — отмена")
    {
        var tcs = new TaskCompletionSource<RegionResult?>();
        UiThread.Post(() => Open(shots, tcs, null, hint));
        return tcs.Task;
    }

    public static Task<System.Drawing.Color?> PickColorAsync(List<MonitorShot> shots)
    {
        var tcs = new TaskCompletionSource<RegionResult?>();
        var colorTcs = new TaskCompletionSource<System.Drawing.Color?>();
        UiThread.Post(() => Open(shots, tcs, colorTcs, "Кликните по цвету · Esc — отмена"));
        return colorTcs.Task;
    }

    private static void Open(List<MonitorShot> shots, TaskCompletionSource<RegionResult?> region,
                             TaskCompletionSource<System.Drawing.Color?>? color, string hint)
    {
        var windows = new List<ShotWindow>();

        void Finish(RegionResult? r, System.Drawing.Color? c)
        {
            // Сначала результат: Close ниже снова вызовет Finish через Closed, и он должен
            // застать уже завершённую задачу.
            region.TrySetResult(r);
            color?.TrySetResult(c);
            var open = windows.ToList();
            windows.Clear();
            foreach (var w in open) w.Close();
        }

        foreach (var shot in shots)
        {
            var w = new ShotWindow(shot, color is not null, hint, Finish);

            // Окно закрыли не нашими клавишами (Alt+F4, завершение сеанса): считаем это отменой.
            // Иначе задача не завершалась, и снимки, текст с экрана и запись переставали работать
            // до перезапуска островка.
            w.Closed += (_, _) => { if (windows.Count > 0) Finish(null, null); };
            windows.Add(w);
        }
        foreach (var w in windows) w.ShowOverlay();
        windows.FirstOrDefault(w => w.ContainsCursor())?.Focus();
    }

    private sealed class ShotWindow : Window
    {
        private static readonly Brush Dim = Freeze(new SolidColorBrush(Color.FromArgb(0x88, 0, 0, 0)));
        private static readonly Brush Accent = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0xED, 0xE6)));
        private static readonly Brush Chip = Freeze(new SolidColorBrush(Color.FromArgb(0xE6, 0x10, 0x0F, 0x0D)));

        private static T Freeze<T>(T b) where T : Freezable { b.Freeze(); return b; }

        private readonly MonitorShot _shot;
        private readonly bool _colorMode;
        private readonly Action<RegionResult?, System.Drawing.Color?> _finish;
        private readonly Canvas _canvas = new();
        private readonly Image _image = new();
        private readonly Path _dim = new() { Fill = Dim, IsHitTestVisible = false };
        private readonly Rectangle _frame = new() { Stroke = Accent, StrokeThickness = 1.5, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        private readonly Border _label = new() { Background = Chip, CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 4, 8, 4), IsHitTestVisible = false };
        private readonly TextBlock _labelText = new() { Foreground = Brushes.White, FontSize = 12, FontFamily = new FontFamily("Segoe UI") };
        private readonly Border _hintBar = new() { Background = Chip, CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 8, 14, 8), IsHitTestVisible = false };

        // Пипетка
        private readonly Border _loupe = new() { Width = 136, Height = 152, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        private readonly Image _loupeImage = new() { Width = 132, Height = 132, Stretch = Stretch.Fill };
        private readonly Rectangle _loupeCenter = new() { Width = 12, Height = 12, Stroke = Brushes.White, StrokeThickness = 1.5 };
        private readonly TextBlock _loupeText = new() { Foreground = Brushes.White, FontSize = 12, FontFamily = new FontFamily("Consolas"), HorizontalAlignment = HorizontalAlignment.Center };

        private Point? _start;
        private Rect _selection;

        public ShotWindow(MonitorShot shot, bool colorMode, string hint, Action<RegionResult?, System.Drawing.Color?> finish)
        {
            _shot = shot;
            _colorMode = colorMode;
            _finish = finish;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = false;
            ShowInTaskbar = false;
            Topmost = true;
            Background = Brushes.Black;
            Cursor = Cursors.Cross;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = shot.Bounds.Left;
            Top = shot.Bounds.Top;
            Width = shot.Bounds.Width;
            Height = shot.Bounds.Height;

            _image.Source = shot.Source;
            _image.Stretch = Stretch.Fill;
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);

            var root = new Grid();
            root.Children.Add(_image);
            root.Children.Add(_canvas);
            Content = root;

            _canvas.Children.Add(_dim);
            _canvas.Children.Add(_frame);
            _label.Child = _labelText;
            _label.Visibility = Visibility.Collapsed;
            _canvas.Children.Add(_label);

            _hintBar.Child = new TextBlock
            {
                Text = hint, Foreground = Brushes.White, FontSize = 13, FontFamily = new FontFamily("Segoe UI"),
            };
            _canvas.Children.Add(_hintBar);

            if (colorMode)
            {
                var stack = new StackPanel();
                stack.Children.Add(new Border
                {
                    Width = 132, Height = 132, CornerRadius = new CornerRadius(12), ClipToBounds = true,
                    BorderBrush = Brushes.White, BorderThickness = new Thickness(2),
                    Child = new Grid { Children = { _loupeImage, new Canvas { Children = { _loupeCenter } } } },
                });
                Canvas.SetLeft(_loupeCenter, 60);
                Canvas.SetTop(_loupeCenter, 60);
                _loupeText.Margin = new Thickness(0, 4, 0, 0);
                stack.Children.Add(new Border { Background = Chip, CornerRadius = new CornerRadius(8), Child = _loupeText, Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(6, 2, 6, 2) });
                _loupe.Child = stack;
                RenderOptions.SetBitmapScalingMode(_loupeImage, BitmapScalingMode.NearestNeighbor);
                _canvas.Children.Add(_loupe);
            }

            Loaded += (_, _) =>
            {
                UpdateDim();
                Canvas.SetLeft(_hintBar, (ActualWidth - 380) / 2);
                Canvas.SetTop(_hintBar, 26);
                // Подсказка показывается на том мониторе, где курсор.
                _hintBar.Visibility = ContainsCursor() ? Visibility.Visible : Visibility.Collapsed;
            };

            MouseLeftButtonDown += OnDown;
            MouseMove += OnMove;
            MouseLeftButtonUp += OnUp;
            MouseRightButtonDown += (_, _) => _finish(null, null);
            KeyDown += (_, e) => { if (e.Key == Key.Escape) _finish(null, null); };
        }

        public void ShowOverlay()
        {
            Show();
            var hwnd = new WindowInteropHelper(this).Handle;
            // Точные физические границы монитора независимо от масштабирования.
            Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, _shot.Bounds.Left, _shot.Bounds.Top,
                _shot.Bounds.Width, _shot.Bounds.Height, Win32.SWP_SHOWWINDOW);
        }

        public bool ContainsCursor()
        {
            Win32.GetCursorPos(out var p);
            return p.X >= _shot.Bounds.Left && p.X < _shot.Bounds.Right && p.Y >= _shot.Bounds.Top && p.Y < _shot.Bounds.Bottom;
        }

        public new void Focus()
        {
            Activate();
            Native.ForceForeground(new WindowInteropHelper(this).Handle);
        }

        private double PxPerDip => _shot.Bounds.Width / Math.Max(1, ActualWidth);

        private void UpdateDim()
        {
            var full = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
            Geometry geometry = full;
            if (_start is not null && _selection.Width > 0 && _selection.Height > 0)
                geometry = new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(_selection));
            _dim.Data = geometry;
        }

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            if (_colorMode)
            {
                var c = PixelAt(e.GetPosition(_canvas));
                _finish(null, c);
                return;
            }

            _start = e.GetPosition(_canvas);
            _selection = new Rect(_start.Value, new Size(0, 0));
            CaptureMouse();
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            var pos = e.GetPosition(_canvas);

            if (_colorMode)
            {
                UpdateLoupe(pos);
                return;
            }

            if (_start is null || e.LeftButton != MouseButtonState.Pressed) return;

            _selection = new Rect(_start.Value, pos);
            Canvas.SetLeft(_frame, _selection.Left);
            Canvas.SetTop(_frame, _selection.Top);
            _frame.Width = _selection.Width;
            _frame.Height = _selection.Height;
            _frame.Visibility = Visibility.Visible;

            var k = PxPerDip;
            _labelText.Text = $"{(int)(_selection.Width * k)} × {(int)(_selection.Height * k)}";
            _label.Visibility = Visibility.Visible;
            Canvas.SetLeft(_label, _selection.Left);
            Canvas.SetTop(_label, Math.Max(0, _selection.Top - 30));
            UpdateDim();
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            if (_colorMode || _start is null) return;
            ReleaseMouseCapture();

            var k = PxPerDip;
            var rect = new Int32Rect((int)(_selection.X * k), (int)(_selection.Y * k),
                (int)(_selection.Width * k), (int)(_selection.Height * k));
            _start = null;
            Log.Write($"Выделение: dip={_selection} k={k:0.###} ActualWidth={ActualWidth} px={rect}");

            // Случайный клик без выделения — не область.
            if (rect.Width < 6 || rect.Height < 6) { _finish(null, null); return; }
            _finish(new RegionResult(_shot, rect), null);
        }

        private System.Drawing.Color PixelAt(Point dip)
        {
            var k = PxPerDip;
            var x = Math.Clamp((int)(dip.X * k), 0, _shot.Bitmap.Width - 1);
            var y = Math.Clamp((int)(dip.Y * k), 0, _shot.Bitmap.Height - 1);
            return _shot.Bitmap.GetPixel(x, y);
        }

        private void UpdateLoupe(Point pos)
        {
            var k = PxPerDip;
            var px = Math.Clamp((int)(pos.X * k), 0, _shot.Bitmap.Width - 1);
            var py = Math.Clamp((int)(pos.Y * k), 0, _shot.Bitmap.Height - 1);

            const int half = 5;
            var x0 = Math.Clamp(px - half, 0, _shot.Bitmap.Width - 11);
            var y0 = Math.Clamp(py - half, 0, _shot.Bitmap.Height - 11);
            _loupeImage.Source = new CroppedBitmap(_shot.Source, new Int32Rect(x0, y0, 11, 11));

            // Рамка вокруг центрального пикселя сдвигается, если лупа упёрлась в край кадра.
            Canvas.SetLeft(_loupeCenter, (px - x0) * 12 + 0);
            Canvas.SetTop(_loupeCenter, (py - y0) * 12 + 0);

            var c = _shot.Bitmap.GetPixel(px, py);
            _loupeText.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";

            var lx = pos.X + 26;
            var ly = pos.Y + 26;
            if (lx + 140 > ActualWidth) lx = pos.X - 166;
            if (ly + 160 > ActualHeight) ly = pos.Y - 186;
            Canvas.SetLeft(_loupe, lx);
            Canvas.SetTop(_loupe, ly);
            _loupe.Visibility = Visibility.Visible;
        }
    }
}
