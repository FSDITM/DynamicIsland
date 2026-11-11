using System.IO;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DynamicIsland.Configuration;

internal enum ScreenAnchor { Top, Bottom }

/// <summary>Чем подписаны вкладки.</summary>
internal enum TabStyle { Icons, Emoji }

/// <summary>Тема самого островка.</summary>
internal enum IslandTheme
{
    Dark,
    Light,

    /// <summary>Как в Windows.</summary>
    System,
}

/// <summary>Во что превращается островок, когда мешает чужому окну.</summary>
internal enum CollapsedLook
{
    /// <summary>Тонкая полоска у края экрана. Занимает несколько пикселей.</summary>
    Notch,

    /// <summary>Полностью исчезает. Ни одного пикселя, перекрывающего чужое окно.</summary>
    Hidden,
}

/// <summary>Когда островок сворачивается в полоску под чужим окном.</summary>
internal enum CollapseMode
{
    /// <summary>Никогда — островок всегда в полной форме.</summary>
    Never,

    /// <summary>Только для приложений из списка. У них сверху свои вкладки и панели.</summary>
    ListedApps,

    /// <summary>Для любого окна, перекрывающего зону островка.</summary>
    AllWindows,
}

/// <summary>
/// Все настройки островка в одном месте.
///
/// Поля намеренно простые (числа и флаги): их читает поток отрисовки без
/// блокировок, а меняет поток окна настроек. Для независимых значений
/// выровненного размера это безопасно — рассинхрон невозможен, худшее, что
/// может случиться, это применение соседних правок в разных кадрах.
/// </summary>
internal sealed class Settings : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ---------- Внешний вид ----------

    private float _notchWidth = 190f;
    private float _notchHeight = 7f;
    private float _restWidth = 250f;
    private float _restHeight = 38f;
    private float _expandedWidth = 450f;
    private float _expandedHeight = 132f;
    private float _cornerRadius = 30f;
    private float _topOffset = 8f;
    private float _opacity = 1f;
    private float _shadowStrength = 1f;
    private uint _backgroundColor = 0xFF0A0A0C;
    private uint _accentColor = 0xFFF0EDE6;

    public float NotchWidth { get => _notchWidth; set => Set(ref _notchWidth, Math.Clamp(value, 60f, 600f)); }
    public float NotchHeight { get => _notchHeight; set => Set(ref _notchHeight, Math.Clamp(value, 2f, 40f)); }
    public float RestWidth { get => _restWidth; set => Set(ref _restWidth, Math.Clamp(value, 120f, 700f)); }
    public float RestHeight { get => _restHeight; set => Set(ref _restHeight, Math.Clamp(value, 16f, 120f)); }
    public float ExpandedWidth { get => _expandedWidth; set => Set(ref _expandedWidth, Math.Clamp(value, 260f, 900f)); }
    public float ExpandedHeight { get => _expandedHeight; set => Set(ref _expandedHeight, Math.Clamp(value, 80f, 400f)); }
    public float CornerRadius { get => _cornerRadius; set => Set(ref _cornerRadius, Math.Clamp(value, 0f, 60f)); }
    public float TopOffset { get => _topOffset; set => Set(ref _topOffset, Math.Clamp(value, 0f, 80f)); }
    public float Opacity { get => _opacity; set => Set(ref _opacity, Math.Clamp(value, 0.2f, 1f)); }
    public float ShadowStrength { get => _shadowStrength; set => Set(ref _shadowStrength, Math.Clamp(value, 0f, 2f)); }
    public uint BackgroundColor { get => _backgroundColor; set => Set(ref _backgroundColor, value); }
    public uint AccentColor { get => _accentColor; set => Set(ref _accentColor, value); }

    private uint _equalizerColor;
    private uint _iconColor;
    private TabStyle _tabStyle = TabStyle.Icons;
    private string _tabEmoji = "🏠 📋 🗂️ 🔤 📝 🎛️ 🌦️ 🛠️";

    /// <summary>Цвет эквалайзера. 0 — как акцент.</summary>
    public uint EqualizerColor { get => _equalizerColor; set => Set(ref _equalizerColor, value); }

    /// <summary>Цвет значков вкладок. 0 — стандартный.</summary>
    public uint IconColor { get => _iconColor; set => Set(ref _iconColor, value); }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TabStyle TabStyle { get => _tabStyle; set => Set(ref _tabStyle, value); }

    /// <summary>Эмодзи вкладок через пробел: Главная, Буфер, Полка, Текст, Заметки, Управление, Погода, Инструменты.</summary>
    public string TabEmoji { get => _tabEmoji; set => Set(ref _tabEmoji, string.IsNullOrWhiteSpace(value) ? "🏠 📋 🗂️ 🔤 📝 🎛️ 🌦️ 🛠️" : value); }

    private bool _accentFromArt = true;

    /// <summary>
    /// Акцент берётся из обложки играющего трека, а без неё остаётся тёплым белым.
    /// Выключите, чтобы всегда использовать свой цвет из поля выше.
    /// </summary>
    public bool AccentFromArt { get => _accentFromArt; set => Set(ref _accentFromArt, value); }

    // ---------- Поведение ----------

    private int _hoverDelayMs = 220;
    private int _hoverOverWindowDelayMs = 400;
    private CollapseMode _collapseMode = CollapseMode.ListedApps;
    private CollapsedLook _collapsedLook = CollapsedLook.Notch;
    private bool _hideInFullscreen = true;

    /// <summary>
    /// Имена процессов без .exe. По умолчанию — браузеры: у них наверху вкладки
    /// и адресная строка, и островок закрывает именно их.
    /// </summary>
    private string[] _collapseApps =
    [
        "chrome", "msedge", "firefox", "opera", "opera_gx", "brave",
        "vivaldi", "browser", "yandex", "tor", "arc", "zen",
    ];
    private bool _peekOnTrackChange = true;
    private float _peekSeconds = 3.5f;
    private float _animationSpeed = 1f;
    private bool _animateEqualizer = true;

    public int HoverDelayMs { get => _hoverDelayMs; set => Set(ref _hoverDelayMs, Math.Clamp(value, 0, 2000)); }
    public int HoverOverWindowDelayMs { get => _hoverOverWindowDelayMs; set => Set(ref _hoverOverWindowDelayMs, Math.Clamp(value, 0, 3000)); }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CollapseMode CollapseMode { get => _collapseMode; set => Set(ref _collapseMode, value); }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CollapsedLook CollapsedLook { get => _collapsedLook; set => Set(ref _collapsedLook, value); }

    public string[] CollapseApps
    {
        get => _collapseApps;
        set => Set(ref _collapseApps, Normalize(value));
    }

    public bool HideInFullscreen { get => _hideInFullscreen; set => Set(ref _hideInFullscreen, value); }

    /// <summary>Сворачиваться ли под окном приложения с таким именем процесса.</summary>
    public bool ShouldCollapseFor(string processName) => CollapseMode switch
    {
        CollapseMode.Never => false,
        CollapseMode.AllWindows => true,
        _ => processName.Length > 0 &&
             _collapseApps.Contains(processName, StringComparer.OrdinalIgnoreCase),
    };

    /// <summary>Приводит список к виду «без .exe, без пустых, без повторов».</summary>
    private static string[] Normalize(string[]? apps) => apps is null
        ? []
        : apps.Where(a => a is not null).Select(a => a.Trim())
              .Where(a => a.Length > 0)
              .Select(a => a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? a[..^4] : a)
              .Distinct(StringComparer.OrdinalIgnoreCase)
              .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
              .ToArray();
    public bool PeekOnTrackChange { get => _peekOnTrackChange; set => Set(ref _peekOnTrackChange, value); }
    public float PeekSeconds { get => _peekSeconds; set => Set(ref _peekSeconds, Math.Clamp(value, 0.5f, 15f)); }

    /// <summary>Множитель частоты пружин: больше — резче и быстрее.</summary>
    public float AnimationSpeed { get => _animationSpeed; set => Set(ref _animationSpeed, Math.Clamp(value, 0.3f, 3f)); }

    public bool AnimateEqualizer { get => _animateEqualizer; set => Set(ref _animateEqualizer, value); }

    // ---------- Расположение ----------

    private int _monitorIndex;
    private float _horizontalOffset;
    private ScreenAnchor _anchor = ScreenAnchor.Top;

    public int MonitorIndex { get => _monitorIndex; set => Set(ref _monitorIndex, Math.Max(0, value)); }
    public float HorizontalOffset { get => _horizontalOffset; set => Set(ref _horizontalOffset, Math.Clamp(value, -2000f, 2000f)); }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ScreenAnchor Anchor { get => _anchor; set => Set(ref _anchor, value); }

    // ---------- Содержимое ----------

    private bool _showClock = true;
    private bool _showDate = true;
    private bool _showBattery = true;
    private bool _showEqualizer = true;
    private bool _use24Hour = true;
    private string _fontFamily = "";
    private bool _showMetrics;
    private bool _runOnStartup;
    private string _hotkey = "Ctrl+Alt+D";

    public bool ShowClock { get => _showClock; set => Set(ref _showClock, value); }
    public bool ShowDate { get => _showDate; set => Set(ref _showDate, value); }
    public bool ShowBattery { get => _showBattery; set => Set(ref _showBattery, value); }
    public bool ShowEqualizer { get => _showEqualizer; set => Set(ref _showEqualizer, value); }
    public bool Use24HourClock { get => _use24Hour; set => Set(ref _use24Hour, value); }

    /// <summary>
    /// Имя семейства шрифта из системы. Пусто — вшитый Rubik.
    /// Существует, чтобы можно было поставить свой шрифт: например SF Pro,
    /// если он у вас установлен. Вшить его в приложение нельзя — лицензия
    /// Apple разрешает использовать SF Pro только при разработке под Apple.
    /// </summary>
    public string FontFamily { get => _fontFamily; set => Set(ref _fontFamily, value ?? ""); }
    public bool ShowMetrics { get => _showMetrics; set => Set(ref _showMetrics, value); }
    public bool RunOnStartup { get => _runOnStartup; set => Set(ref _runOnStartup, value); }

    /// <summary>Сочетание вида «Ctrl+Alt+D». Пустая строка — клавиша отключена.</summary>
    public string Hotkey { get => _hotkey; set => Set(ref _hotkey, value ?? ""); }

    // ---------- 2.0: панель и вкладки ----------

    private float _panelWidth = 520f;
    private float _panelHeight = 310f;
    private float _islandScale = 1f;
    private IslandTheme _theme = IslandTheme.Dark;

    /// <summary>Размер раскрытой панели с вкладками (кроме «Главной»).</summary>
    public float PanelWidth { get => _panelWidth; set => Set(ref _panelWidth, Math.Clamp(value, 380f, 900f)); }
    public float PanelHeight { get => _panelHeight; set => Set(ref _panelHeight, Math.Clamp(value, 220f, 600f)); }

    /// <summary>Общий масштаб островка: 0.8–2.0.</summary>
    public float IslandScale { get => _islandScale; set => Set(ref _islandScale, Math.Clamp(value, 0.8f, 2f)); }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public IslandTheme Theme { get => _theme; set => Set(ref _theme, value); }

    // ---------- 2.0: горячие клавиши ----------

    private string _hkCommandBar = "Alt+Space";
    private string _hkScreenshot = "Ctrl+Alt+A";
    private string _hkOcr = "Ctrl+Alt+T";
    private string _hkColor = "Ctrl+Alt+C";
    private string _hkQr = "Ctrl+Alt+Q";
    private string _hkRecord = "Ctrl+Alt+R";
    private string _hkPastePlain = "Ctrl+Alt+V";

    public string HotkeyCommandBar { get => _hkCommandBar; set => Set(ref _hkCommandBar, value ?? ""); }
    public string HotkeyScreenshot { get => _hkScreenshot; set => Set(ref _hkScreenshot, value ?? ""); }
    public string HotkeyOcr { get => _hkOcr; set => Set(ref _hkOcr, value ?? ""); }
    public string HotkeyColor { get => _hkColor; set => Set(ref _hkColor, value ?? ""); }
    public string HotkeyQr { get => _hkQr; set => Set(ref _hkQr, value ?? ""); }
    public string HotkeyRecord { get => _hkRecord; set => Set(ref _hkRecord, value ?? ""); }
    public string HotkeyPastePlain { get => _hkPastePlain; set => Set(ref _hkPastePlain, value ?? ""); }

    // ---------- 2.0: буфер, снимки, запись ----------

    private bool _clipboardHistory = true;
    private int _clipboardLimit = 60;
    private string _screenshotFolder = "";
    private string _recordFolder = "";
    private int _recordFps = 30;
    private bool _recordHighQuality;
    private bool _recordGif;
    private bool _islandInCapture;
    private bool _autoPasteAfterShot;

    public bool ClipboardHistory { get => _clipboardHistory; set => Set(ref _clipboardHistory, value); }
    public int ClipboardLimit { get => _clipboardLimit; set => Set(ref _clipboardLimit, Math.Clamp(value, 10, 200)); }

    /// <summary>Пусто — «Изображения\DynamicIsland».</summary>
    public string ScreenshotFolder { get => _screenshotFolder; set => Set(ref _screenshotFolder, value ?? ""); }

    /// <summary>Пусто — «Видео\DynamicIsland».</summary>
    public string RecordFolder { get => _recordFolder; set => Set(ref _recordFolder, value ?? ""); }
    public int RecordFps { get => _recordFps; set => Set(ref _recordFps, value >= 45 ? 60 : 30); }
    public bool RecordHighQuality { get => _recordHighQuality; set => Set(ref _recordHighQuality, value); }
    public bool RecordGif { get => _recordGif; set => Set(ref _recordGif, value); }

    /// <summary>Показывать островок на снимках и в записи экрана. По умолчанию его там нет.</summary>
    public bool IslandInCapture { get => _islandInCapture; set => Set(ref _islandInCapture, value); }
    public bool AutoPasteAfterShot { get => _autoPasteAfterShot; set => Set(ref _autoPasteAfterShot, value); }

    // ---------- 2.0: события в пилюле ----------

    private bool _evMicCam = true;
    private bool _evDownloads = true;
    private bool _evLayout = true;
    private bool _evCaps = true;
    private bool _evVpn = true;
    private bool _evSteam = true;
    private bool _evRecording = true;
    private bool _wheelVolume = true;
    private bool _showLyrics = true;

    public bool EventMicCam { get => _evMicCam; set => Set(ref _evMicCam, value); }
    public bool EventDownloads { get => _evDownloads; set => Set(ref _evDownloads, value); }
    public bool EventLayout { get => _evLayout; set => Set(ref _evLayout, value); }
    public bool EventCaps { get => _evCaps; set => Set(ref _evCaps, value); }
    public bool EventVpn { get => _evVpn; set => Set(ref _evVpn, value); }
    public bool EventSteam { get => _evSteam; set => Set(ref _evSteam, value); }
    public bool EventRecording { get => _evRecording; set => Set(ref _evRecording, value); }
    public bool WheelVolume { get => _wheelVolume; set => Set(ref _wheelVolume, value); }
    public bool ShowLyrics { get => _showLyrics; set => Set(ref _showLyrics, value); }

    // ---------- 2.0: погода ----------

    private string _weatherCity = "";
    private string _weatherCityResolved = "";
    private double _weatherLat;
    private double _weatherLon;
    private bool _weatherInPill = true;
    private bool _rainWarning = true;
    private bool _fahrenheit;

    /// <summary>Город, как его ввёл человек. Определяется только по названию, не по IP.</summary>
    public string WeatherCity { get => _weatherCity; set => Set(ref _weatherCity, value ?? ""); }
    public string WeatherCityResolved { get => _weatherCityResolved; set => Set(ref _weatherCityResolved, value ?? ""); }
    public double WeatherLat { get => _weatherLat; set => Set(ref _weatherLat, value); }
    public double WeatherLon { get => _weatherLon; set => Set(ref _weatherLon, value); }
    public bool WeatherInPill { get => _weatherInPill; set => Set(ref _weatherInPill, value); }
    public bool RainWarning { get => _rainWarning; set => Set(ref _rainWarning, value); }
    public bool Fahrenheit { get => _fahrenheit; set => Set(ref _fahrenheit, value); }

    // ---------- 2.0: звук ----------

    private string _volumeDevice = "";
    private string _micDevice = "";

    /// <summary>Устройство вывода, чью громкость меняет островок. Пусто — выбранное в Windows.</summary>
    public string VolumeDevice { get => _volumeDevice; set => Set(ref _volumeDevice, value ?? ""); }

    /// <summary>Микрофон, который островок включает и выключает. Пусто — выбранный в Windows.</summary>
    public string MicDevice { get => _micDevice; set => Set(ref _micDevice, value ?? ""); }

    // ---------- Уведомления ----------

    private bool _showNotifications = true;
    private string[] _notificationBlocklist = [];

    /// <summary>Показывать уведомления Windows карточкой на островке.</summary>
    public bool ShowNotifications { get => _showNotifications; set => Set(ref _showNotifications, value); }

    /// <summary>Приложения, чьи уведомления не показывать (часть названия).</summary>
    public string[] NotificationBlocklist { get => _notificationBlocklist; set => Set(ref _notificationBlocklist, value ?? []); }

    // ---------- Баннеры Windows ----------

    private bool _islandOnlyNotifications = true;
    private string[] _islandOnlyApps = ["Telegram", "Discord", "WhatsApp", "Viber", "Signal"];

    /// <summary>Убирать системные баннеры у перечисленных приложений: сообщения видны только на острове.</summary>
    public bool IslandOnlyNotifications { get => _islandOnlyNotifications; set => Set(ref _islandOnlyNotifications, value); }

    /// <summary>Части названий приложений (по идентификатору в Windows), которых это касается.</summary>
    public string[] IslandOnlyApps { get => _islandOnlyApps; set => Set(ref _islandOnlyApps, value ?? []); }

    /// <summary>Прежнее значение «показывать баннер» для каждого приложения: -1 — значения не было.</summary>
    public Dictionary<string, int> BannerBackup { get => _bannerBackup; set => _bannerBackup = value ?? []; }
    private Dictionary<string, int> _bannerBackup = [];

        // ---------- Telegram ----------

    private bool _telegramReply;

    /// <summary>Кнопка «Ответить» на карточках Telegram. Выключена: Telegram Desktop не даёт открыть чат по номеру, надёжного способа пока нет.</summary>
    public bool TelegramReply { get => _telegramReply; set => Set(ref _telegramReply, value); }

    // ---------- 2.0: командная строка ----------

    private string _searchUrl = "https://yandex.ru/search/?text={0}";
    public string SearchUrl { get => _searchUrl; set => Set(ref _searchUrl, string.IsNullOrWhiteSpace(value) ? "https://yandex.ru/search/?text={0}" : value); }

    // ---------- Хранение ----------

    [JsonIgnore]
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DynamicIsland", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                try
                {
                    var loaded = JsonSerializer.Deserialize<Settings>(json, JsonOptions);
                    if (loaded is not null) return loaded;
                }
                catch (JsonException ex)
                {
                    // Одно испорченное поле (скажем, значение из другой версии) не повод терять
                    // все настройки: выбрасываем только его. Исходный файл сохраняем рядом.
                    Log.Write("Настройки: " + ex.Message);
                    File.Copy(FilePath, FilePath + ".bad", overwrite: true);
                    if (Salvage(json) is { } salvaged)
                    {
                        salvaged.Save();
                        return salvaged;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Повреждённый файл не должен мешать запуску: берём значения по умолчанию.
            Log.Write("Настройки не прочитаны, беру значения по умолчанию: " + ex.Message);
        }

        // Файл создаём сразу: иначе до первой правки его нет, и посмотреть
        // или поправить настройки руками негде.
        var defaults = new Settings();
        defaults.Save();
        return defaults;
    }

    /// <summary>Убирает из JSON поля, которые не разбираются, пока остальное не прочитается.</summary>
    private static Settings? Salvage(string json)
    {
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(json) is not System.Text.Json.Nodes.JsonObject obj) return null;
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    return obj.Deserialize<Settings>(JsonOptions);
                }
                catch (JsonException ex) when (ex.Path is { Length: > 2 } path)
                {
                    var name = path[2..].Split('.', '[')[0];
                    if (!obj.Remove(name)) return null;
                    Log.Write("Настройки: поле «" + name + "» сброшено к значению по умолчанию");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("Настройки не восстановлены: " + ex.Message);
        }
        return null;
    }

    private static readonly object SaveGate = new();

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

            // Сохраняют из разных потоков (отрисовка, таймер, окно настроек): по одному,
            // иначе две записи дрались за один временный файл и одна молча терялась.
            lock (SaveGate)
            {
                // Пишем во временный файл и подменяем: обрыв записи не оставит
                // пользователя с нечитаемым файлом настроек.
                var temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
                File.Move(temp, FilePath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Log.Write("Настройки не сохранены: " + ex.Message);
        }
    }

    /// <summary>Возвращает всё к значениям по умолчанию.</summary>
    public void ResetToDefaults()
    {
        var d = new Settings();
        foreach (var p in typeof(Settings).GetProperties())
        {
            if (!p.CanWrite || !p.CanRead) continue;

            // Это не настройка, а память о чужих значениях реестра: без неё баннеры мессенджеров
            // после сброса уже не вернуть.
            if (p.Name == nameof(BannerBackup)) continue;
            p.SetValue(this, p.GetValue(d));
        }
    }
}
