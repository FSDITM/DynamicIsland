using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DynamicIsland.Features;

/// <summary>Синхронные тексты песен из открытой базы LRCLIB. Запрос — только для играющего трека.</summary>
internal sealed partial class LyricsService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly Lock _gate = new();
    private string _key = "";
    private (TimeSpan At, string Text)[] _lines = [];

    public event Action? Changed;

    public LyricsService()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DynamicIsland/2.0 (+https://github.com/FSDITM/DynamicIsland)");
    }

    public bool Available
    {
        get { lock (_gate) return _lines.Length > 0; }
    }

    /// <summary>Сообщить, какой трек играет. При смене трека текст подгружается заново.</summary>
    public void Track(string artist, string title, TimeSpan duration)
    {
        var key = $"{artist}|{title}";
        lock (_gate)
        {
            if (key == _key) return;

            // Сеть подвела — не долбим сервер на каждое событие плеера, пробуем снова через полминуты.
            if (key == _failedKey && DateTime.UtcNow < _retryAfter) return;

            _key = key;
            _lines = [];
        }

        if (title.Length == 0) return;

        // Каждый трек грузится сам по себе. Раньше второй запрос не запускался, пока шёл первый,
        // и при быстром переключении треков текст для нового так и не приходил.
        _ = Task.Run(async () =>
        {
            try
            {
                var lines = await FetchAsync(artist, title, duration);
                lock (_gate)
                    if (_key == key) _lines = lines;
                Changed?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Write("Текст песни не получен: " + ex.Message);
                lock (_gate)
                {
                    if (_key == key) _key = "";
                    _failedKey = key;
                    _retryAfter = DateTime.UtcNow.AddSeconds(30);
                }
            }
        });
    }

    private string _failedKey = "";
    private DateTime _retryAfter;

    private async Task<(TimeSpan, string)[]> FetchAsync(string artist, string title, TimeSpan duration)
    {
        var url = $"https://lrclib.net/api/get?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}";
        if (duration > TimeSpan.Zero) url += $"&duration={(int)duration.TotalSeconds}";

        using var response = await _http.GetAsync(url);
        if (!response.IsSuccessStatusCode) return [];

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("syncedLyrics", out var synced) || synced.ValueKind != JsonValueKind.String) return [];
        return ParseLrc(synced.GetString() ?? "");
    }

    public static (TimeSpan, string)[] ParseLrc(string lrc)
    {
        var list = new List<(TimeSpan, string)>();
        foreach (var raw in lrc.Replace("\r", "").Split('\n'))
        {
            var m = LrcLine().Match(raw);
            if (!m.Success) continue;
            var minutes = int.Parse(m.Groups[1].Value);
            var seconds = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var text = m.Groups[3].Value.Trim();
            list.Add((TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds), text));
        }
        return list.OrderBy(l => l.Item1).ToArray();
    }

    [GeneratedRegex(@"^\[(\d+):(\d+(?:\.\d+)?)\](.*)$")]
    private static partial Regex LrcLine();

    /// <summary>Строка, которую поют в данный момент; null — слов нет или пауза между строками.</summary>
    public string? LineAt(TimeSpan position)
    {
        (TimeSpan At, string Text)[] lines;
        lock (_gate) lines = _lines;
        if (lines.Length == 0) return null;

        var pos = position + TimeSpan.FromMilliseconds(250);
        var idx = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].At <= pos) idx = i;
            else break;
        }
        if (idx < 0) return null;
        var text = lines[idx].Text;
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// Текущая строка и сколько секунд она уже звучит.
    /// </summary>
    public (string Text, float Seconds, float Length)? Current(TimeSpan position)
    {
        (TimeSpan At, string Text)[] lines;
        lock (_gate) lines = _lines;
        if (lines.Length == 0) return null;

        var pos = position + TimeSpan.FromMilliseconds(250);
        var idx = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].At <= pos) idx = i;
            else break;
        }
        if (idx < 0 || lines[idx].Text.Length == 0) return null;

        var start = lines[idx].At;
        var end = idx + 1 < lines.Length ? lines[idx + 1].At : start + TimeSpan.FromSeconds(5);
        // Сколько секунд строка уже звучит: по этому времени, а не по доле строки, едет бегущий текст,
        // иначе короткая строка прокручивалась бы слишком быстро.
        var length = Math.Clamp((float)(end - start).TotalSeconds, 0.8f, 20f);
        return (lines[idx].Text, (float)Math.Max(0, (pos - start).TotalSeconds), length);
    }

    public void Dispose() => _http.Dispose();
}
