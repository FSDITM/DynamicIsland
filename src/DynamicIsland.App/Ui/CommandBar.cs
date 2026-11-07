using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicIsland.Features;
using DynamicIsland.Interop;
using DynamicIsland.Platform;

namespace DynamicIsland.Ui;

/// <summary>Командная строка: окно по горячей клавише, живёт на общем UI-потоке.</summary>
internal static class CommandBarHost
{
    private static CommandBarWindow? _window;
    private static CommandEngine? _engine;

    public static void Init(CommandHost host) => _engine = new CommandEngine(host);

    public static void Toggle(string? prefill = null) => UiThread.Post(() =>
    {
        if (_engine is null) return;
        _window ??= new CommandBarWindow(_engine);

        if (_window.IsVisible && prefill is null) _window.Hide();
        else _window.Open(prefill);
    });

    public static void Close() => UiThread.Post(() => _window?.Hide());
}

internal sealed class CommandBarWindow : Window
{
    private static readonly Brush Panel = Freeze(new SolidColorBrush(Color.FromArgb(0xF6, 0x10, 0x0F, 0x0D)));
    private static readonly Brush Edge = Freeze(new SolidColorBrush(Color.FromArgb(0x3A, 0xF0, 0xED, 0xE6)));
    private static readonly Brush Primary = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0xED, 0xE6)));
    private static readonly Brush Muted = Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0x95, 0x8B)));
    private static readonly Brush Selected = Freeze(new SolidColorBrush(Color.FromArgb(0x22, 0xF0, 0xED, 0xE6)));
    private static readonly Brush IconBg = Freeze(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush Accent = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0xED, 0xE6)));
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly FontFamily TextFont = WpfFonts.Text;

    private static T Freeze<T>(T b) where T : Freezable { b.Freeze(); return b; }

    private readonly CommandEngine _engine;
    private readonly TextBox _box = new();
    private readonly TextBlock _hint = new();
    private readonly StackPanel _list = new();
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(70) };

    private List<CmdItem> _items = [];
    private int _selected;
    private CmdItem? _confirming;
    private int _version;

    public CommandBarWindow(CommandEngine engine)
    {
        _engine = engine;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 660;
        ShowActivated = true;
        Title = "DynamicIsland — командная строка";

        _box.FontFamily = TextFont;
        _box.FontSize = 20;
        _box.Foreground = Primary;
        _box.CaretBrush = Primary;
        _box.Background = Brushes.Transparent;
        _box.BorderThickness = new Thickness(0);
        _box.Padding = new Thickness(0, 6, 0, 6);
        _box.VerticalContentAlignment = VerticalAlignment.Center;
        _box.TextChanged += (_, _) => { _debounce.Stop(); _debounce.Start(); };

        _hint.Text = "Программа, файл, расчёт, таймер…";
        _hint.TextTrimming = TextTrimming.CharacterEllipsis;
        _hint.FontFamily = TextFont;
        _hint.FontSize = 20;
        _hint.Foreground = Muted;
        _hint.IsHitTestVisible = false;
        _hint.VerticalAlignment = VerticalAlignment.Center;
        _hint.Margin = new Thickness(0, 0, 0, 0);

        var inputGrid = new Grid { Margin = new Thickness(20, 12, 20, 8) };
        var glyph = new TextBlock
        {
            Text = G.Search, FontFamily = IconFont, FontSize = 18, Foreground = Muted,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
        };
        var inputHost = new Grid();
        inputHost.Children.Add(_hint);
        inputHost.Children.Add(_box);
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(glyph, 0);
        Grid.SetColumn(inputHost, 1);
        inputGrid.Children.Add(glyph);
        inputGrid.Children.Add(inputHost);

        var separator = new Border { Height = 1, Background = Edge, Margin = new Thickness(0), Visibility = Visibility.Collapsed, Name = "Sep" };

        _list.Margin = new Thickness(10, 6, 10, 10);

        var stack = new StackPanel();
        stack.Children.Add(inputGrid);
        stack.Children.Add(separator);
        stack.Children.Add(_list);

        Content = new Border
        {
            Background = Panel,
            BorderBrush = Edge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(18),
            Child = stack,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 24, ShadowDepth = 4, Opacity = 0.4, Color = Colors.Black,
            },
        };

        _debounce.Tick += async (_, _) =>
        {
            _debounce.Stop();
            await RunQuery();
        };

        PreviewKeyDown += OnKey;
        Deactivated += (_, _) => { if (IsVisible) Hide(); };
    }

    public void Open(string? prefill)
    {
        _box.Text = prefill ?? "";
        _box.CaretIndex = _box.Text.Length;
        _items = [];
        _list.Children.Clear();
        _confirming = null;
        Sep().Visibility = Visibility.Collapsed;
        _hint.Visibility = _box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Окно по центру верхней трети того монитора, где курсор.
        var screen = SystemParameters.WorkArea;
        Left = screen.Left + (screen.Width - Width - 36) / 2;
        Top = screen.Top + screen.Height * 0.16;

        Show();
        Activate();
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.ForceForeground(hwnd);
        _box.Focus();
        Keyboard.Focus(_box);

        if (_box.Text.Length > 0) _ = RunQuery();
    }

    private Border Sep() => (Border)((StackPanel)((Border)Content).Child).Children[1];

    private async Task RunQuery()
    {
        var version = ++_version;
        var q = _box.Text;
        _hint.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        List<CmdItem> items;
        try { items = _engine.Query(q); }
        catch (Exception ex) { Log.Write("Запрос не разобран: " + ex); items = []; }

        _items = items;
        _selected = 0;
        _confirming = null;
        Render();

        // Единицы и валюты: ответ может прийти позже, чем остальное.
        if (q.Trim().Length > 2)
        {
            CmdItem? conv = null;
            try { conv = await _engine.ConversionAsync(q); }
            catch (Exception ex) { Log.Write("Конвертер: " + ex.Message); }

            if (conv is not null && version == _version)
            {
                _items.Insert(0, conv);
                _selected = 0;
                Render();
            }
        }
    }

    private void Render()
    {
        _list.Children.Clear();
        Sep().Visibility = _items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        for (var i = 0; i < _items.Count && i < 9; i++)
        {
            var item = _items[i];
            var index = i;
            var selected = i == _selected;

            // Значок без подложки; выбранная строка отмечена полоской слева.
            var icon = new TextBlock
            {
                Text = item.Glyph, FontFamily = IconFont, FontSize = 16, Foreground = selected ? Primary : Muted,
                Width = 30, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Left,
                Margin = new Thickness(6, 0, 8, 0),
            };

            var title = new TextBlock
            {
                Text = item.Title, FontFamily = TextFont, FontSize = 14.5, FontWeight = FontWeights.SemiBold,
                Foreground = Primary, TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var sub = new TextBlock
            {
                Text = _confirming == item ? "Нажмите Enter ещё раз — это точно?" : item.Sub,
                FontFamily = TextFont, FontSize = 11.5,
                Foreground = _confirming == item ? Accent : Muted, TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(title);
            texts.Children.Add(sub);

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(texts, 1);
            row.Children.Add(icon);
            row.Children.Add(texts);

            var holder = new Border
            {
                Child = row,
                Padding = new Thickness(8, 7, 10, 7),
                CornerRadius = new CornerRadius(4),
                Background = selected ? Selected : Brushes.Transparent,
                BorderBrush = selected ? Accent : Brushes.Transparent,
                BorderThickness = new Thickness(2, 0, 0, 0),
                Cursor = Cursors.Hand,
            };
            holder.MouseEnter += (_, _) => { if (_selected != index) { _selected = index; Render(); } };
            holder.MouseLeftButtonUp += (_, _) => Execute(Keyboard.Modifiers);
            _list.Children.Add(holder);
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Hide();
                e.Handled = true;
                break;
            case Key.Down:
                if (_items.Count > 0) { _selected = (_selected + 1) % Math.Min(_items.Count, 9); _confirming = null; Render(); }
                e.Handled = true;
                break;
            case Key.Up:
                if (_items.Count > 0) { _selected = (_selected + Math.Min(_items.Count, 9) - 1) % Math.Min(_items.Count, 9); _confirming = null; Render(); }
                e.Handled = true;
                break;
            case Key.Enter:
                Execute(Keyboard.Modifiers);
                e.Handled = true;
                break;
        }
    }

    private void Execute(ModifierKeys keys)
    {
        if (_selected < 0 || _selected >= _items.Count) return;
        var item = _items[_selected];

        if (item.NeedsConfirm && _confirming != item)
        {
            _confirming = item;
            Render();
            return;
        }

        var mods = CmdMods.None;
        if (keys.HasFlag(ModifierKeys.Control)) mods |= CmdMods.Ctrl;
        if (keys.HasFlag(ModifierKeys.Shift)) mods |= CmdMods.Shift;

        bool close;
        try { close = item.Run(mods); }
        catch (Exception ex) { Log.Write("Команда не выполнилась: " + ex); close = true; }

        if (close) Hide();
    }
}
