using System.Diagnostics;
using System.Text.RegularExpressions;
using DynamicIsland.Configuration;
using DynamicIsland.Ui;
using Windows.Devices.Radios;

namespace DynamicIsland.Features;

/// <summary>Модификаторы при запуске строки результата.</summary>
[Flags]
internal enum CmdMods { None = 0, Ctrl = 1, Shift = 2 }

internal sealed class CmdItem
{
    public string Title { get; init; } = "";
    public string Sub { get; init; } = "";
    public string Glyph { get; init; } = G.Search;

    /// <summary>Выполнить. Возвращает true, если окно после этого надо закрыть.</summary>
    public Func<CmdMods, bool> Run { get; init; } = _ => true;

    /// <summary>Опасное действие: первый Enter только просит подтверждения.</summary>
    public bool NeedsConfirm { get; init; }
}

/// <summary>Связи командной строки с остальным островком.</summary>
internal sealed class CommandHost
{
    public required Settings Settings { get; init; }
    public required ClipboardService Clipboard { get; init; }
    public required ReminderService Reminders { get; init; }
    public required AppIndex Index { get; init; }
    public Action<int> OpenTab { get; init; } = _ => { };
    public Action OpenSettings { get; init; } = () => { };
    public Action NewNote { get; init; } = () => { };
    public Action<string> Capture { get; init; } = _ => { };
    public Action<string> Toast { get; init; } = _ => { };
    public Func<string, Task<string?>> SetCity { get; init; } = _ => Task.FromResult<string?>(null);
}

/// <summary>Превращает строку запроса в список результатов: программы, файлы, расчёты, команды.</summary>
internal sealed class CommandEngine(CommandHost host)
{
    private static readonly (string[] Names, string Title, string Glyph, string Action)[] SystemCommands =
    [
        (["wifi", "wi-fi", "вайфай", "вай-фай", "вайфай"], "Wi-Fi: включить или выключить", G.Wifi, "wifi"),
        (["bluetooth", "блютуз", "блютус", "bt"], "Bluetooth: включить или выключить", G.Bluetooth, "bt"),
        (["theme", "dark", "тема", "темная", "тёмная", "светлая", "dark mode"], "Переключить тёмную тему Windows", G.Moon, "theme"),
        (["lock", "блокировка", "заблокировать", "блок"], "Заблокировать компьютер", G.Lock, "lock"),
        (["sleep", "сон", "спать", "спящий"], "Спящий режим", G.Moon, "sleep"),
        (["restart", "reboot", "перезагрузка", "перезагрузить"], "Перезагрузить компьютер", G.Restart, "restart"),
        (["shutdown", "выключение", "выключить", "завершение"], "Выключить компьютер", G.Power, "shutdown"),
        (["settings", "настройки", "параметры острова"], "Настройки DynamicIsland", G.Settings, "settings"),
        (["clipboard", "буфер", "буфер обмена"], "Открыть историю буфера обмена", G.Clipboard, "tab1"),
        (["shelf", "полка"], "Открыть полку с файлами", G.Folder, "tab2"),
        (["notes", "заметки", "заметка"], "Открыть заметки", G.Notes, "tab4"),
        (["new note", "новая заметка", "создать заметку", "note"], "Создать новую заметку", G.Edit, "newnote"),
        (["controls", "управление"], "Открыть управление системой", G.Settings, "tab5"),
        (["weather", "погода"], "Открыть погоду", G.Cloud, "tab6"),
        (["screenshot", "скриншот", "снимок"], "Снимок области экрана", G.Camera, "cap:shot"),
        (["ocr", "распознать", "текст с экрана"], "Распознать текст с экрана", G.Text, "cap:ocr"),
        (["color", "цвет", "пипетка"], "Пипетка: взять цвет с экрана", G.Palette, "cap:color"),
        (["qr", "кьюар", "кюар"], "Считать QR-код с экрана", G.Zoom, "cap:qr"),
        (["record", "запись", "запись экрана"], "Запись экрана", G.Video, "cap:record"),
    ];

    public List<CmdItem> Query(string raw)
    {
        var q = raw.Trim();
        var result = new List<CmdItem>();
        if (q.Length == 0) return result;

        // Команда оболочки: «> ipconfig».
        if (q.StartsWith('>'))
        {
            var command = q[1..].Trim();
            if (command.Length > 0)
            {
                result.Add(Shell("PowerShell", command, "powershell.exe", $"-NoExit -Command \"{command.Replace("\"", "\\\"")}\""));
                result.Add(Shell("cmd", command, "cmd.exe", $"/K {command}"));
            }
            return result;
        }

        // Таймер.
        var timerMatch = Regex.Match(q, @"^(таймер|timer|засеки)\s*(?<d>.*)$", RegexOptions.IgnoreCase);
        if (timerMatch.Success)
        {
            var duration = ReminderParser.ParseDuration(timerMatch.Groups["d"].Value);
            result.Add(duration is { } d
                ? new CmdItem
                {
                    Title = $"Таймер на {ReminderService.FormatSpan(d)}",
                    Sub = "Enter — запустить, время будет в островке",
                    Glyph = G.Stopwatch,
                    Run = _ => { host.Reminders.StartTimer(d); host.Toast("Таймер запущен"); return true; },
                }
                : new CmdItem { Title = "Таймер", Sub = "Например: таймер 10 мин, таймер 1:30, timer 25m", Glyph = G.Stopwatch, Run = _ => false });
            return result;
        }

        // Напоминание.
        if (ReminderParser.LooksLikeReminder(q) || Regex.IsMatch(q, @"^(через|in\s+\d|завтра|послезавтра|сегодня|tomorrow)\b", RegexOptions.IgnoreCase))
        {
            var parsed = ReminderParser.Parse(q, DateTime.Now);
            if (parsed is not null)
            {
                var when = new Reminder { At = parsed.At, Repeat = parsed.Repeat, DaysMask = parsed.DaysMask }.When();
                result.Add(new CmdItem
                {
                    Title = parsed.Text,
                    Sub = $"Напомнить {when} · Enter — сохранить",
                    Glyph = G.Alarm,
                    Run = _ => { host.Reminders.Add(parsed); host.Toast($"Напомню {when}"); return true; },
                });
                return result;
            }

            if (ReminderParser.LooksLikeReminder(q))
            {
                result.Add(new CmdItem
                {
                    Title = "Напоминание",
                    Sub = "Например: напомни позвонить маме завтра в 9 · таблетки каждый день в 23:00",
                    Glyph = G.Alarm,
                    Run = _ => false,
                });
                return result;
            }
        }

        // Город для погоды: «город Казань».
        var city = Regex.Match(q, @"^(город|city|погода\s+в)\s+(?<c>.+)$", RegexOptions.IgnoreCase);
        if (city.Success)
        {
            var name = city.Groups["c"].Value.Trim();
            result.Add(new CmdItem
            {
                Title = $"Показывать погоду для: {name}",
                Sub = "Enter — найти город и сохранить",
                Glyph = G.Cloud,
                Run = mods =>
                {
                    _ = host.SetCity(name).ContinueWith(t =>
                        host.Toast(t.Result is { } found ? $"Город: {found}" : "Такой город не нашёлся"));
                    return true;
                },
            });
            return result;
        }

        // Калькулятор.
        if (Calc.TryEvaluate(q.TrimStart('='), out var value))
        {
            var text = Calc.Format(value);
            result.Add(new CmdItem
            {
                Title = "= " + text,
                Sub = "Enter — скопировать результат",
                Glyph = G.Add,
                Run = _ => { host.Clipboard.SetText(text); host.Toast("Скопировано: " + text); return true; },
            });
        }

        // Поиск по буферу обмена: «буфер слово».
        var clip = Regex.Match(q, @"^(буфер|кб|clip|clipboard)\s+(?<q>.+)$", RegexOptions.IgnoreCase);
        if (clip.Success)
        {
            var needle = clip.Groups["q"].Value;
            foreach (var e in host.Clipboard.Snapshot()
                         .Where(e => e.Kind != ClipKind.Image && e.Text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                         .Take(8))
            {
                var entry = e;
                result.Add(new CmdItem
                {
                    Title = entry.Preview.Length > 90 ? entry.Preview[..90] + "…" : entry.Preview,
                    Sub = "Из буфера · Enter — вставить",
                    Glyph = G.Clipboard,
                    Run = _ =>
                    {
                        // Окно закроется, фокус вернётся в прежнее приложение — тогда и вставляем.
                        Task.Delay(180).ContinueWith(_ => host.Clipboard.Paste(entry));
                        return true;
                    },
                });
            }
            if (result.Count > 0) return result;
        }

        // Системные команды.
        var lower = q.ToLowerInvariant();
        foreach (var (names, title, glyph, action) in SystemCommands)
        {
            if (!names.Any(n => n.StartsWith(lower, StringComparison.Ordinal) && lower.Length >= 2) &&
                !names.Contains(lower)) continue;
            result.Add(SystemItem(title, glyph, action));
        }

        // Программы.
        host.Index.EnsureStarted();
        foreach (var app in host.Index.SearchApps(q, 6))
        {
            var a = app;
            result.Add(new CmdItem
            {
                Title = a.Name,
                Sub = "Программа · Enter — запустить, Ctrl+Shift+Enter — от администратора",
                Glyph = G.Home,
                Run = mods =>
                {
                    if (mods.HasFlag(CmdMods.Ctrl) && !mods.HasFlag(CmdMods.Shift)) AppIndex.ShowInFolder(a);
                    else AppIndex.Open(a, admin: mods.HasFlag(CmdMods.Ctrl) && mods.HasFlag(CmdMods.Shift));
                    return true;
                },
            });
        }

        // Файлы и папки.
        if (q.Length >= 2)
        {
            foreach (var f in host.Index.SearchFiles(q, 5))
            {
                var file = f;
                result.Add(new CmdItem
                {
                    Title = file.Name,
                    Sub = (file.Kind == EntryKind.Folder ? "Папка · " : file.Kind == EntryKind.Recent ? "Недавнее · " : "Файл · ") +
                          System.IO.Path.GetDirectoryName(file.Path),
                    Glyph = file.Kind == EntryKind.Folder ? G.Folder : G.File,
                    Run = mods =>
                    {
                        if (mods.HasFlag(CmdMods.Ctrl)) AppIndex.ShowInFolder(file); else AppIndex.Open(file);
                        return true;
                    },
                });
            }
        }

        // Адрес сайта.
        if (Regex.IsMatch(q, @"^(https?://|www\.)\S+$", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(q, @"^[\w-]+(\.[\w-]+)+(/\S*)?$") && !q.Contains(' ') && q.Split('.')[^1].Length is >= 2 and <= 6 && !char.IsDigit(q[0]))
        {
            var url = q.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? q : "https://" + q;
            result.Add(new CmdItem
            {
                Title = $"Открыть {q}",
                Sub = "Сайт в браузере по умолчанию",
                Glyph = G.Globe,
                Run = _ => { OpenUrl(url); return true; },
            });
        }

        // Поиск в интернете — всегда последним.
        var searchUrl = string.Format(host.Settings.SearchUrl, Uri.EscapeDataString(q));
        result.Add(new CmdItem
        {
            Title = $"Искать в интернете: {q}",
            Sub = "Enter — открыть в браузере",
            Glyph = G.Search,
            Run = _ => { OpenUrl(searchUrl); return true; },
        });

        return result;
    }

    /// <summary>Единицы и валюты считаются асинхронно: валютам нужна сеть.</summary>
    public async Task<CmdItem?> ConversionAsync(string q)
    {
        var text = await Units.TryConvertAsync(q);
        if (text is null) return null;
        return new CmdItem
        {
            Title = "= " + text,
            Sub = "Enter — скопировать результат",
            Glyph = G.Refresh,
            Run = _ => { host.Clipboard.SetText(text); host.Toast("Скопировано: " + text); return true; },
        };
    }

    private CmdItem SystemItem(string title, string glyph, string action) => new()
    {
        Title = title,
        Sub = action is "restart" or "shutdown" ? "Нажмите Enter ещё раз, чтобы подтвердить" : "Системная команда",
        Glyph = glyph,
        NeedsConfirm = action is "restart" or "shutdown",
        Run = mods =>
        {
            switch (action)
            {
                case "wifi": _ = SystemControl.ToggleRadioAsync(RadioKind.WiFi); break;
                case "bt": _ = SystemControl.ToggleRadioAsync(RadioKind.Bluetooth); break;
                case "theme": SystemControl.ToggleTheme(); break;
                case "lock": SystemControl.Lock(); break;
                case "sleep": SystemControl.Sleep(); break;
                case "restart": SystemControl.Restart(); break;
                case "shutdown": SystemControl.ShutDown(); break;
                case "settings": host.OpenSettings(); break;
                case "newnote": host.NewNote(); break;
                case var t when t.StartsWith("tab"): host.OpenTab(int.Parse(t[3..])); break;
                case var c when c.StartsWith("cap:"): Task.Delay(220).ContinueWith(_ => host.Capture(c[4..])); break;
            }
            return true;
        },
    };

    private static CmdItem Shell(string name, string command, string file, string args) => new()
    {
        Title = command,
        Sub = $"Выполнить в {name}",
        Glyph = G.Chevron,
        Run = _ =>
        {
            try { Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Write("Команда не запущена: " + ex.Message); }
            return true;
        },
    };

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("Ссылка не открылась: " + ex.Message); }
    }
}
