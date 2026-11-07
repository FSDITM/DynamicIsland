using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using DynamicIsland.Configuration;
using DynamicIsland.Ui;
using SkiaSharp;

namespace DynamicIsland.Features;

internal sealed record HourPoint(DateTime Time, double Temp, int Code, int RainChance);

internal sealed record DayPoint(DateTime Date, double Min, double Max, int Code);

internal sealed record WeatherNow(double Temp, double Feels, int Code, double Wind, bool IsDay);

/// <summary>Погода от Open-Meteo: без ключа и регистрации. Город задаётся названием, по IP не определяется.</summary>
internal sealed class WeatherService : IDisposable
{
    private readonly Settings _settings;
    private readonly PillHub _hub;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly Timer _timer;
    private int _busy;
    private string _lastRainWarning = "";

    public WeatherNow? Now { get; private set; }
    public List<HourPoint> Hours { get; private set; } = [];
    public List<DayPoint> Days { get; private set; } = [];
    public string Error { get; private set; } = "";
    public bool Loading { get; private set; }

    public event Action? Changed;

    public WeatherService(Settings settings, PillHub hub)
    {
        _settings = settings;
        _hub = hub;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DynamicIsland/2.0 (+https://github.com/FSDITM/DynamicIsland)");
        _timer = new Timer(_ => Refresh(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public bool HasCity => _settings.WeatherCityResolved.Length > 0;

    public void Start() => _timer.Change(2000, (int)TimeSpan.FromMinutes(30).TotalMilliseconds);

    /// <summary>Задать город по названию: ищет координаты и тут же обновляет прогноз.</summary>
    public async Task<string?> SetCityAsync(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return null;
        try
        {
            var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(name)}&count=1&language=ru&format=json";
            using var response = await _http.GetAsync(url);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return null;

            var first = results[0];
            var resolved = first.GetProperty("name").GetString() ?? name;
            var country = first.TryGetProperty("country", out var c) ? c.GetString() : "";
            _settings.WeatherCity = name;
            _settings.WeatherLat = first.GetProperty("latitude").GetDouble();
            _settings.WeatherLon = first.GetProperty("longitude").GetDouble();
            _settings.WeatherCityResolved = string.IsNullOrEmpty(country) ? resolved : $"{resolved}, {country}";
            Refresh();
            return _settings.WeatherCityResolved;
        }
        catch (Exception ex)
        {
            Log.Write("Город не найден: " + ex.Message);
            Error = "Нет сети";
            Changed?.Invoke();
            return null;
        }
    }

    public void Refresh()
    {
        if (!HasCity) { Changed?.Invoke(); return; }
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;

        _ = Task.Run(async () =>
        {
            try
            {
                Loading = true;
                var fahrenheit = _settings.Fahrenheit ? "&temperature_unit=fahrenheit" : "";
                var lat = _settings.WeatherLat.ToString(CultureInfo.InvariantCulture);
                var lon = _settings.WeatherLon.ToString(CultureInfo.InvariantCulture);
                var url = $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}" +
                    "&current=temperature_2m,apparent_temperature,weather_code,wind_speed_10m,is_day" +
                    "&hourly=temperature_2m,weather_code,precipitation_probability" +
                    "&daily=weather_code,temperature_2m_max,temperature_2m_min" +
                    $"&timezone=auto&forecast_days=7&wind_speed_unit=ms{fahrenheit}";

                using var response = await _http.GetAsync(url);
                response.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Parse(doc.RootElement);
                Error = "";
            }
            catch (Exception ex)
            {
                Log.Write("Погода не обновилась: " + ex.Message);
                Error = Now is null ? "Нет сети" : "";
            }
            finally
            {
                Loading = false;
                Interlocked.Exchange(ref _busy, 0);
                UpdateChipAndWarning();
                Changed?.Invoke();
            }
        });
    }

    private void Parse(JsonElement root)
    {
        var cur = root.GetProperty("current");
        Now = new WeatherNow(
            cur.GetProperty("temperature_2m").GetDouble(),
            cur.GetProperty("apparent_temperature").GetDouble(),
            cur.GetProperty("weather_code").GetInt32(),
            cur.GetProperty("wind_speed_10m").GetDouble(),
            cur.GetProperty("is_day").GetInt32() == 1);

        var h = root.GetProperty("hourly");
        var times = h.GetProperty("time").EnumerateArray().Select(t => DateTime.Parse(t.GetString()!, CultureInfo.InvariantCulture)).ToList();
        var temps = h.GetProperty("temperature_2m").EnumerateArray().Select(t => t.ValueKind == JsonValueKind.Null ? 0 : t.GetDouble()).ToList();
        var codes = h.GetProperty("weather_code").EnumerateArray().Select(t => t.ValueKind == JsonValueKind.Null ? 0 : t.GetInt32()).ToList();
        var rain = h.GetProperty("precipitation_probability").EnumerateArray().Select(t => t.ValueKind == JsonValueKind.Null ? 0 : t.GetInt32()).ToList();

        var hours = new List<HourPoint>();
        var from = DateTime.Now.AddMinutes(-30);
        for (var i = 0; i < times.Count; i++)
            if (times[i] >= from && hours.Count < 24)
                hours.Add(new HourPoint(times[i], temps[i], codes[i], rain[i]));
        Hours = hours;

        var d = root.GetProperty("daily");
        var dates = d.GetProperty("time").EnumerateArray().Select(t => DateTime.Parse(t.GetString()!, CultureInfo.InvariantCulture)).ToList();
        var max = d.GetProperty("temperature_2m_max").EnumerateArray().Select(t => t.GetDouble()).ToList();
        var min = d.GetProperty("temperature_2m_min").EnumerateArray().Select(t => t.GetDouble()).ToList();
        var dcodes = d.GetProperty("weather_code").EnumerateArray().Select(t => t.GetInt32()).ToList();
        Days = dates.Select((date, i) => new DayPoint(date, min[i], max[i], dcodes[i])).ToList();
    }

    public static bool IsPrecipitation(int code) => code is >= 51 and <= 67 or >= 71 and <= 77 or >= 80 and <= 86 or >= 95;

    public string Temp(double t)
    {
        var r = Math.Round(t);
        return r == 0 ? "0°" : $"{(r > 0 ? "+" : "−")}{Math.Abs(r)}°";
    }

    private void UpdateChipAndWarning()
    {
        if (Now is null || !_settings.WeatherInPill)
        {
            _hub.SetChip("weather", null);
            return;
        }

        _hub.SetChip("weather", new Chip("weather", "", Temp(Now.Temp), null, Priority: 1));

        if (!_settings.RainWarning || Now is null || IsPrecipitation(Now.Code)) return;

        // Осадки в ближайшие два часа: предупреждаем один раз на событие.
        var soon = Hours.FirstOrDefault(p => p.Time > DateTime.Now && p.Time <= DateTime.Now.AddHours(2) && IsPrecipitation(p.Code) && p.RainChance >= 50);
        if (soon is null) return;

        var key = soon.Time.ToString("yyyyMMddHH");
        if (key == _lastRainWarning) return;
        _lastRainWarning = key;

        var snow = soon.Code is >= 71 and <= 77 or 85 or 86;
        _hub.Post(new Card
        {
            Glyph = G.Cloud,
            Title = snow ? "Скоро пойдёт снег" : "Скоро пойдёт дождь",
            Subtitle = $"Примерно в {soon.Time:HH:mm} · вероятность {soon.RainChance}%",
            Tint = new SKColor(100, 180, 255),
            Seconds = 6f,
        });
    }

    public static string Describe(int code) => code switch
    {
        0 => "Ясно",
        1 => "Преимущественно ясно",
        2 => "Переменная облачность",
        3 => "Пасмурно",
        45 or 48 => "Туман",
        51 or 53 or 55 or 56 or 57 => "Морось",
        61 or 63 => "Дождь",
        65 or 66 or 67 => "Сильный дождь",
        71 or 73 => "Снег",
        75 or 77 => "Сильный снег",
        80 or 81 or 82 => "Ливень",
        85 or 86 => "Снегопад",
        95 => "Гроза",
        96 or 99 => "Гроза с градом",
        _ => "—",
    };

    public void Dispose()
    {
        _timer.Dispose();
        _http.Dispose();
    }
}
