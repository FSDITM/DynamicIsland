using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DynamicIsland.Interop;

namespace DynamicIsland.Configuration;

/// <summary>
/// Окно настроек.
///
/// Строки строятся кодом, а не в XAML: их несколько десятков, и одинаковая
/// разметка в разметке же превратилась бы в километр копипасты. Каждая строка
/// сразу пишет значение в <see cref="Settings"/> — островок подхватывает его
/// в ближайшем кадре, поэтому кнопки «Применить» нет.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly Settings _settings;
    private readonly List<Action> _refreshers = [];
    private bool _updating;

    internal SettingsWindow(Settings settings)
    {
        _settings = settings;
        InitializeComponent();

        BuildAppearance();
        BuildBehaviour();
        BuildPlacement();
        BuildContent();

        // Настройки можно поменять и мимо этого окна — например галочками
        // в меню трея. Тогда контролы надо привести в соответствие.
        _settings.PropertyChanged += OnSettingsChanged;
        Closed += (_, _) => _settings.PropertyChanged -= OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_updating) return;
        Dispatcher.BeginInvoke(() =>
        {
            _updating = true;
            foreach (var refresh in _refreshers) refresh();
            _updating = false;
        });
    }

    // ---------- Вкладки ----------

    private void BuildAppearance()
    {
        var p = AppearancePanel;

        Caption(p, "РАЗМЕРЫ");
        Slider(p, "Ширина в покое", 120, 700, 5, () => _settings.RestWidth, v => _settings.RestWidth = v);
        Slider(p, "Высота в покое", 16, 120, 1, () => _settings.RestHeight, v => _settings.RestHeight = v);
        Slider(p, "Ширина раскрытого", 260, 900, 5, () => _settings.ExpandedWidth, v => _settings.ExpandedWidth = v);
        Slider(p, "Высота раскрытого", 80, 400, 2, () => _settings.ExpandedHeight, v => _settings.ExpandedHeight = v);
        Slider(p, "Ширина полоски", 60, 600, 5, () => _settings.NotchWidth, v => _settings.NotchWidth = v);
        Slider(p, "Толщина полоски", 2, 40, 1, () => _settings.NotchHeight, v => _settings.NotchHeight = v);

        Caption(p, "ФОРМА");
        Slider(p, "Скругление углов", 0, 60, 1, () => _settings.CornerRadius, v => _settings.CornerRadius = v);
        Slider(p, "Отступ от края экрана", 0, 80, 1, () => _settings.TopOffset, v => _settings.TopOffset = v);

        Caption(p, "ЦВЕТА И ПРОЗРАЧНОСТЬ");
        Color(p, "Цвет фона", () => _settings.BackgroundColor, v => _settings.BackgroundColor = v);
        Color(p, "Акцентный цвет", () => _settings.AccentColor, v => _settings.AccentColor = v);
        Slider(p, "Непрозрачность", 0.2f, 1f, 0.01f, () => _settings.Opacity, v => _settings.Opacity = v, "0.00");
        Slider(p, "Сила тени", 0f, 2f, 0.05f, () => _settings.ShadowStrength, v => _settings.ShadowStrength = v, "0.00");
    }

    private void BuildBehaviour()
    {
        var p = BehaviourPanel;

        Caption(p, "РАСКРЫТИЕ");
        Slider(p, "Задержка наведения, мс", 0, 1200, 10,
            () => _settings.HoverDelayMs, v => _settings.HoverDelayMs = (int)v, "0");
        Hint(p, "Сколько курсор должен пробыть на островке, прежде чем тот раскроется.");

        Slider(p, "Задержка поверх окна, мс", 0, 2000, 10,
            () => _settings.HoverOverWindowDelayMs, v => _settings.HoverOverWindowDelayMs = (int)v, "0");
        Hint(p, "Когда островок свёрнут в полоску, под ним чужое окно. Раскрытие там " +
                "требует упереть курсор в кромку экрана и подержать — иначе островок " +
                "накрывал бы то, с чем вы работаете.");

        Caption(p, "РЕАКЦИЯ НА ОКНА");
        Combo(p, "Сворачиваться в полоску",
            ["Никогда", "Только для приложений из списка", "Под любым окном"],
            () => (int)_settings.CollapseMode,
            v => _settings.CollapseMode = (CollapseMode)v);
        Hint(p, "Островок мешает не всем: браузеру он закрывает вкладки, а терминалу " +
                "или блокноту эта полоса экрана не нужна. Список решает, для кого сворачиваться.");

        Combo(p, "Свёрнутый вид",
            ["Тонкая полоска у края", "Скрывать полностью"],
            () => (int)_settings.CollapsedLook,
            v => _settings.CollapsedLook = (CollapsedLook)v);
        Hint(p, "Полоска занимает несколько пикселей у верхней кромки, и нажатия по ним " +
                "достаются островку — так устроен Windows, отдать их вниз нельзя. " +
                "Если эти пиксели мешают вкладкам браузера, выберите «скрывать полностью».");

        AppList(p);

        Check(p, "Прятаться в полноэкранном режиме",
            () => _settings.HideInFullscreen, v => _settings.HideInFullscreen = v);
        Hint(p, "Игры, видео на весь экран и презентации.");

        Caption(p, "МУЗЫКА");
        Check(p, "Показывать новый трек при смене песни",
            () => _settings.PeekOnTrackChange, v => _settings.PeekOnTrackChange = v);
        Slider(p, "Сколько показывать, с", 0.5f, 15f, 0.5f,
            () => _settings.PeekSeconds, v => _settings.PeekSeconds = v, "0.0");
        Check(p, "Анимировать эквалайзер",
            () => _settings.AnimateEqualizer, v => _settings.AnimateEqualizer = v);
        Hint(p, "Пока музыка играет, островок перерисовывается ~18 раз в секунду. " +
                "Выключите, если бережёте батарею — в покое расход и так нулевой.");

        Caption(p, "АНИМАЦИЯ");
        Slider(p, "Скорость анимаций", 0.3f, 3f, 0.05f,
            () => _settings.AnimationSpeed, v => _settings.AnimationSpeed = v, "0.00");
        Hint(p, "Больше — резче и быстрее, меньше — плавнее и мягче.");
    }

    private void BuildPlacement()
    {
        var p = PlacementPanel;

        Caption(p, "ЭКРАН");

        var monitors = Win32.GetMonitors();
        var names = monitors.Select((m, i) =>
            $"Монитор {i + 1} — {m.rcMonitor.Width}×{m.rcMonitor.Height}" +
            ((m.dwFlags & 1) != 0 ? " (основной)" : "")).ToList();
        if (names.Count == 0) names.Add("Монитор 1");

        Combo(p, "Показывать на", names,
            () => Math.Min(_settings.MonitorIndex, names.Count - 1),
            v => _settings.MonitorIndex = v);

        Combo(p, "Край экрана", ["Сверху", "Снизу"],
            () => _settings.Anchor == ScreenAnchor.Bottom ? 1 : 0,
            v => _settings.Anchor = v == 1 ? ScreenAnchor.Bottom : ScreenAnchor.Top);

        Caption(p, "СМЕЩЕНИЕ");
        Slider(p, "По горизонтали", -600, 600, 5,
            () => _settings.HorizontalOffset, v => _settings.HorizontalOffset = v, "0");
        Hint(p, "Ноль — по центру экрана. Отрицательные значения сдвигают влево.");
    }

    private void BuildContent()
    {
        var p = ContentPanel;

        Caption(p, "ЧТО ПОКАЗЫВАТЬ");
        Check(p, "Часы", () => _settings.ShowClock, v => _settings.ShowClock = v);
        Check(p, "Дата", () => _settings.ShowDate, v => _settings.ShowDate = v);
        Check(p, "Заряд батареи", () => _settings.ShowBattery, v => _settings.ShowBattery = v);
        Check(p, "Эквалайзер во время воспроизведения",
            () => _settings.ShowEqualizer, v => _settings.ShowEqualizer = v);
        Check(p, "24-часовой формат времени",
            () => _settings.Use24HourClock, v => _settings.Use24HourClock = v);

        Caption(p, "ГОРЯЧАЯ КЛАВИША");
        HotkeyRow(p);
        Hint(p, "Показать или скрыть островок. Кликните по полю и нажмите сочетание. " +
                "Escape очищает.");

        Caption(p, "ПРОЧЕЕ");
        Check(p, "Запускать при входе в систему",
            () => _settings.RunOnStartup, v => _settings.RunOnStartup = v);
        Check(p, "Показывать метрики производительности",
            () => _settings.ShowMetrics, v => _settings.ShowMetrics = v);
    }

    // ---------- Строители строк ----------

    private static void Caption(Panel parent, string text) =>
        parent.Children.Add(new TextBlock { Text = text, Style = (Style)parent.FindResource("Caption") });

    private static void Hint(Panel parent, string text) =>
        parent.Children.Add(new TextBlock { Text = text, Style = (Style)parent.FindResource("Hint") });

    private void Slider(Panel parent, string label, float min, float max, float step,
                        Func<float> get, Action<float> set, string format = "0")
    {
        var grid = NewRow(label, out var host);

        var slider = new System.Windows.Controls.Slider
        {
            Minimum = min,
            Maximum = max,
            TickFrequency = step,
            Value = get(),
            Width = 260,
        };

        var value = new TextBlock
        {
            Text = get().ToString(format, CultureInfo.InvariantCulture),
            Width = 60,
            Margin = new Thickness(12, 0, 0, 0),
            Foreground = (Brush)FindResource("FgMuted"),
        };

        slider.ValueChanged += (_, _) =>
        {
            if (_updating) return;
            _updating = true;
            set((float)slider.Value);
            value.Text = ((float)slider.Value).ToString(format, CultureInfo.InvariantCulture);
            _updating = false;
        };

        _refreshers.Add(() =>
        {
            slider.Value = get();
            value.Text = get().ToString(format, CultureInfo.InvariantCulture);
        });

        host.Children.Add(slider);
        host.Children.Add(value);
        parent.Children.Add(grid);
    }

    private void Check(Panel parent, string label, Func<bool> get, Action<bool> set)
    {
        var box = new CheckBox { Content = label, IsChecked = get() };

        box.Checked += (_, _) => { if (!_updating) set(true); };
        box.Unchecked += (_, _) => { if (!_updating) set(false); };
        _refreshers.Add(() => box.IsChecked = get());

        parent.Children.Add(box);
    }

    private void Combo(Panel parent, string label, IList<string> items, Func<int> get, Action<int> set)
    {
        var grid = NewRow(label, out var host);

        var combo = new ComboBox { Width = 320, SelectedIndex = Math.Max(0, get()) };
        foreach (var item in items) combo.Items.Add(item);

        combo.SelectionChanged += (_, _) =>
        {
            if (!_updating && combo.SelectedIndex >= 0) set(combo.SelectedIndex);
        };
        _refreshers.Add(() => combo.SelectedIndex = Math.Max(0, get()));

        host.Children.Add(combo);
        parent.Children.Add(grid);
    }

    private void Color(Panel parent, string label, Func<uint> get, Action<uint> set)
    {
        var grid = NewRow(label, out var host);

        var preview = new Border
        {
            Width = 30,
            Height = 22,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)FindResource("Line"),
            Background = new SolidColorBrush(FromArgb(get())),
            Margin = new Thickness(0, 0, 10, 0),
        };

        var text = new TextBox { Text = $"#{get():X8}", Width = 110 };

        text.TextChanged += (_, _) =>
        {
            if (_updating) return;
            var raw = text.Text.TrimStart('#');
            if (!uint.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed)) return;

            // Без альфы считаем цвет непрозрачным — так удобнее вводить #RRGGBB.
            if (raw.Length <= 6) parsed |= 0xFF000000;

            _updating = true;
            set(parsed);
            preview.Background = new SolidColorBrush(FromArgb(parsed));
            _updating = false;
        };

        _refreshers.Add(() =>
        {
            text.Text = $"#{get():X8}";
            preview.Background = new SolidColorBrush(FromArgb(get()));
        });

        host.Children.Add(preview);
        host.Children.Add(text);
        parent.Children.Add(grid);
    }

    /// <summary>Редактор списка приложений: по одному имени процесса в строке.</summary>
    private void AppList(Panel parent)
    {
        var box = new TextBox
        {
            Text = string.Join(Environment.NewLine, _settings.CollapseApps),
            AcceptsReturn = true,
            Height = 130,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 4, 0, 6),
        };

        box.LostFocus += (_, _) => Commit();
        _refreshers.Add(() => box.Text = string.Join(Environment.NewLine, _settings.CollapseApps));

        void Commit()
        {
            if (_updating) return;
            _updating = true;
            _settings.CollapseApps = box.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            box.Text = string.Join(Environment.NewLine, _settings.CollapseApps);
            _updating = false;
        }

        var add = new Button { Content = "Добавить запущенное…", Style = (Style)FindResource("Action"), Margin = new Thickness(0) };
        add.Click += (_, _) =>
        {
            var picked = RunningAppPicker.Pick(this);
            if (picked is null) return;

            box.Text = string.Join(Environment.NewLine,
                box.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Append(picked));
            Commit();
        };

        parent.Children.Add(box);
        parent.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 10),
            Children = { add },
        });
        Hint(parent, "Имя процесса без .exe, по одному в строке. Список применяется, " +
                     "когда поле теряет фокус.");
    }

    private void HotkeyRow(Panel parent)
    {
        var grid = NewRow("Показать или скрыть", out var host);

        var box = new TextBox
        {
            Text = _settings.Hotkey,
            Width = 220,
            IsReadOnly = true,
            Cursor = Cursors.Hand,
            FontFamily = new FontFamily("Segoe UI"),
        };

        box.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;

            if (e.Key == Key.Escape) { _settings.Hotkey = ""; box.Text = ""; return; }

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;

            var parts = new List<string>();
            var mods = Keyboard.Modifiers;
            if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");

            // Без модификатора сочетание перехватывало бы обычный ввод.
            if (parts.Count == 0) return;

            parts.Add(key.ToString());
            var combo = string.Join("+", parts);

            if (!Platform.Hotkey.TryParse(combo, out var parsedMods, out var parsedKey)) return;
            if (parsedKey == 0 || parsedMods == 0) return;

            _settings.Hotkey = combo;
            box.Text = combo;
        };

        _refreshers.Add(() => box.Text = _settings.Hotkey);

        host.Children.Add(box);
        parent.Children.Add(grid);
    }

    private Grid NewRow(string label, out StackPanel host)
    {
        var grid = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var caption = new TextBlock { Text = label };
        Grid.SetColumn(caption, 0);
        grid.Children.Add(caption);

        host = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(host, 1);
        grid.Children.Add(host);

        return grid;
    }

    private static System.Windows.Media.Color FromArgb(uint value) => System.Windows.Media.Color.FromArgb(
        (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);

    // ---------- Кнопки ----------

    private void OnOpenFile(object sender, RoutedEventArgs e)
    {
        _settings.Save();
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Settings.FilePath}\""));
        }
        catch (Exception ex) { Log.Write("Не удалось открыть файл настроек: " + ex.Message); }
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Вернуть все настройки к значениям по умолчанию?",
            "Сброс настроек", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return;

        _settings.ResetToDefaults();

        _updating = true;
        foreach (var refresh in _refreshers) refresh();
        _updating = false;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
