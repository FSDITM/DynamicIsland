using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicIsland.Configuration;
using DynamicIsland.Ui;
using SkiaSharp;

namespace DynamicIsland.Features;

internal enum Repeat { None, Daily, Weekly }

internal sealed class Reminder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = "";
    public DateTime At { get; set; }
    public Repeat Repeat { get; set; }

    /// <summary>Дни недели для еженедельного повтора: бит 0 — понедельник … бит 6 — воскресенье.</summary>
    public int DaysMask { get; set; }

    public string When()
    {
        var time = At.ToString("HH:mm");
        return Repeat switch
        {
            Repeat.Daily => $"каждый день в {time}",
            Repeat.Weekly => $"{DaysText(DaysMask)} в {time}",
            _ => At.Date == DateTime.Today ? $"сегодня в {time}"
               : At.Date == DateTime.Today.AddDays(1) ? $"завтра в {time}"
               : At.ToString("d MMM, HH:mm"),
        };
    }

    public static string DaysText(int mask)
    {
        string[] names = ["пн", "вт", "ср", "чт", "пт", "сб", "вс"];
        if (mask == 0b0011111) return "по будням";
        var list = Enumerable.Range(0, 7).Where(i => (mask & (1 << i)) != 0).Select(i => names[i]);
        return "по " + string.Join(", ", list);
    }
}

internal sealed record ParsedReminder(string Text, DateTime At, Repeat Repeat, int DaysMask);

/// <summary>
/// Разбор фраз вроде «позвонить маме завтра в 9», «таблетки каждый день в 23:00»,
/// «через 20 минут выключить плиту». Обычные регулярные выражения.
/// </summary>
internal static partial class ReminderParser
{
    private const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly (string Pattern, DayOfWeek Day)[] Days =
    [
        (@"понедельни\w*|пн|monday|mon", DayOfWeek.Monday),
        (@"вторни\w*|вт|tuesday|tue", DayOfWeek.Tuesday),
        (@"сред[ауы]\w*|ср|wednesday|wed", DayOfWeek.Wednesday),
        (@"четвер\w*|чт|thursday|thu", DayOfWeek.Thursday),
        (@"пятниц\w*|пт|friday|fri", DayOfWeek.Friday),
        (@"суббот\w*|сб|saturday|sat", DayOfWeek.Saturday),
        (@"воскресень\w*|вс|sunday|sun", DayOfWeek.Sunday),
    ];

    private static int MaskOf(DayOfWeek d) => 1 << (((int)d + 6) % 7);

    public static bool LooksLikeReminder(string input) =>
        Regex.IsMatch(input.Trim(), @"^(напомни\w*|напоминание|remind\b)", O);

    public static ParsedReminder? Parse(string input, DateTime now)
    {
        var text = input.Trim();
        text = Regex.Replace(text, @"^(напомни(ть)?(\s+мне)?|напоминание|remind(\s+me)?(\s+to)?)\s*[:,-]?\s*", "", O);
        if (text.Length == 0) return null;

        var rest = text;
        DateTime? date = null;
        var byWeekday = false;
        TimeSpan? time = null;
        TimeSpan? relative = null;
        var repeat = Repeat.None;
        var mask = 0;

        // 1. «через 20 минут», «in 2 hours», «через час», «через полчаса»
        var rel = Regex.Match(rest,
            @"\b(через|in)\s+(?<n>\d+|пол|an?|один|одну|два|две|три|пять|десять)?\s*(?<u>полчаса|секунд\w*|сек\w*|минут\w*|мин\w*|час\w*|ч\b|дн\w*|день|сут\w*|недел\w*|seconds?|secs?|minutes?|mins?|hours?|hrs?|days?|weeks?)",
            O);
        if (rel.Success)
        {
            var unit = rel.Groups["u"].Value.ToLowerInvariant();
            var word = rel.Groups["n"].Value.ToLowerInvariant();
            double n = word switch
            {
                "" or "a" or "an" or "один" or "одну" => 1,
                "пол" => 0.5,
                "два" or "две" => 2,
                "три" => 3,
                "пять" => 5,
                "десять" => 10,
                _ => double.TryParse(word, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 1,
            };
            relative = unit switch
            {
                "полчаса" => TimeSpan.FromMinutes(30),
                var u when u.StartsWith("сек") || u.StartsWith("sec") => TimeSpan.FromSeconds(n),
                var u when u.StartsWith("мин") || u.StartsWith("min") => TimeSpan.FromMinutes(n),
                var u when u.StartsWith("ч") || u.StartsWith("hou") || u.StartsWith("hr") => TimeSpan.FromHours(n),
                var u when u.StartsWith("д") || u.StartsWith("сут") || u.StartsWith("day") => TimeSpan.FromDays(n),
                _ => TimeSpan.FromDays(7 * n),
            };
            rest = rest.Remove(rel.Index, rel.Length);
        }

        // 2. Повторы
        var daily = Regex.Match(rest, @"\b(каждый\s+день|ежедневно|каждые?\s+сутки|every\s*day|daily)\b", O);
        if (daily.Success)
        {
            repeat = Repeat.Daily;
            rest = rest.Remove(daily.Index, daily.Length);
        }
        else
        {
            var weekdays = Regex.Match(rest, @"\b(по\s+будням|every\s+weekday|в\s+будни)\b", O);
            if (weekdays.Success)
            {
                repeat = Repeat.Weekly;
                mask = 0b0011111;
                rest = rest.Remove(weekdays.Index, weekdays.Length);
            }
            else
            {
                foreach (var (pattern, day) in Days)
                {
                    var every = Regex.Match(rest, $@"\b(кажд(ый|ую|ое|ые)|every|по)\s+({pattern})\b", O);
                    if (!every.Success) continue;
                    repeat = Repeat.Weekly;
                    mask |= MaskOf(day);
                    rest = rest.Remove(every.Index, every.Length);
                    // «каждый понедельник и среду» — собираем остальные дни
                }
            }
        }

        // 3. День
        if (relative is null && repeat != Repeat.Daily)
        {
            var today = now.Date;
            var dayWord = Regex.Match(rest, @"\b(послезавтра|day\s+after\s+tomorrow|завтра|tomorrow|сегодня|today)\b", O);
            if (dayWord.Success)
            {
                var w = dayWord.Value.ToLowerInvariant();
                date = w.StartsWith("после") || w.StartsWith("day") ? today.AddDays(2)
                     : w.StartsWith("завтра") || w.StartsWith("tomorrow") ? today.AddDays(1)
                     : today;
                rest = rest.Remove(dayWord.Index, dayWord.Length);
            }
            else if (repeat == Repeat.None)
            {
                foreach (var (pattern, day) in Days)
                {
                    var wd = Regex.Match(rest, $@"\b(в|во|on|next)?\s*({pattern})\b", O);
                    if (!wd.Success || wd.Groups[2].Value.Length < 2) continue;
                    var delta = ((int)day - (int)today.DayOfWeek + 7) % 7;
                    date = today.AddDays(delta);
                    byWeekday = true;
                    rest = rest.Remove(wd.Index, wd.Length);
                    break;
                }
            }
        }

        // 4. Время: «в 9», «в 19:30», «at 7pm», «в 2 часа дня»
        var tm = Regex.Match(rest,
            @"\b(в|к|at|@)\s*(?<h>\d{1,2})(?:[:.](?<m>\d{2}))?\s*(?<p>утра|вечера|дня|ночи|am|pm|час\w*)?", O);
        if (tm.Success && int.TryParse(tm.Groups["h"].Value, out var hour))
        {
            var minute = tm.Groups["m"].Success ? int.Parse(tm.Groups["m"].Value) : 0;
            var period = tm.Groups["p"].Value.ToLowerInvariant();
            if ((period is "вечера" or "pm" or "дня") && hour < 12) hour += 12;
            if (period == "ночи" && hour == 12) hour = 0;
            if (hour is >= 0 and <= 23 && minute is >= 0 and <= 59)
            {
                time = new TimeSpan(hour, minute, 0);
                rest = rest.Remove(tm.Index, tm.Length);
            }
        }

        DateTime at;
        if (relative is { } r)
        {
            at = now + r;
        }
        else if (repeat == Repeat.Daily)
        {
            var t = time ?? new TimeSpan(9, 0, 0);
            at = now.Date + t;
            if (at <= now) at = at.AddDays(1);
        }
        else if (repeat == Repeat.Weekly)
        {
            var t = time ?? new TimeSpan(9, 0, 0);
            at = NextWeekly(now, mask, t);
        }
        else if (date is { } d)
        {
            at = d + (time ?? new TimeSpan(9, 0, 0));
            // Время сегодня уже прошло: «в понедельник в 9», сказанное в понедельник в 10, — это
            // следующий понедельник; «сегодня в 9» в 10 — завтра. Раньше карточка «пропущено»
            // показывалась сразу же.
            if (at <= now && date == now.Date) at = at.AddDays(byWeekday ? 7 : 1);
        }
        else if (time is { } tOnly)
        {
            at = now.Date + tOnly;
            if (at <= now) at = at.AddDays(1);
        }
        else
        {
            return null;
        }

        rest = Regex.Replace(rest, @"\s{2,}", " ").Trim(' ', ',', '.', '-', ':', ';');
        rest = Regex.Replace(rest, @"^(что|чтобы|to|that)\s+", "", O);
        if (rest.Length == 0) rest = "Напоминание";
        else rest = char.ToUpper(rest[0], CultureInfo.CurrentCulture) + rest[1..];

        return new ParsedReminder(rest, at, repeat, mask);
    }

    public static DateTime NextWeekly(DateTime from, int mask, TimeSpan time)
    {
        for (var i = 0; i < 8; i++)
        {
            var day = from.Date.AddDays(i);
            if ((mask & MaskOf(day.DayOfWeek)) == 0) continue;
            var candidate = day + time;
            if (candidate > from) return candidate;
        }
        return from.Date.AddDays(1) + time;
    }

    /// <summary>«10», «10 мин», «25m», «1ч 30м», «90 сек», «1:30» (минуты:секунды).</summary>
    public static TimeSpan? ParseDuration(string input)
    {
        var s = input.Trim().ToLowerInvariant();
        if (s.Length == 0) return null;

        var colon = Regex.Match(s, @"^(\d{1,3}):(\d{2})$");
        if (colon.Success)
            return TimeSpan.FromMinutes(int.Parse(colon.Groups[1].Value)) + TimeSpan.FromSeconds(int.Parse(colon.Groups[2].Value));

        var total = TimeSpan.Zero;
        var found = false;
        foreach (Match m in Regex.Matches(s, @"(\d+(?:[.,]\d+)?)\s*(часов|часа|час|ч|h|hours?|hrs?|минуты|минуту|минут|мин|м|m|minutes?|mins?|секунд\w*|сек|с|s|seconds?|secs?)?"))
        {
            if (!double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var n)) continue;
            var u = m.Groups[2].Value;
            found = true;
            if (u.StartsWith("час") || u == "ч" || u.StartsWith('h')) total += TimeSpan.FromHours(n);
            else if (u.StartsWith("сек") || u == "с" || u.StartsWith('s')) total += TimeSpan.FromSeconds(n);
            else total += TimeSpan.FromMinutes(n);
        }
        return found && total > TimeSpan.Zero ? total : null;
    }
}

/// <summary>Напоминания и таймер: хранение, срабатывание, карточки и чип в пилюле.</summary>
internal sealed class ReminderService : IDisposable
{
    private readonly Settings _settings;
    private readonly PillHub _hub;
    private readonly Lock _gate = new();
    private readonly List<Reminder> _items = [];
    private readonly Timer _dueTimer;
    private readonly Timer _tickTimer;

    private DateTime? _timerEnd;
    private TimeSpan _timerTotal;
    private string _timerLabel = "";

    public event Action? Changed;

    public static string StorePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DynamicIsland", "reminders.json");

    public ReminderService(Settings settings, PillHub hub)
    {
        _settings = settings;
        _hub = hub;
        _dueTimer = new Timer(_ => CheckDue(), null, Timeout.Infinite, Timeout.Infinite);
        _tickTimer = new Timer(_ => TickTimer(), null, Timeout.Infinite, Timeout.Infinite);
        Load();
    }

    public void Start()
    {
        CheckDue();
        Reschedule();
    }

    public List<Reminder> Upcoming()
    {
        lock (_gate) return _items.OrderBy(r => r.At).ToList();
    }

    public bool TimerRunning => _timerEnd is not null;
    public TimeSpan TimerLeft => _timerEnd is { } e ? TimeSpan.FromSeconds(Math.Max(0, (e - DateTime.Now).TotalSeconds)) : TimeSpan.Zero;
    public TimeSpan TimerTotal => _timerTotal;
    public string TimerLabel => _timerLabel;

    public void Add(ParsedReminder parsed)
    {
        lock (_gate)
            _items.Add(new Reminder { Text = parsed.Text, At = parsed.At, Repeat = parsed.Repeat, DaysMask = parsed.DaysMask });
        Save();
        Reschedule();
        Changed?.Invoke();
    }

    public void Remove(Guid id)
    {
        lock (_gate) _items.RemoveAll(r => r.Id == id);
        Save();
        Reschedule();
        Changed?.Invoke();
    }

    public void StartTimer(TimeSpan duration, string label = "")
    {
        _timerEnd = DateTime.Now + duration;
        _timerTotal = duration;
        _timerLabel = label;
        _tickTimer.Change(0, 1000);
        Changed?.Invoke();
    }

    public void StopTimer()
    {
        _timerEnd = null;
        _tickTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _hub.SetChip("timer", null);
        Changed?.Invoke();
    }

    private void TickTimer()
    {
        if (_timerEnd is not { } end) return;
        var left = end - DateTime.Now;
        if (left <= TimeSpan.Zero)
        {
            _timerEnd = null;
            _tickTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _hub.SetChip("timer", null);
            Sound();
            _hub.Post(new Card
            {
                Glyph = G.Stopwatch,
                Title = _timerLabel.Length > 0 ? _timerLabel : "Таймер",
                Subtitle = $"Время вышло · {FormatSpan(_timerTotal)}",
                Tint = new SKColor(255, 159, 10),
                Buttons = [new CardButton("Ещё 5 мин", () => StartTimer(TimeSpan.FromMinutes(5), _timerLabel)),
                           new CardButton("Готово", () => { }, Primary: true)],
            });
            Changed?.Invoke();
            return;
        }

        _hub.SetChip("timer", new Chip("timer", G.Stopwatch, FormatSpan(left), new SKColor(255, 159, 10), Priority: 90));
    }

    public static string FormatSpan(TimeSpan t)
    {
        var s = (int)Math.Ceiling(t.TotalSeconds);
        return s >= 3600 ? $"{s / 3600}:{s % 3600 / 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
    }

    private void Reschedule()
    {
        DateTime? next;
        lock (_gate) next = _items.Count == 0 ? null : _items.Min(r => r.At);
        if (next is null) { _dueTimer.Change(Timeout.Infinite, Timeout.Infinite); return; }

        var delay = next.Value - DateTime.Now;
        if (delay < TimeSpan.FromMilliseconds(200)) delay = TimeSpan.FromMilliseconds(200);
        // Таймер .NET не принимает больше ~49 суток; перепроверяем раз в сутки.
        if (delay > TimeSpan.FromDays(1)) delay = TimeSpan.FromDays(1);
        _dueTimer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void CheckDue()
    {
        List<Reminder> due;
        lock (_gate) due = _items.Where(r => r.At <= DateTime.Now.AddMilliseconds(500)).ToList();

        foreach (var r in due)
        {
            Fire(r);

            lock (_gate)
            {
                switch (r.Repeat)
                {
                    case Repeat.Daily:
                        r.At = r.At.AddDays(1);
                        while (r.At <= DateTime.Now) r.At = r.At.AddDays(1);
                        break;
                    case Repeat.Weekly:
                        r.At = ReminderParser.NextWeekly(DateTime.Now, r.DaysMask, r.At.TimeOfDay);
                        break;
                    default:
                        _items.Remove(r);
                        break;
                }
            }
        }

        if (due.Count > 0)
        {
            Save();
            Changed?.Invoke();
        }
        Reschedule();
    }

    private void Fire(Reminder r)
    {
        Sound();
        var late = DateTime.Now - r.At > TimeSpan.FromMinutes(2);
        _hub.Post(new Card
        {
            Glyph = G.Alarm,
            Title = r.Text,
            Subtitle = late ? $"Пропущено · было {r.At:d MMM, HH:mm}" : "Напоминание",
            Tint = new SKColor(255, 159, 10),
            Buttons =
            [
                new CardButton("Через 10 мин", () =>
                {
                    lock (_gate)
                        _items.Add(new Reminder { Text = r.Text, At = DateTime.Now.AddMinutes(10) });
                    Save();
                    Reschedule();
                    Changed?.Invoke();
                }),
                new CardButton("Готово", () => { }, Primary: true),
            ],
        });
    }

    private static void Sound()
    {
        try { System.Media.SystemSounds.Exclamation.Play(); }
        catch { /* звук не критичен */ }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            var list = JsonSerializer.Deserialize<List<Reminder>>(File.ReadAllText(StorePath)) ?? [];
            _items.AddRange(list);
        }
        catch (Exception ex)
        {
            Log.Write("Напоминания не прочитаны: " + ex.Message);
            SafeFile.KeepBroken(StorePath);
        }
    }

    private void Save()
    {
        try
        {
            List<Reminder> copy;
            lock (_gate) copy = [.. _items];
            SafeFile.WriteAllText(StorePath, JsonSerializer.Serialize(copy));
        }
        catch (Exception ex) { Log.Write("Напоминания не сохранены: " + ex.Message); }
    }

    public void Dispose()
    {
        _dueTimer.Dispose();
        _tickTimer.Dispose();
    }
}
