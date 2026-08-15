using System.IO;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DynamicIsland.Configuration;

internal enum ScreenAnchor { Top, Bottom }

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
    private uint _accentColor = 0xFF48BEFF;

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

    // ---------- Поведение ----------

    private int _hoverDelayMs = 220;
    private int _hoverOverWindowDelayMs = 400;
    private CollapseMode _collapseMode = CollapseMode.ListedApps;
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
        : apps.Select(a => a.Trim())
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
    /// Имя семейства шрифта из системы. Пусто — вшитый Inter.
    /// Существует, чтобы можно было поставить свой шрифт: например SF Pro,
    /// если он у вас установлен. Вшить его в приложение нельзя — лицензия
    /// Apple разрешает использовать SF Pro только при разработке под Apple.
    /// </summary>
    public string FontFamily { get => _fontFamily; set => Set(ref _fontFamily, value ?? ""); }
    public bool ShowMetrics { get => _showMetrics; set => Set(ref _showMetrics, value); }
    public bool RunOnStartup { get => _runOnStartup; set => Set(ref _runOnStartup, value); }

    /// <summary>Сочетание вида «Ctrl+Alt+D». Пустая строка — клавиша отключена.</summary>
    public string Hotkey { get => _hotkey; set => Set(ref _hotkey, value ?? ""); }

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
                var loaded = JsonSerializer.Deserialize<Settings>(json, JsonOptions);
                if (loaded is not null) return loaded;
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

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

            // Пишем во временный файл и подменяем: обрыв записи не оставит
            // пользователя с нечитаемым файлом настроек.
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(temp, FilePath, overwrite: true);
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
            p.SetValue(this, p.GetValue(d));
        }
    }
}
