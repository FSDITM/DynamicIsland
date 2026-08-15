using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DynamicIsland.Interop;
using DynamicIsland.Platform;

namespace DynamicIsland.Configuration;

/// <summary>
/// Окно настроек.
///
/// Разделы слева, содержимое справа карточками — так устроены системные
/// настройки на маках, и это привычнее вкладок: видно все разделы сразу,
/// а не только соседние.
///
/// Всё применяется мгновенно, кнопки «Сохранить» нет: настройка, которую
/// надо подтверждать, заставляет гадать, что изменится. Здесь видно сразу.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly Settings _settings;

    /// <summary>Обновляют контролы, когда значения меняются не из них (сброс).</summary>
    private readonly List<Action> _refreshers = [];

    /// <summary>Защита от петли: контрол меняет настройку, та обновляет контрол.</summary>
    private bool _updating;

    private readonly List<(string Title, string Subtitle, Action<Panel> Build)> _sections;

    internal SettingsWindow(Settings settings)
    {
        _settings = settings;
        InitializeComponent();

        ApplyBundledFont();
        LoadLogo();
        VersionLabel.Text = "версия " + AppInfo.Version;

        _sections =
        [
            ("Вид", "Размеры, цвета и форма островка.", BuildAppearance),
            ("Поведение", "Когда островок раскрывается, сворачивается и уходит с глаз.", BuildBehaviour),
            ("Расположение", "На каком экране и в каком месте он живёт.", BuildPlacement),
            ("Содержимое", "Что показывать и какой клавишей вызывать.", BuildContent),
            ("О программе", "Откуда взялся этот островок.", BuildAbout),
        ];

        foreach (var (title, _, _) in _sections) Nav.Items.Add(title);
        Nav.SelectedIndex = 0;
    }

    /// <summary>
    /// Подключает вшитый Inter. В системе его нет, поэтому по имени он не
    /// найдётся — нужен путь к папке со шрифтом.
    /// </summary>
    private void ApplyBundledFont()
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "Resources", "Fonts");
            if (!Directory.Exists(dir)) return;

            var family = new FontFamily(new Uri(dir + Path.DirectorySeparatorChar), "./#Inter");
            if (family.FamilyNames.Count > 0) FontFamily = family;
        }
        catch (Exception ex)
        {
            Log.Write("Шрифт окна настроек не подключился: " + ex.Message);
        }
    }

    private void LoadLogo()
    {
        try
        {
            if (File.Exists(TrayIcon.IconPath))
                AppLogo.Source = new BitmapImage(new Uri(TrayIcon.IconPath));
        }
        catch (Exception ex)
        {
            Log.Write("Логотип не загрузился: " + ex.Message);
        }
    }

    private void OnSectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedIndex < 0) return;

        _refreshers.Clear();
        ContentHost.Children.Clear();

        var (title, subtitle, build) = _sections[Nav.SelectedIndex];
        ContentHost.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Title") });
        ContentHost.Children.Add(new TextBlock { Text = subtitle, Style = (Style)FindResource("Subtitle") });

        build(ContentHost);
    }

    // ==================== Разделы ====================

    private void BuildAppearance(Panel host)
    {
        host.Children.Add(BuildPreview());

        var size = Card(host, "Размеры");
        SliderRow(size, "Ширина в покое", 120, 700, 5,
            () => _settings.RestWidth, v => _settings.RestWidth = v, "{0:0} px");
        SliderRow(size, "Высота в покое", 16, 120, 1,
            () => _settings.RestHeight, v => _settings.RestHeight = v, "{0:0} px");
        SliderRow(size, "Ширина раскрытого", 260, 900, 5,
            () => _settings.ExpandedWidth, v => _settings.ExpandedWidth = v, "{0:0} px");
        SliderRow(size, "Высота раскрытого", 80, 400, 2,
            () => _settings.ExpandedHeight, v => _settings.ExpandedHeight = v, "{0:0} px");
        SliderRow(size, "Ширина полоски", 60, 600, 5,
            () => _settings.NotchWidth, v => _settings.NotchWidth = v, "{0:0} px");
        SliderRow(size, "Толщина полоски", 2, 40, 1,
            () => _settings.NotchHeight, v => _settings.NotchHeight = v, "{0:0} px",
            "Эти пиксели перекрывают окно под островком — чем тоньше, тем меньше мешает.");

        var shape = Card(host, "Форма и цвет");
        SliderRow(shape, "Скругление углов", 0, 60, 1,
            () => _settings.CornerRadius, v => _settings.CornerRadius = v, "{0:0} px");
        SliderRow(shape, "Отступ от края экрана", 0, 80, 1,
            () => _settings.TopOffset, v => _settings.TopOffset = v, "{0:0} px");
        SliderRow(shape, "Непрозрачность", 0.2f, 1f, 0.05f,
            () => _settings.Opacity, v => _settings.Opacity = v, "{0:P0}");
        SliderRow(shape, "Сила тени", 0f, 2f, 0.1f,
            () => _settings.ShadowStrength, v => _settings.ShadowStrength = v, "{0:0.0}×");
        ColorRow(shape, "Цвет фона", () => _settings.BackgroundColor, v => _settings.BackgroundColor = v);
        ColorRow(shape, "Акцентный цвет", () => _settings.AccentColor, v => _settings.AccentColor = v);
    }

    private void BuildBehaviour(Panel host)
    {
        var hover = Card(host, "Раскрытие");
        SliderRow(hover, "Задержка перед раскрытием", 0, 1000, 20,
            () => _settings.HoverDelayMs, v => _settings.HoverDelayMs = (int)v, "{0:0} мс",
            "Курсор должен задержаться на островке — иначе он распахивался бы от любого движения мыши.");
        SliderRow(hover, "Задержка поверх чужого окна", 0, 1500, 20,
            () => _settings.HoverOverWindowDelayMs, v => _settings.HoverOverWindowDelayMs = (int)v, "{0:0} мс",
            "Здесь дольше: раскрыться поверх чужого окна должно быть решением, а не случайностью.");
        SliderRow(hover, "Скорость анимаций", 0.3f, 3f, 0.1f,
            () => _settings.AnimationSpeed, v => _settings.AnimationSpeed = v, "{0:0.0}×");

        var windows = Card(host, "Реакция на окна");
        ChoiceRow(windows, "Сворачиваться в полоску",
            ["Никогда", "Только для приложений из списка", "Под любым окном"],
            () => (int)_settings.CollapseMode, v => _settings.CollapseMode = (CollapseMode)v,
            "Островок мешает не всем: браузеру он закрывает вкладки, а терминалу эта полоса экрана не нужна.");
        ChoiceRow(windows, "Свёрнутый вид",
            ["Тонкая полоска у края", "Скрывать полностью"],
            () => (int)_settings.CollapsedLook, v => _settings.CollapsedLook = (CollapsedLook)v,
            "Полоска занимает несколько пикселей, и нажатия по ним достаются островку — так устроен Windows. " +
            "Если они мешают вкладкам, выберите «скрывать полностью».");
        ToggleRow(windows, "Прятаться в полноэкранном режиме",
            () => _settings.HideInFullscreen, v => _settings.HideInFullscreen = v,
            "Игры, видео на весь экран и презентации.");

        var apps = Card(host, "Приложения, которым мешаем");
        AppListRow(apps);

        var media = Card(host, "Музыка");
        ToggleRow(media, "Показывать новый трек",
            () => _settings.PeekOnTrackChange, v => _settings.PeekOnTrackChange = v,
            "При смене песни островок сам раскроется на несколько секунд.");
        SliderRow(media, "Насколько задержаться", 0.5f, 15f, 0.5f,
            () => _settings.PeekSeconds, v => _settings.PeekSeconds = v, "{0:0.0} с");
        ToggleRow(media, "Живой эквалайзер",
            () => _settings.AnimateEqualizer, v => _settings.AnimateEqualizer = v,
            "Столбики шевелятся, пока играет музыка. Стоит около процента одного ядра.");
    }

    private void BuildPlacement(Panel host)
    {
        var monitors = Win32.GetMonitors();
        var names = new List<string>();
        for (var i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            var primary = (m.dwFlags & 1) != 0 ? " — основной" : "";
            names.Add($"Экран {i + 1}: {m.rcMonitor.Width}×{m.rcMonitor.Height}{primary}");
        }
        if (names.Count == 0) names.Add("Экран 1");

        var place = Card(host, "Где показывать");
        ChoiceRow(place, "Экран", names,
            () => Math.Min(_settings.MonitorIndex, names.Count - 1),
            v => _settings.MonitorIndex = v);
        ChoiceRow(place, "Край экрана", ["Сверху", "Снизу"],
            () => (int)_settings.Anchor, v => _settings.Anchor = (ScreenAnchor)v);
        SliderRow(place, "Смещение по горизонтали", -600, 600, 5,
            () => _settings.HorizontalOffset, v => _settings.HorizontalOffset = v, "{0:0} px",
            "Ноль — по центру экрана.");
    }

    private void BuildContent(Panel host)
    {
        var shows = Card(host, "Что показывать");
        ToggleRow(shows, "Часы", () => _settings.ShowClock, v => _settings.ShowClock = v);
        ToggleRow(shows, "Дата", () => _settings.ShowDate, v => _settings.ShowDate = v);
        ToggleRow(shows, "Заряд батареи", () => _settings.ShowBattery, v => _settings.ShowBattery = v);
        ToggleRow(shows, "Эквалайзер при воспроизведении",
            () => _settings.ShowEqualizer, v => _settings.ShowEqualizer = v);
        ToggleRow(shows, "24-часовое время",
            () => _settings.Use24HourClock, v => _settings.Use24HourClock = v);

        var font = Card(host, "Шрифт");
        TextRow(font, "Семейство", () => _settings.FontFamily, v => _settings.FontFamily = v,
            "Пусто — вшитый Inter. Можно указать свой, например SF Pro, если он установлен в системе.");

        var system = Card(host, "Система");
        HotkeyRow(system);
        ToggleRow(system, "Запускать при входе в систему",
            () => _settings.RunOnStartup, v => _settings.RunOnStartup = v);
        ToggleRow(system, "Показывать метрики",
            () => _settings.ShowMetrics, v => _settings.ShowMetrics = v,
            "Кадры, время отрисовки и активное приложение — поверх островка.");
    }

    private void BuildAbout(Panel host)
    {
        var card = Card(host, null);

        InfoRow(card, "Версия", AppInfo.Version);
        InfoRow(card, "Отрисовка", "Direct3D 12 + DirectComposition, Skia на GPU");
        InfoRow(card, "Шрифт", "Inter (SIL Open Font License)");
        InfoRow(card, "Файл настроек", Settings.FilePath);

        var origin = Card(host, "Происхождение");
        var text = new TextBlock
        {
            Text = "Переработанная версия DynamicWin Флориана Бутца. Исходники оригинала были " +
                   "удалены из репозитория в октябре 2025 года и восстановлены из истории git.\n\n" +
                   "Ядро отрисовки написано заново: окно размером с островок вместо полноэкранного, " +
                   "кадр не покидает видеопамять, а в покое поток спит и кадры не рисуются вовсе.\n\n" +
                   "Лицензия CC BY-SA 4.0 — как у оригинала.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("FgMuted"),
            FontSize = 13,
            Margin = new Thickness(16, 14, 16, 14),
            LineHeight = 19,
        };
        origin.Children.Add(text);
    }

    // ==================== Живой предпросмотр ====================

    /// <summary>
    /// Показывает островок на макете экрана. Настройка размера и цвета вслепую,
    /// с перебежками к настоящему островку и обратно, — худшее, что можно
    /// придумать; здесь результат виден сразу.
    /// </summary>
    private UIElement BuildPreview()
    {
        const double screenWidth = 460;
        const double screenHeight = 132;

        var island = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
        };

        var caption = new TextBlock
        {
            Foreground = (Brush)FindResource("FgMuted"),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0),
        };

        var screen = new Border
        {
            Width = screenWidth,
            Height = screenHeight,
            CornerRadius = new CornerRadius(14),
            Background = new LinearGradientBrush(
                Color.FromRgb(0x2A, 0x2F, 0x3E), Color.FromRgb(0x14, 0x16, 0x1D), 60),
            Child = new Canvas { Children = { island } },
            Margin = new Thickness(0, 0, 0, 6),
        };

        void Refresh()
        {
            // Макет — не пиксель в пиксель, а пропорция: реальный экран шире
            // макета примерно вдвое, и в таком масштабе островок различим.
            const double virtualScreenWidth = 900.0;
            var scale = screenWidth / virtualScreenWidth;

            island.Width = Math.Max(8, _settings.RestWidth * scale);
            island.Height = Math.Max(3, _settings.RestHeight * scale);
            island.CornerRadius = new CornerRadius(
                Math.Min(island.Height / 2, _settings.CornerRadius * scale));
            island.Background = new SolidColorBrush(FromArgb(_settings.BackgroundColor))
            {
                Opacity = _settings.Opacity,
            };

            var top = _settings.Anchor == ScreenAnchor.Bottom
                ? screenHeight - _settings.TopOffset * scale - island.Height
                : _settings.TopOffset * scale;

            Canvas.SetTop(island, top);
            Canvas.SetLeft(island, (screenWidth - island.Width) / 2 + _settings.HorizontalOffset * scale);

            caption.Text = $"в покое {_settings.RestWidth:0}×{_settings.RestHeight:0}, " +
                           $"раскрытый {_settings.ExpandedWidth:0}×{_settings.ExpandedHeight:0}, " +
                           $"полоска {_settings.NotchWidth:0}×{_settings.NotchHeight:0}";
        }

        Refresh();
        _refreshers.Add(Refresh);

        return new StackPanel
        {
            Margin = new Thickness(0, 0, 0, 22),
            Children = { screen, caption },
        };
    }

    // ==================== Кирпичики ====================

    private StackPanel Card(Panel parent, string? caption)
    {
        if (caption is not null)
            parent.Children.Add(new TextBlock
            {
                Text = caption.ToUpperInvariant(),
                Style = (Style)FindResource("GroupCaption"),
            });

        var inner = new StackPanel();
        parent.Children.Add(new Border
        {
            Background = (Brush)FindResource("Card"),
            CornerRadius = new CornerRadius(12),
            Child = inner,
            Margin = new Thickness(0, 0, 0, 22),
        });
        return inner;
    }

    private Border Separator() => new()
    {
        Height = 1,
        Background = (Brush)FindResource("Line"),
        Margin = new Thickness(16, 0, 0, 0),
    };

    private void Row(Panel card, string label, string? hint, UIElement control)
    {
        if (card.Children.Count > 0) card.Children.Add(Separator());

        var grid = new Grid { Margin = new Thickness(16, 13, 16, 13) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("RowLabel") });
        if (hint is not null)
            text.Children.Add(new TextBlock { Text = hint, Style = (Style)FindResource("RowHint") });

        Grid.SetColumn(text, 0);
        Grid.SetColumn(control, 1);
        grid.Children.Add(text);
        grid.Children.Add(control);

        card.Children.Add(grid);
    }

    private void ToggleRow(Panel card, string label, Func<bool> get, Action<bool> set, string? hint = null)
    {
        var toggle = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = get() };
        toggle.Checked += (_, _) => Apply(() => set(true));
        toggle.Unchecked += (_, _) => Apply(() => set(false));
        _refreshers.Add(() => toggle.IsChecked = get());

        Row(card, label, hint, toggle);
    }

    private void SliderRow(Panel card, string label, float min, float max, float step,
                           Func<float> get, Action<float> set, string format, string? hint = null)
    {
        var value = new TextBlock
        {
            Foreground = (Brush)FindResource("FgMuted"),
            FontSize = 13,
            MinWidth = 62,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(12, 0, 0, 0),
        };

        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            TickFrequency = step,
            IsSnapToTickEnabled = true,
            Value = Math.Clamp(get(), min, max),
        };

        void ShowValue() => value.Text = string.Format(format, format.Contains("P0") ? get() : get());

        slider.ValueChanged += (_, e) => Apply(() =>
        {
            set((float)e.NewValue);
            ShowValue();
        });

        _refreshers.Add(() =>
        {
            slider.Value = Math.Clamp(get(), min, max);
            ShowValue();
        });

        ShowValue();

        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(slider);
        panel.Children.Add(value);

        Row(card, label, hint, panel);
    }

    private void ChoiceRow(Panel card, string label, IReadOnlyList<string> options,
                           Func<int> get, Action<int> set, string? hint = null)
    {
        var combo = new ComboBox();
        foreach (var option in options) combo.Items.Add(option);
        combo.SelectedIndex = Math.Clamp(get(), 0, options.Count - 1);

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0) Apply(() => set(combo.SelectedIndex));
        };
        _refreshers.Add(() => combo.SelectedIndex = Math.Clamp(get(), 0, options.Count - 1));

        Row(card, label, hint, combo);
    }

    private void TextRow(Panel card, string label, Func<string> get, Action<string> set, string? hint = null)
    {
        var box = new TextBox { Text = get(), MinWidth = 200 };
        box.LostFocus += (_, _) => Apply(() => set(box.Text));
        _refreshers.Add(() => box.Text = get());

        Row(card, label, hint, box);
    }

    private void ColorRow(Panel card, string label, Func<uint> get, Action<uint> set)
    {
        var swatch = new Border
        {
            Width = 30,
            Height = 22,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(FromArgb(get())),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Ширины под девять знаков «#AARRGGBB»: при 110 последний символ обрезался.
        var box = new TextBox { Text = ToHex(get()), Width = 136 };

        void Commit()
        {
            if (!TryParseHex(box.Text, out var color)) { box.Text = ToHex(get()); return; }
            Apply(() =>
            {
                set(color);
                swatch.Background = new SolidColorBrush(FromArgb(color));
            });
        }

        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Commit(); };

        _refreshers.Add(() =>
        {
            box.Text = ToHex(get());
            swatch.Background = new SolidColorBrush(FromArgb(get()));
        });

        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(swatch);
        panel.Children.Add(box);

        Row(card, label, null, panel);
    }

    private void HotkeyRow(Panel card)
    {
        var box = new TextBox { Text = _settings.Hotkey, MinWidth = 200 };
        var status = new TextBlock
        {
            Style = (Style)FindResource("RowHint"),
            Text = "Например Ctrl+Alt+D. Пусто — клавиша отключена.",
        };

        void Commit()
        {
            var text = box.Text.Trim();
            if (text.Length > 0 && !Hotkey.TryParse(text, out _, out _))
            {
                status.Text = "Не удалось разобрать сочетание — оставлено прежнее.";
                box.Text = _settings.Hotkey;
                return;
            }

            status.Text = "Например Ctrl+Alt+D. Пусто — клавиша отключена.";
            Apply(() => _settings.Hotkey = text);
        }

        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Commit(); };
        _refreshers.Add(() => box.Text = _settings.Hotkey);

        if (card.Children.Count > 0) card.Children.Add(Separator());

        var grid = new Grid { Margin = new Thickness(16, 13, 16, 13) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(new TextBlock
        {
            Text = "Показать или скрыть",
            Style = (Style)FindResource("RowLabel"),
        });
        text.Children.Add(status);

        Grid.SetColumn(text, 0);
        Grid.SetColumn(box, 1);
        grid.Children.Add(text);
        grid.Children.Add(box);
        card.Children.Add(grid);
    }

    private void AppListRow(Panel card)
    {
        var box = new TextBox
        {
            Text = string.Join(Environment.NewLine, _settings.CollapseApps),
            AcceptsReturn = true,
            Height = 132,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(16, 14, 16, 0),
            FontSize = 13,
        };

        void Commit()
        {
            if (_updating) return;
            _updating = true;
            _settings.CollapseApps = box.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            box.Text = string.Join(Environment.NewLine, _settings.CollapseApps);
            _updating = false;
        }

        box.LostFocus += (_, _) => Commit();
        _refreshers.Add(() => box.Text = string.Join(Environment.NewLine, _settings.CollapseApps));

        var add = new Button
        {
            Content = "Добавить из запущенных…",
            Style = (Style)FindResource("Action"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(16, 10, 16, 14),
        };
        add.Click += (_, _) =>
        {
            var picked = RunningAppPicker.Pick(this);
            if (picked is null) return;

            box.Text = string.Join(Environment.NewLine,
                box.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Append(picked));
            Commit();
        };

        card.Children.Add(new TextBlock
        {
            Text = "Имя процесса без .exe, по одному в строке.",
            Style = (Style)FindResource("RowHint"),
            Margin = new Thickness(16, 14, 16, 0),
        });
        card.Children.Add(box);
        card.Children.Add(add);
    }

    private void InfoRow(Panel card, string label, string value)
    {
        var text = new TextBlock
        {
            Text = value,
            Foreground = (Brush)FindResource("FgMuted"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 340,
            TextAlignment = TextAlignment.Right,
        };
        Row(card, label, null, text);
    }

    // ==================== Служебное ====================

    private void Apply(Action change)
    {
        if (_updating) return;
        _updating = true;
        try { change(); }
        finally { _updating = false; }

        foreach (var refresh in _refreshers.ToArray())
        {
            // Обновление предпросмотра не должно ронять окно из-за одной строки.
            try { refresh(); } catch (Exception ex) { Log.Write("Предпросмотр: " + ex.Message); }
        }
    }

    private static Color FromArgb(uint value) => Color.FromArgb(
        (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);

    private static string ToHex(uint value) => "#" + value.ToString("X8");

    private static bool TryParseHex(string text, out uint value)
    {
        text = text.Trim().TrimStart('#');
        if (text.Length == 6) text = "FF" + text;
        return uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out value)
               && text.Length == 8;
    }

    private void OnOpenFile(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings.Save();
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Settings.FilePath}\""));
        }
        catch (Exception ex) { Log.Write("Не удалось открыть файл настроек: " + ex.Message); }
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Вернуть все настройки к значениям по умолчанию?",
            "Сброс настроек", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (answer != MessageBoxResult.OK) return;

        _settings.ResetToDefaults();
        OnSectionChanged(this, null!);
    }
}

/// <summary>
/// Отрисовка окна настроек в PNG.
///
/// Нужна, чтобы вёрстку можно было проверить глазами так же, как снимки
/// островка: WPF умеет рисовать себя в растр, и для этого не нужен ни экран,
/// ни скриншот.
/// </summary>
internal static class SettingsPreview
{
    public static void Render(Settings settings, string directory)
    {
        Directory.CreateDirectory(directory);

        var thread = new Thread(() =>
        {
            var window = new SettingsWindow(settings)
            {
                // Показываем далеко за пределами экрана: окно должно быть
                // «живым», иначе разметка не посчитается, но мелькать ему незачем.
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -30000,
                Top = -30000,
                ShowInTaskbar = false,

                // Высоко, чтобы поместился весь раздел: то, что скрыто прокруткой,
                // на снимке не проверить, а проверять надо всё.
                Height = 1700,
            };
            window.Show();

            for (var section = 0; section < window.Nav.Items.Count; section++)
            {
                window.Nav.SelectedIndex = section;
                window.UpdateLayout();
                Pump();

                var name = window.Nav.Items[section]?.ToString() ?? section.ToString();

                window.ContentScroll.ScrollToTop();
                Pump();
                Save(window, Path.Combine(directory, $"{section + 1:00}-{name}-верх.png"));

                // Нижнюю часть раздела иначе не проверить: окно ограничено
                // высотой экрана, а карточки уходят под прокрутку.
                window.ContentScroll.ScrollToEnd();
                Pump();
                if (window.ContentScroll.VerticalOffset > 1)
                    Save(window, Path.Combine(directory, $"{section + 1:00}-{name}-низ.png"));

                // Раскрытый список живёт в Popup — это отдельное дерево, и
                // наследование цвета туда доходит не всегда. Проверяем отдельно.
                window.ContentScroll.ScrollToTop();
                Pump();
                if (FindDescendant<ComboBox>(window.ContentHost) is { } combo)
                {
                    combo.IsDropDownOpen = true;
                    Pump();

                    // Popup — отдельное окно, и снимок основного его не захватит.
                    // Рисуем содержимое списка напрямую.
                    if (FindDescendant<System.Windows.Controls.Primitives.Popup>(combo)
                        is { Child: FrameworkElement child })
                    {
                        SaveElement(child, Path.Combine(directory, $"{section + 1:00}-{name}-список.png"));
                    }

                    combo.IsDropDownOpen = false;
                    Pump();
                }
            }

            // Диалог выбора приложений — отдельное окно со своими стилями,
            // проверяем и его.
            var picker = RunningAppPicker.BuildForPreview();
            picker.WindowStartupLocation = WindowStartupLocation.Manual;
            picker.Left = -30000;
            picker.Top = -30000;
            picker.ShowInTaskbar = false;
            picker.Show();
            Pump();
            Save(picker, Path.Combine(directory, "06-Выбор приложения.png"));
            picker.Close();

            window.Close();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(60));
    }

    /// <summary>Снимок отдельного элемента — например содержимого всплывающего списка.</summary>
    private static void SaveElement(FrameworkElement element, string path)
    {
        var width = (int)Math.Ceiling(element.ActualWidth);
        var height = (int)Math.Ceiling(element.ActualHeight);
        if (width <= 0 || height <= 0) return;

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(element);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));

        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T found) return found;
            if (FindDescendant<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    /// <summary>Прогоняет очередь диспетчера, чтобы разметка успела посчитаться.</summary>
    private static void Pump()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ContextIdle,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    /// <summary>
    /// Пишет снимок один к одному с экраном.
    ///
    /// Двойной масштаб здесь вреден: мелкий текст на нём выглядит чётким
    /// всегда, и проблема с читаемостью на обычном экране остаётся невидимой.
    /// </summary>
    private static void Save(Window window, string path)
    {
        const double scale = 1.0;
        var width = (int)(window.ActualWidth * scale);
        var height = (int)(window.ActualHeight * scale);
        if (width <= 0 || height <= 0) return;

        var target = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        target.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));

        using var file = File.Create(path);
        encoder.Save(file);
    }
}

internal static class AppInfo
{
    public static string Version =>
        typeof(AppInfo).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";
}
