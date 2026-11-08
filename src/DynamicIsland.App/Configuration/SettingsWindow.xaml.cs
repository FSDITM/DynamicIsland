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
internal sealed record NavEntry(string Title, string Glyph)
{
    public override string ToString() => Title;
}

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
        Platform.DarkTitleBar.Apply(this);

        ApplyBundledFont();
        LoadLogo();
        VersionLabel.Text = "версия " + AppInfo.Version;

        _sections =
        [
            ("Вид", "Размеры, цвета и форма островка.", BuildAppearance),
            ("Поведение", "Когда островок раскрывается, сворачивается и уходит с глаз.", BuildBehaviour),
            ("Расположение", "На каком экране и в каком месте он живёт.", BuildPlacement),
            ("Содержимое", "Что показывать и какой клавишей вызывать.", BuildContent),
            ("Панель и события", "Вкладки, события в пилюле, звук и тема.", BuildFeatures),
            ("Уведомления", "Сообщения из Telegram и других приложений прямо на острове.", BuildNotifications),
            ("Снимки и запись", "Куда сохранять и как снимать экран.", BuildCapture),
            ("Погода", "Город, значки и предупреждения.", BuildWeatherSection),
            ("Горячие клавиши", "Глобальные сочетания, работают в любом приложении.", BuildHotkeys),
            ("О программе", "Откуда взялся этот островок.", BuildAbout),
        ];

        string[] glyphs = ["\uE790", "\uE713", "\uE707", "\uE8A5", "\uE8A9", "", "\uE722", "\uE753", "\uE765", "\uE946"];
        for (var i = 0; i < _sections.Count; i++)
            Nav.Items.Add(new NavEntry(_sections[i].Title, glyphs[Math.Min(i, glyphs.Length - 1)]));

        // Пункт меню: значок из системного шрифта значков и название. Эмодзи тут нет сознательно:
        // цветные картинки в каждой строке сразу делают окно похожим на сгенерированное.
        var item = new FrameworkElementFactory(typeof(StackPanel));
        item.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var icon = new FrameworkElementFactory(typeof(TextBlock));
        icon.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(NavEntry.Glyph)));
        icon.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"));
        icon.SetValue(TextBlock.FontSizeProperty, 15.0);
        icon.SetValue(TextBlock.WidthProperty, 26.0);
        icon.SetValue(TextBlock.ForegroundProperty, (Brush)FindResource("FgMuted"));
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(NavEntry.Title)));
        item.AppendChild(icon);
        item.AppendChild(label);
        Nav.ItemTemplate = new DataTemplate { VisualTree = item };
        Nav.SelectedIndex = 0;
    }

    /// <summary>
    /// Подключает вшитый Rubik. В системе его нет, поэтому по имени он не
    /// найдётся — нужен путь к папке со шрифтом.
    /// </summary>
    private void ApplyBundledFont()
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "Resources", "Fonts");
            if (!Directory.Exists(dir)) return;

            var family = new FontFamily(new Uri(dir + Path.DirectorySeparatorChar), "./#Rubik");
            if (family.FamilyNames.Count > 0) FontFamily = family;
        }
        catch (Exception ex)
        {
            Log.Write("Шрифт окна настроек не подключился: " + ex.Message);
        }
    }

    /// <summary>
    /// Берёт из .ico самый крупный кадр.
    ///
    /// BitmapImage отдаёт из многокадрового .ico первый попавшийся — обычно
    /// 16×16. Растянутый до 38 точек, да ещё на экране со 150%, он
    /// превращался в кашу из пикселей.
    /// </summary>
    private void LoadLogo()
    {
        try
        {
            if (!File.Exists(TrayIcon.IconPath)) return;

            using var stream = File.OpenRead(TrayIcon.IconPath);
            var decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

            BitmapFrame? best = null;
            foreach (var frame in decoder.Frames)
                if (best is null || frame.PixelWidth > best.PixelWidth) best = frame;

            AppLogo.Source = best;

            // Кадр крупнее места, куда его ставят, поэтому важно, как он ужимается.
            RenderOptions.SetBitmapScalingMode(AppLogo, BitmapScalingMode.HighQuality);
        }
        catch (Exception ex)
        {
            Log.Write("Логотип не загрузился: " + ex.Message);
        }
    }

    private void OnSectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedIndex < 0) return;

        // Выбрал раздел в меню — поиск закончен.
        if (SearchBox.Text.Length > 0)
        {
            _searchSuppress = true;
            SearchBox.Text = "";
            _searchSuppress = false;
            DropSearchCache();
            SearchHint.Visibility = Visibility.Visible;
            SearchClear.Visibility = Visibility.Collapsed;
        }
        _lastSection = Nav.SelectedIndex;

        _refreshers.Clear();
        ContentHost.Children.Clear();

        var (title, subtitle, build) = _sections[Nav.SelectedIndex];
        ContentHost.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Title") });
        ContentHost.Children.Add(new TextBlock { Text = subtitle, Style = (Style)FindResource("Subtitle") });

        build(ContentHost);
    }

    // ==================== Поиск ====================

    /// <summary>Слова, по которым находится раздел, даже если их нет в названиях строк.</summary>
    private static readonly Dictionary<string, string> Keywords = new()
    {
        ["Вид"] = "цвет фон размер ширина высота скругление тень прозрачность акцент эквалайзер иконки эмодзи тема color",
        ["Поведение"] = "раскрытие задержка анимация полноэкранный сворачивать браузер скорость",
        ["Расположение"] = "монитор экран позиция положение место",
        ["Содержимое"] = "часы дата батарея шрифт автозапуск автозагрузка метрики font",
        ["Панель и события"] = "вкладки буфер обмена микрофон камера caps lock vpn steam загрузки раскладка тема масштаб",
        ["Уведомления"] = "telegram телеграм discord whatsapp viber signal мессенджер баннер сообщения",
        ["Снимки и запись"] = "скриншот видео gif mp4 папка запись screenshot",
        ["Погода"] = "город температура дождь снег фаренгейт weather",
        ["Горячие клавиши"] = "hotkey сочетания комбинации клавиши ctrl alt",
        ["О программе"] = "версия лицензия автор",
    };

    private bool _searchSuppress;
    private int _lastSection;

    private void OnSearchClear(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    private void OnSearchKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape && SearchBox.Text.Length > 0)
        {
            SearchBox.Clear();
            e.Handled = true;
        }
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == System.Windows.Input.Key.F &&
            (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchClear.Visibility = SearchBox.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (_searchSuppress) return;

        if (query.Length == 0)
        {
            DropSearchCache();
            // Вернуться в раздел, где был. Если выбор тот же, событие не придёт — строим вручную.
            if (Nav.SelectedIndex == _lastSection) OnSectionChanged(this, null!);
            else Nav.SelectedIndex = _lastSection;
            return;
        }

        RunSearch(query);
    }

    private List<(int Section, int Group, string? Caption, Grid Row)>? _searchCache;
    private List<Action>? _searchRefreshers;
    private readonly List<StackPanel> _shownCards = [];

    private void DropSearchCache()
    {
        _searchCache = null;
        _searchRefreshers = null;
        _shownCards.Clear();
    }

    /// <summary>Собирает все разделы во временные панели и забирает из них строки с живыми контролами.</summary>
    private void BuildSearchCache()
    {
        // Обновлялки открытого раздела не трогаем: кэш можно строить заранее, пока ещё виден раздел.
        var saved = _refreshers.ToList();
        _refreshers.Clear();
        var cache = new List<(int, int, string?, Grid)>();
        for (var s = 0; s < _sections.Count; s++)
        {
            var temp = new StackPanel();
            try { _sections[s].Build(temp); }
            catch (Exception ex) { Log.Write("Поиск: раздел не собрался: " + ex.Message); continue; }

            string? caption = null;
            var group = 0;
            foreach (var child in temp.Children.Cast<UIElement>().ToList())
            {
                if (child is TextBlock { Style: var st } tb && ReferenceEquals(st, FindResource("GroupCaption")))
                {
                    caption = tb.Text;
                }
                else if (child is Border { Child: StackPanel inner })
                {
                    foreach (var row in inner.Children.OfType<Grid>().ToList())
                    {
                        inner.Children.Remove(row);
                        cache.Add((s, group, caption, row));
                    }
                    group++;
                    caption = null;
                }
            }
        }

        _searchCache = cache;
        _searchRefreshers = [.. _refreshers];
        _refreshers.Clear();
        _refreshers.AddRange(saved);
    }

    /// <summary>Пока человек тянется к клавиатуре, кэш поиска уже готовится — первая буква не подвисает.</summary>
    private void OnSearchFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (_searchCache is not null) return;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
        {
            if (_searchCache is null && SearchBox.Text.Length == 0) BuildSearchCache();
        });
    }

    /// <summary>Показывает подходящие строки из кэша; сами контролы остаются живыми.</summary>
    private void RunSearch(string query)
    {
        _searchSuppress = true;
        Nav.SelectedIndex = -1;
        _searchSuppress = false;

        // Разделы собираются один раз за поиск: сборка всех десяти занимала ~350 мс на каждую букву.
        if (_searchCache is null) BuildSearchCache();
        _refreshers.Clear();
        _refreshers.AddRange(_searchRefreshers!);

        // Строки живут в карточках выдачи; перед новой выдачей их надо оторвать от старых.
        foreach (var shown in _shownCards) shown.Children.Clear();
        _shownCards.Clear();
        ContentHost.Children.Clear();

        var title = new TextBlock { Text = "Поиск", Style = (Style)FindResource("Title") };
        var subtitle = new TextBlock { Style = (Style)FindResource("Subtitle") };
        ContentHost.Children.Add(title);
        ContentHost.Children.Add(subtitle);

        var total = 0;
        for (var s = 0; s < _sections.Count; s++)
        {
            // Запрос совпал с названием или ключевым словом раздела — показываем раздел целиком.
            var sectionHit = _sections[s].Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                             (Keywords.TryGetValue(_sections[s].Title, out var kw) &&
                              kw.Contains(query, StringComparison.CurrentCultureIgnoreCase));

            var groups = _searchCache!.Where(x => x.Section == s)
                .GroupBy(x => x.Group)
                .Select(g => (Caption: g.First().Caption, Rows: g
                    .Where(x => sectionHit || (x.Row.Tag as string ?? "").Contains(query, StringComparison.CurrentCultureIgnoreCase))
                    .Select(x => x.Row).ToList()))
                .Where(g => g.Rows.Count > 0)
                .ToList();
            if (groups.Count == 0) continue;

            var index = s;
            var link = new TextBlock
            {
                Text = _sections[s].Title + "  ›",
                Foreground = (Brush)FindResource("Fg"),
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(0, 4, 0, 10),
                ToolTip = "Открыть раздел",
            };
            link.MouseLeftButtonDown += (_, _) => Nav.SelectedIndex = index;
            ContentHost.Children.Add(link);

            foreach (var (cap, rows) in groups)
            {
                var card = Card(ContentHost, cap);
                _shownCards.Add(card);
                foreach (var row in rows)
                {
                    if (card.Children.Count > 0) card.Children.Add(Separator());
                    card.Children.Add(row);
                    total++;
                }
            }
        }

        subtitle.Text = total == 0
            ? $"По запросу «{query}» ничего не нашлось. Попробуйте другое слово."
            : $"Найдено: {total}. Нажмите на название раздела, чтобы открыть его целиком.";
        ContentScroll.ScrollToTop();
    }

    // ==================== Разделы ====================

    /// <summary>Какое состояние островка сейчас показывает и настраивает раздел «Вид».</summary>
    private int _shownState = 1;

    /// <summary>
    /// Принудительный масштаб для снимка. В обычной работе null — берётся
    /// настоящий масштаб экрана.
    /// </summary>
    internal static double? DpiOverrideForPreview { get; set; }

    /// <summary>
    /// Режим отрисовки текста зависит от масштаба экрана.
    ///
    /// Display прибивает глифы к пиксельной сетке: на 100% это спасает мелкий
    /// текст от размытия, но на 150% делает его ступенчатым. Ideal наоборот —
    /// размывает на 100% и рисует гладко там, где пикселей хватает.
    /// Одного правильного значения нет, поэтому выбираем по месту.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var scale = DpiOverrideForPreview ?? VisualTreeHelper.GetDpi(this).DpiScaleX;
        TextOptions.SetTextFormattingMode(this,
            scale >= 1.25 ? TextFormattingMode.Ideal : TextFormattingMode.Display);
    }

    /// <summary>Переключает состояние для снимка — все три надо проверить глазами.</summary>
    internal void ShowStateForPreview(int state)
    {
        _shownState = state;
        OnSectionChanged(this, null!);
    }

    private void BuildAppearance(Panel host)
    {
        // Переключатель состояний вместо шести ползунков подряд: раньше было
        // непонятно, какой из них на что влияет, и увидеть результат можно было
        // только уйдя из окна. Теперь выбираешь состояние — видишь его и правишь
        // только его размеры.
        host.Children.Add(BuildStateSelector());
        host.Children.Add(BuildPreview());

        var size = Card(host, "Размер " + _shownState switch
        {
            0 => "полоски",
            2 => "раскрытого",
            _ => "в покое",
        });

        switch (_shownState)
        {
            case 0:
                SliderRow(size, "Ширина", 60, 600, 5,
                    () => _settings.NotchWidth, v => _settings.NotchWidth = v, "{0:0} px");
                SliderRow(size, "Толщина", 2, 40, 1,
                    () => _settings.NotchHeight, v => _settings.NotchHeight = v, "{0:0} px",
                    "Эти пиксели перекрывают окно под островком — чем тоньше, тем меньше мешает.");
                break;

            case 2:
                SliderRow(size, "Ширина", 260, 900, 5,
                    () => _settings.ExpandedWidth, v => _settings.ExpandedWidth = v, "{0:0} px");
                SliderRow(size, "Высота", 80, 400, 2,
                    () => _settings.ExpandedHeight, v => _settings.ExpandedHeight = v, "{0:0} px",
                    "Сюда помещается обложка, название трека и полоса перемотки.");
                break;

            default:
                SliderRow(size, "Ширина", 120, 700, 5,
                    () => _settings.RestWidth, v => _settings.RestWidth = v, "{0:0} px");
                SliderRow(size, "Высота", 16, 120, 1,
                    () => _settings.RestHeight, v => _settings.RestHeight = v, "{0:0} px");
                break;
        }

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
        ToggleRow(shape, "Акцент из обложки", () => _settings.AccentFromArt, v => _settings.AccentFromArt = v,
            "Цвет интерфейса берётся с обложки играющего трека. Выключите, чтобы всегда брался ваш цвет ниже.");
        ColorRow(shape, "Цвет интерфейса", () => _settings.AccentColor, v => _settings.AccentColor = v);
        ColorRow(shape, "Цвет эквалайзера", () => _settings.EqualizerColor, v => _settings.EqualizerColor = v, allowAuto: true);
        ColorRow(shape, "Цвет значков", () => _settings.IconColor, v => _settings.IconColor = v, allowAuto: true);

        var tabs = Card(host, "Вкладки");
        ChoiceRow(tabs, "Чем подписаны", ["Значки", "Эмодзи"],
            () => (int)_settings.TabStyle, v => _settings.TabStyle = (TabStyle)v,
            "Значки — тонкие контурные; эмодзи — цветные из системного шрифта.");
        TextRow(tabs, "Эмодзи вкладок", () => _settings.TabEmoji, v => _settings.TabEmoji = v,
            "Восемь через пробел: Главная, Буфер, Полка, Текст, Заметки, Управление, Погода, Инструменты. Win+. открывает панель эмодзи.");
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
            "Можно указать свой, например SF Pro, если он установлен в системе.", "Rubik (вшитый)");

        var system = Card(host, "Система");
        HotkeyRow(system);
        ToggleRow(system, "Запускать при входе в систему",
            () => _settings.RunOnStartup, v => _settings.RunOnStartup = v);
        ToggleRow(system, "Показывать метрики",
            () => _settings.ShowMetrics, v => _settings.ShowMetrics = v,
            "Кадры, время отрисовки и активное приложение — поверх островка.");
    }

    private void BuildFeatures(Panel host)
    {
        var panel = Card(host, "Панель с вкладками");
        SliderRow(panel, "Ширина панели", 380, 900, 10, () => _settings.PanelWidth, v => _settings.PanelWidth = v, "{0:0} px");
        SliderRow(panel, "Высота панели", 220, 600, 10, () => _settings.PanelHeight, v => _settings.PanelHeight = v, "{0:0} px");
        SliderRow(panel, "Масштаб островка", 0.8f, 2f, 0.05f, () => _settings.IslandScale, v => _settings.IslandScale = v, "{0:0.00}×",
            "Во всём: пилюля, панель, карточки. Удобно на больших мониторах.");
        ChoiceRow(panel, "Тема островка", ["Тёмная", "Светлая", "Как в Windows"],
            () => (int)_settings.Theme, v => _settings.Theme = (IslandTheme)v);

        var clip = Card(host, "Буфер обмена");
        ToggleRow(clip, "Хранить историю", () => _settings.ClipboardHistory, v => _settings.ClipboardHistory = v,
            "Пароли из менеджеров паролей в историю не попадают. Закреплённые записи переживают перезапуск.");
        SliderRow(clip, "Сколько записей хранить", 10, 200, 5, () => _settings.ClipboardLimit, v => _settings.ClipboardLimit = (int)v, "{0:0}");

        var events = Card(host, "События в пилюле");
        ToggleRow(events, "Микрофон и камера в работе", () => _settings.EventMicCam, v => _settings.EventMicCam = v);
        ToggleRow(events, "Загрузки файлов", () => _settings.EventDownloads, v => _settings.EventDownloads = v,
            "Следит за папкой «Загрузки» и сообщает, когда файл готов.");
        ToggleRow(events, "Раскладка клавиатуры", () => _settings.EventLayout, v => _settings.EventLayout = v);
        ToggleRow(events, "Caps Lock", () => _settings.EventCaps, v => _settings.EventCaps = v);
        ToggleRow(events, "VPN: страна и IP", () => _settings.EventVpn, v => _settings.EventVpn = v,
            "Когда VPN поднимается, один запрос на ipwho.is показывает страну и адрес.");
        ToggleRow(events, "Закачки Steam", () => _settings.EventSteam, v => _settings.EventSteam = v);
        ToggleRow(events, "Запись экрана", () => _settings.EventRecording, v => _settings.EventRecording = v);
        ToggleRow(events, "Громкость колесом мыши", () => _settings.WheelVolume, v => _settings.WheelVolume = v,
            "Колесо над раскрытой «Главной» меняет громкость.");
        ToggleRow(events, "Слова песни в такт", () => _settings.ShowLyrics, v => _settings.ShowLyrics = v,
            "Слова берутся из открытой базы LRCLIB, только для играющего трека.");

        var sound = Card(host, "Звук");
        var audio = new Features.AudioService();
        var outputs = new List<string> { "Как выбрано в Windows" };
        outputs.AddRange(audio.OutputNames());
        var inputs = new List<string> { "Как выбрано в Windows" };
        inputs.AddRange(audio.InputNames());
        audio.Dispose();

        ChoiceRow(sound, "Чью громкость менять", outputs,
            () => Math.Max(0, outputs.IndexOf(_settings.VolumeDevice)),
            v => _settings.VolumeDevice = v == 0 ? "" : outputs[v]);
        ChoiceRow(sound, "Какой микрофон выключать", inputs,
            () => Math.Max(0, inputs.IndexOf(_settings.MicDevice)),
            v => _settings.MicDevice = v == 0 ? "" : inputs[v],
            "Нужно, если у вас виртуальный микшер: звук внутри системы от этого не переключается.");

        var cmd = Card(host, "Командная строка");
        TextRow(cmd, "Поиск в интернете", () => _settings.SearchUrl, v => _settings.SearchUrl = v,
            "Адрес с {0} вместо запроса. Например https://www.google.com/search?q={0}");
    }

    private void BuildNotifications(Panel host)
    {
        var general = Card(host, "Уведомления Windows");
        ToggleRow(general, "Показывать на острове", () => _settings.ShowNotifications, v => _settings.ShowNotifications = v,
            "Читаются уведомления из центра уведомлений. Если пусто, разрешите доступ: Параметры → Конфиденциальность → Уведомления.");
        TextRow(general, "Не показывать от приложений", () => string.Join(", ", _settings.NotificationBlocklist),
            v => _settings.NotificationBlocklist = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            "Части названий через запятую.", "Например: Steam, Realtek");

        var only = Card(host, "Только на острове");
        ToggleRow(only, "Убрать системные баннеры мессенджеров", () => _settings.IslandOnlyNotifications,
            v => _settings.IslandOnlyNotifications = v,
            "Сообщения не дублируются в углу экрана. Они по-прежнему приходят в центр уведомлений, оттуда их читает остров. При выходе из острова баннеры возвращаются.");
        TextRow(only, "Для каких приложений", () => string.Join(", ", _settings.IslandOnlyApps),
            v => _settings.IslandOnlyApps = Features.NotificationSetup.ParseList(v),
            "Для них же остров включает уведомления Windows.", "Через запятую");

        var tg = Card(host, "Telegram");
        InfoRow(tg, "Как работает",
            "Ничего настраивать и входить не нужно: остров читает обычные уведомления Windows и показывает их с аватаром.");
        InfoRow(tg, "Когда нет карточки",
            "Telegram не присылает уведомление, пока его окно активно, и при включённом режиме «Не беспокоить».");
    }

    private void BuildCapture(Panel host)
    {
        var folders = Card(host, "Куда сохранять");
        TextRow(folders, "Папка снимков", () => _settings.ScreenshotFolder, v => _settings.ScreenshotFolder = v,
            "Куда падают снимки экрана.", "Изображения\\DynamicIsland", folder: true);
        TextRow(folders, "Папка записей", () => _settings.RecordFolder, v => _settings.RecordFolder = v,
            "Куда падают записи экрана.", "Видео\\DynamicIsland", folder: true);

        var record = Card(host, "Запись экрана");
        ChoiceRow(record, "Кадров в секунду", ["30", "60"],
            () => _settings.RecordFps >= 60 ? 1 : 0, v => _settings.RecordFps = v == 1 ? 60 : 30);
        ToggleRow(record, "Высокое качество", () => _settings.RecordHighQuality, v => _settings.RecordHighQuality = v,
            "Больше размер файла, чётче картинка.");
        ToggleRow(record, "Писать в GIF вместо MP4", () => _settings.RecordGif, v => _settings.RecordGif = v,
            "GIF тяжелее и без звука; запись до одной минуты, 12 кадров в секунду.");

        var visible = Card(host, "Видимость островка");
        ToggleRow(visible, "Показывать островок на снимках и в записи",
            () => _settings.IslandInCapture, v => _settings.IslandInCapture = v,
            "По умолчанию островок на снимках экрана отсутствует.");
    }

    private void BuildWeatherSection(Panel host)
    {
        var card = Card(host, "Город");
        TextRow(card, "Название города", () => _settings.WeatherCity, v => _settings.WeatherCity = v,
            "Город ищется по названию, по сети он не угадывается. Можно и из командной строки: «город Казань».", "Например, Казань");
        InfoRow(card, "Найдено", _settings.WeatherCityResolved.Length > 0 ? _settings.WeatherCityResolved : "—");

        var show = Card(host, "Как показывать");
        ToggleRow(show, "Температура в пилюле", () => _settings.WeatherInPill, v => _settings.WeatherInPill = v,
            "Показывается, пока нет музыки и других событий.");
        ToggleRow(show, "Предупреждать о дожде и снеге", () => _settings.RainWarning, v => _settings.RainWarning = v,
            "Карточка за час-два до осадков.");
        ToggleRow(show, "Градусы Фаренгейта", () => _settings.Fahrenheit, v => _settings.Fahrenheit = v);

        var source = Card(host, "Источник");
        InfoRow(source, "Прогноз", "Open-Meteo — без ключа и регистрации");
    }

    private void BuildHotkeys(Panel host)
    {
        var card = Card(host, "Сочетания клавиш");
        HotkeyField(card, "Показать или скрыть островок", () => _settings.Hotkey, v => _settings.Hotkey = v);
        HotkeyField(card, "Командная строка", () => _settings.HotkeyCommandBar, v => _settings.HotkeyCommandBar = v);
        HotkeyField(card, "Снимок области", () => _settings.HotkeyScreenshot, v => _settings.HotkeyScreenshot = v);
        HotkeyField(card, "Текст с экрана", () => _settings.HotkeyOcr, v => _settings.HotkeyOcr = v);
        HotkeyField(card, "QR с экрана", () => _settings.HotkeyQr, v => _settings.HotkeyQr = v);
        HotkeyField(card, "Пипетка", () => _settings.HotkeyColor, v => _settings.HotkeyColor = v);
        HotkeyField(card, "Запись экрана", () => _settings.HotkeyRecord, v => _settings.HotkeyRecord = v);
        HotkeyField(card, "Вставить без форматирования", () => _settings.HotkeyPastePlain, v => _settings.HotkeyPastePlain = v);

        host.Children.Add(new TextBlock
        {
            Text = "Нажмите на поле и сами нажмите нужные клавиши. Backspace выключает сочетание, Esc отменяет.",
            Style = (Style)FindResource("RowHint"),
            Margin = new Thickness(2, 0, 0, 0),
        });
    }

    /// <summary>Имя клавиши в том виде, который понимает Hotkey.TryParse; null — клавиша не годится.</summary>
    private static string? KeyName(System.Windows.Input.Key key)
    {
        var vk = System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
        if (vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A) return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x70 + 1);
        return vk switch
        {
            0x20 => "Space", 0x09 => "Tab", 0x2D => "Insert", 0x2E => "Delete", 0x24 => "Home", 0x23 => "End",
            0x21 => "PageUp", 0x22 => "PageDown", 0x26 => "Up", 0x28 => "Down", 0x25 => "Left", 0x27 => "Right",
            _ => null,
        };
    }

    private void HotkeyField(Panel card, string label, Func<string> get, Action<string> set)
    {
        // Поле «слушает» клавиши: набирать сочетание текстом неудобно и легко ошибиться.
        var box = new TextBox { Text = get(), Width = 200, IsReadOnly = true, Cursor = System.Windows.Input.Cursors.Hand };
        var ph = new TextBlock
        {
            Text = "Выключено", Foreground = (Brush)FindResource("FgMuted"), FontSize = 14,
            Margin = new Thickness(10, 0, 0, 0), IsHitTestVisible = false,
        };
        var holder = new Grid();
        holder.Children.Add(box);
        holder.Children.Add(ph);
        void Sync() => ph.Visibility = box.Text.Length == 0 && !box.IsKeyboardFocused ? Visibility.Visible : Visibility.Collapsed;

        string before = get();
        box.GotKeyboardFocus += (_, _) => { before = box.Text; box.Text = ""; ph.Text = "Нажмите сочетание…"; Sync(); ph.Visibility = Visibility.Visible; };
        box.LostKeyboardFocus += (_, _) => { if (box.Text.Length == 0) box.Text = before; ph.Text = "Выключено"; Sync(); };
        box.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
            if (key is System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl or
                System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt or
                System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift or
                System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin) return;

            var mods = System.Windows.Input.Keyboard.Modifiers;
            if (key == System.Windows.Input.Key.Escape && mods == 0) { box.Text = before; System.Windows.Input.Keyboard.ClearFocus(); return; }
            if (key == System.Windows.Input.Key.Back && mods == 0)
            {
                box.Text = "";
                before = "";
                Apply(() => set(""));
                System.Windows.Input.Keyboard.ClearFocus();
                return;
            }

            var name = KeyName(key);
            var isF = key >= System.Windows.Input.Key.F1 && key <= System.Windows.Input.Key.F24;
            if (name is null || (mods == 0 && !isF))
            {
                ph.Text = "Добавьте Ctrl, Alt или Shift";
                ph.Visibility = Visibility.Visible;
                return;
            }

            var parts = new List<string>();
            if ((mods & System.Windows.Input.ModifierKeys.Control) != 0) parts.Add("Ctrl");
            if ((mods & System.Windows.Input.ModifierKeys.Alt) != 0) parts.Add("Alt");
            if ((mods & System.Windows.Input.ModifierKeys.Shift) != 0) parts.Add("Shift");
            if ((mods & System.Windows.Input.ModifierKeys.Windows) != 0) parts.Add("Win");
            parts.Add(name);
            var combo = string.Join("+", parts);
            box.Text = combo;
            before = combo;
            Apply(() => set(combo));
            System.Windows.Input.Keyboard.ClearFocus();
        };
        _refreshers.Add(() => { box.Text = get(); Sync(); });
        Sync();

        Row(card, label, null, holder);
    }

    private void BuildAbout(Panel host)
    {
        var card = Card(host, null);

        InfoRow(card, "Версия", AppInfo.Version);
        InfoRow(card, "Отрисовка", "Direct3D 12 + DirectComposition, Skia на GPU");
        InfoRow(card, "Шрифт", "Rubik (SIL Open Font License)");
        InfoRow(card, "Лицензия", "MIT");
        InfoRow(card, "Файл настроек", Settings.FilePath);

        var origin = Card(host, "Происхождение");
        var text = new TextBlock
        {
            Text = "Идея и первая реализация — DynamicWin Флориана Бутца. Исходники оригинала " +
                   "были удалены из репозитория в октябре 2025 года.\n\n" +
                   "Этот островок написан заново: окно размером с саму фигуру вместо " +
                   "полноэкранного, кадр не покидает видеопамять, а в покое поток спит и кадры " +
                   "не рисуются вовсе.\n\n" +
                   "Шрифт Rubik — Hubert and Fischer, Cyreal; SIL Open Font License.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("FgMuted"),
            FontSize = 13,
            Margin = new Thickness(16, 14, 16, 14),
            LineHeight = 19,
        };
        origin.Children.Add(text);
    }

    // ==================== Живой предпросмотр ====================

    /// <summary>Переключатель состояний островка — он же выбирает, что настраивать.</summary>
    private UIElement BuildStateSelector()
    {
        string[] names = ["Полоска", "В покое", "Раскрытый"];

        var grid = new Grid();
        for (var i = 0; i < names.Length; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (var i = 0; i < names.Length; i++)
        {
            var index = i;
            var selected = index == _shownState;

            var label = new TextBlock
            {
                Text = names[i],
                HorizontalAlignment = HorizontalAlignment.Left,
                Foreground = (Brush)FindResource(selected ? "Fg" : "FgMuted"),
                FontSize = 14,
                FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
            };

            var cell = new Border
            {
                Background = Brushes.Transparent,
                BorderBrush = selected ? (Brush)FindResource("Accent") : Brushes.Transparent,
                BorderThickness = new Thickness(0, 0, 0, 2),
                Padding = new Thickness(0, 8, 0, 9),
                Margin = new Thickness(0, 0, 14, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = label,
            };

            cell.MouseLeftButtonUp += (_, _) =>
            {
                if (_shownState == index) return;
                _shownState = index;
                OnSectionChanged(this, null!);
            };

            Grid.SetColumn(cell, index);
            grid.Children.Add(cell);
        }

        return new Border
        {
            BorderBrush = (Brush)FindResource("Line"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = grid,
            Margin = new Thickness(0, 0, 0, 18),
        };
    }

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
                Color.FromRgb(0x38, 0x33, 0x2B), Color.FromRgb(0x1B, 0x19, 0x15), 60),
            Child = new Canvas { Children = { island } },
            Margin = new Thickness(0, 0, 0, 6),
        };

        void Refresh()
        {
            // Макет — не пиксель в пиксель, а пропорция: реальный экран шире
            // макета примерно вдвое, и в таком масштабе островок различим.
            const double virtualScreenWidth = 900.0;
            var scale = screenWidth / virtualScreenWidth;

            var (logicalWidth, logicalHeight) = _shownState switch
            {
                0 => (_settings.NotchWidth, _settings.NotchHeight),
                2 => (_settings.ExpandedWidth, _settings.ExpandedHeight),
                _ => (_settings.RestWidth, _settings.RestHeight),
            };

            island.Width = Math.Max(8, logicalWidth * scale);
            island.Height = Math.Max(3, logicalHeight * scale);
            island.CornerRadius = new CornerRadius(
                Math.Min(island.Height / 2, _settings.CornerRadius * scale));
            island.Background = new SolidColorBrush(FromArgb(_settings.BackgroundColor))
            {
                Opacity = _settings.Opacity,
            };
            island.Child = MockContent(_shownState, island.Height);

            // В свёрнутом виде островок жмётся к кромке, в остальных отступает.
            var offset = _shownState == 0 ? 0 : _settings.TopOffset * scale;
            var top = _settings.Anchor == ScreenAnchor.Bottom
                ? screenHeight - offset - island.Height
                : offset;

            Canvas.SetTop(island, top);
            Canvas.SetLeft(island, (screenWidth - island.Width) / 2 + _settings.HorizontalOffset * scale);

            caption.Text = $"{logicalWidth:0} × {logicalHeight:0} логических пикселей";
        }

        Refresh();
        _refreshers.Add(Refresh);

        return new StackPanel
        {
            Margin = new Thickness(0, 0, 0, 22),
            Children = { screen, caption },
        };
    }

    /// <summary>
    /// Схематичное содержимое островка для предпросмотра.
    ///
    /// Именно схема, а не настоящий текст: в этом масштабе буквы вышли бы
    /// высотой в семь пикселей и превратились бы в грязь. Блоки же сразу
    /// показывают, что и где помещается.
    /// </summary>
    private UIElement? MockContent(int state, double islandHeight)
    {
        if (state == 0 || islandHeight < 10) return null;

        var accent = new SolidColorBrush(FromArgb(_settings.AccentColor));
        var bright = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xEE));
        var dim = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x84));

        static Border Block(Brush brush, double w, double h, double radius = 2) => new()
        {
            Width = w,
            Height = h,
            Background = brush,
            CornerRadius = new CornerRadius(radius),
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (state == 1)
        {
            // В покое: часы слева, заряд справа.
            var row = new Grid { Margin = new Thickness(islandHeight * 0.42, 0, islandHeight * 0.42, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var clock = Block(bright, islandHeight * 1.35, islandHeight * 0.30);
            var battery = Block(dim, islandHeight * 0.85, islandHeight * 0.26);
            Grid.SetColumn(clock, 0);
            Grid.SetColumn(battery, 2);
            row.Children.Add(clock);
            row.Children.Add(battery);
            return row;
        }

        // Раскрытый: обложка, две строки текста, три кнопки, полоса перемотки.
        var pad = islandHeight * 0.12;
        var artSize = islandHeight * 0.48;

        var art = new Border
        {
            Width = artSize,
            Height = artSize,
            Background = new LinearGradientBrush(
                Color.FromRgb(0xFF, 0x5E, 0x62), Color.FromRgb(0xFF, 0xC3, 0x71), 45),
            CornerRadius = new CornerRadius(artSize * 0.16),
            VerticalAlignment = VerticalAlignment.Top,
        };

        var lines = new StackPanel { Margin = new Thickness(pad, artSize * 0.12, 0, 0) };
        lines.Children.Add(Block(bright, islandHeight * 0.95, islandHeight * 0.085));
        lines.Children.Add(new Border { Height = pad * 0.5 });
        lines.Children.Add(Block(dim, islandHeight * 0.6, islandHeight * 0.07));

        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, artSize * 0.16, 0, 0),
        };
        for (var i = 0; i < 3; i++)
        {
            controls.Children.Add(Block(bright, islandHeight * 0.1, islandHeight * 0.1, islandHeight * 0.05));
            if (i < 2) controls.Children.Add(new Border { Width = pad * 0.8 });
        }

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(art, 0);
        Grid.SetColumn(lines, 1);
        Grid.SetColumn(controls, 2);
        top.Children.Add(art);
        top.Children.Add(lines);
        top.Children.Add(controls);

        // Полоса перемотки: заполненная часть — доля ширины, а не число пикселей,
        // иначе при узком островке она закрывала бы всю дорожку.
        var thickness = islandHeight * 0.035;
        var corner = new CornerRadius(thickness / 2);

        var seek = new Grid { Margin = new Thickness(0, pad, 0, 0) };
        seek.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(35, GridUnitType.Star) });
        seek.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(65, GridUnitType.Star) });

        var track = new Border { Height = thickness, Background = dim, CornerRadius = corner };
        Grid.SetColumnSpan(track, 2);

        var filled = new Border { Height = thickness, Background = accent, CornerRadius = corner };
        Grid.SetColumn(filled, 0);

        seek.Children.Add(track);
        seek.Children.Add(filled);

        return new StackPanel
        {
            Margin = new Thickness(pad * 1.4, pad * 1.2, pad * 1.4, pad * 1.2),
            Children = { top, seek },
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
            BorderBrush = (Brush)FindResource("Line"),
            BorderThickness = new Thickness(0, 1, 0, 1),
            Child = inner,
            Margin = new Thickness(0, 0, 0, 24),
        });
        return inner;
    }

    private Border Separator() => new()
    {
        Height = 1,
        Background = (Brush)FindResource("Line"),
        Margin = new Thickness(0),
    };

    private void Row(Panel card, string label, string? hint, UIElement control)
    {
        if (card.Children.Count > 0) card.Children.Add(Separator());

        // Прозрачный фон нужен, чтобы строка ловила наведение по всей ширине, а не только над текстом.
        var grid = new Grid { Margin = new Thickness(0), Tag = label + " " + hint, Background = Brushes.Transparent };
        // Отступы сверху и снизу — строками сетки, чтобы подсветка при наведении закрашивала всю строку.
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(13) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(13) });
        grid.MouseEnter += (_, _) => grid.Background = (Brush)FindResource("CardHover");
        grid.MouseLeave += (_, _) => grid.Background = Brushes.Transparent;
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 16, 0) };
        text.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("RowLabel") });
        if (hint is not null)
            text.Children.Add(new TextBlock { Text = hint, Style = (Style)FindResource("RowHint") });

        Grid.SetColumn(text, 0);
        Grid.SetColumn(control, 1);
        Grid.SetRow(text, 1);
        Grid.SetRow(control, 1);
        if (control is FrameworkElement fe) fe.Margin = new Thickness(fe.Margin.Left, fe.Margin.Top, fe.Margin.Right + 8, fe.Margin.Bottom);
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

    private void TextRow(Panel card, string label, Func<string> get, Action<string> set, string? hint = null,
                         string? placeholder = null, bool folder = false)
    {
        var box = new TextBox { Text = get(), Width = folder ? 210 : 260 };

        // Пустое поле без подсказки выглядит как тёмный прямоугольник — непонятно, что в него писать.
        // Ширина держателя равна ширине поля: длинная подсказка не должна растягивать строку.
        var holder = new Grid { Width = box.Width, VerticalAlignment = VerticalAlignment.Center };
        holder.Children.Add(box);
        var ph = new TextBlock
        {
            Text = placeholder ?? "", Foreground = (Brush)FindResource("FgMuted"), FontSize = 14,
            Margin = new Thickness(10, 0, 8, 0), IsHitTestVisible = false, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        holder.Children.Add(ph);
        void Sync() => ph.Visibility = placeholder is not null && box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) => Sync();
        Sync();

        box.LostFocus += (_, _) => Apply(() => set(box.Text));
        box.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) { Apply(() => set(box.Text)); System.Windows.Input.Keyboard.ClearFocus(); }
        };
        _refreshers.Add(() => box.Text = get());

        UIElement control = holder;
        if (folder)
        {
            var pick = new Button { Content = "Выбрать…", Style = (Style)FindResource("Action"), Padding = new Thickness(0, 7, 0, 7), Width = 100, Margin = new Thickness(8, 0, 0, 0) };
            pick.Click += (_, _) =>
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog { Title = label };
                if (box.Text.Length > 0 && Directory.Exists(box.Text)) dialog.InitialDirectory = box.Text;
                if (dialog.ShowDialog(this) == true)
                {
                    box.Text = dialog.FolderName;
                    Apply(() => set(dialog.FolderName));
                }
            };
            control = new StackPanel { Orientation = Orientation.Horizontal, Children = { holder, pick } };
        }

        Row(card, label, hint, control);
    }

    private void ColorRow(Panel card, string label, Func<uint> get, Action<uint> set, bool allowAuto = false)
    {
        var swatch = new Border
        {
            Width = 30,
            Height = 22,
            CornerRadius = new CornerRadius(6),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x55, 0x50, 0x48)),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(FromArgb(get())),
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

        // Нажатие на образец открывает палитру. Окно палитры кладём в тот же контейнер: так оно получает стили окна.
        var swatchHost = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        swatchHost.Children.Add(swatch);
        ColorPickerPopup.Attach(swatch, swatchHost,
            color => Apply(() =>
            {
                set(color);
                swatch.Background = new SolidColorBrush(FromArgb(color));
            }),
            get, allowAuto);

        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(swatchHost);
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

        var grid = new Grid { Margin = new Thickness(2, 13, 2, 13) };
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

    private static string ToHex(uint value) => value == 0 ? "авто" : "#" + value.ToString("X8");

    private static bool TryParseHex(string text, out uint value)
    {
        text = text.Trim().TrimStart('#');
        if (text.Length == 0 || text.StartsWith("авт", StringComparison.OrdinalIgnoreCase) || text.StartsWith("по ", StringComparison.OrdinalIgnoreCase) || text == "0")
        {
            value = 0;
            return true;
        }
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
    public static void Render(Settings settings, string directory, double scale = 1.0)
    {
        Directory.CreateDirectory(directory);

        PreviewScale = scale;
        SettingsWindow.DpiOverrideForPreview = scale;

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

                // У раздела «Вид» три состояния островка, и каждое рисуется
                // по-своему — снимок одного ничего не говорит про остальные.
                if (section == 0)
                {
                    foreach (var (state, label) in new[] { (0, "полоска"), (2, "раскрытый") })
                    {
                        window.ShowStateForPreview(state);
                        window.ContentScroll.ScrollToTop();
                        Pump();
                        Save(window, Path.Combine(directory, $"01-Вид-{label}.png"));
                    }
                    window.ShowStateForPreview(1);
                    Pump();
                }

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

            // Меню в трее рисует само приложение, значит и проверять его надо
            // снимком, а не на глаз.
            var menu = TrayMenu.BuildForPreview();
            menu.Left = -30000;
            menu.Top = -30000;
            menu.Show();
            Pump();
            Save(menu, Path.Combine(directory, "07-Меню в трее.png"));
            menu.Close();

            // Палитра цвета: открываем по образцу «Цвет интерфейса» и снимаем её содержимое.
            window.Nav.SelectedIndex = 0;
            window.UpdateLayout();
            Pump();
            var swatches = new List<Border>();
            void Collect(DependencyObject o)
            {
                if (o is Border { Width: 30, Height: 22 } b) swatches.Add(b);
                foreach (var child in LogicalTreeHelper.GetChildren(o).OfType<DependencyObject>()) Collect(child);
            }
            Collect(window.ContentHost);
            if (swatches.Count > 2)
            {
                var target = swatches[1];
                target.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                Pump();
                if (FindLogicalPopup(target.Parent) is { Child: FrameworkElement pickerChild })
                    SaveElement(pickerChild, Path.Combine(directory, "11-Палитра.png"));
            }

            // Поиск: выдача по слову и пустой результат.
            foreach (var (q, file) in new[] { ("уведом", "08-Поиск.png"), ("telegram", "10-Поиск-telegram.png"), ("яяяя", "09-Поиск-пусто.png") })
            {
                var sw = System.Diagnostics.Stopwatch.StartNew(); window.SearchBox.Text = q; Log.Write("Поиск «" + q + "»: " + sw.ElapsedMilliseconds + " мс");
                window.UpdateLayout();
                Pump();
                window.ContentScroll.ScrollToTop();
                Pump();
                Save(window, Path.Combine(directory, file));
            }

            window.Close();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(60));
    }

    /// <summary>Снимок отдельного элемента — например содержимого всплывающего списка.</summary>
    private static System.Windows.Controls.Primitives.Popup? FindLogicalPopup(DependencyObject? host) =>
        host is null ? null : LogicalTreeHelper.GetChildren(host).OfType<System.Windows.Controls.Primitives.Popup>().FirstOrDefault();

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
    /// <summary>
    /// Масштаб экрана для снимка. Единица — экран со 100%, полтора — со 150%.
    /// Проверять надо в том же масштабе, в котором смотрит пользователь:
    /// текст на 100% и на 150% рисуется по-разному, и правка под один масштаб
    /// легко ломает другой.
    /// </summary>
    public static double PreviewScale { get; set; } = 1.0;

    private static void Save(Window window, string path)
    {
        var scale = PreviewScale;
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
