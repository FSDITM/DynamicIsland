using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace DynamicIsland.Features;

internal enum EntryKind { App, File, Folder, Recent }

internal sealed record IndexEntry(string Name, string Path, EntryKind Kind, string? AppId = null);

/// <summary>
/// Индекс программ (меню «Пуск» и приложения Магазина) и файлов в пользовательских папках.
/// Строится в фоне при первом обращении и не мешает запуску.
/// </summary>
internal sealed class AppIndex
{
    private readonly Lock _gate = new();
    private List<IndexEntry> _apps = [];
    private List<IndexEntry> _files = [];
    private List<IndexEntry> _recent = [];
    private int _started;
    private DateTime _filesBuiltAt = DateTime.MinValue;
    private int _filesBuilding;

    public bool Ready { get; private set; }

    public void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
            _ = Task.Run(BuildAll);
        else if (DateTime.Now - _filesBuiltAt > TimeSpan.FromMinutes(10) &&
                 Interlocked.Exchange(ref _filesBuilding, 1) == 0)
        {
            // Обход папок идёт один. Раньше, пока первый не закончился, каждая нажатая
            // клавиша в командной строке запускала ещё один полный обход диска.
            _ = Task.Run(() =>
            {
                try { BuildFiles(); }
                catch (Exception ex) { Log.Write("Индекс файлов: " + ex.Message); }
                finally { Volatile.Write(ref _filesBuilding, 0); }
            });
        }
    }

    private void BuildAll()
    {
        try { BuildApps(); } catch (Exception ex) { Log.Write("Индекс программ: " + ex.Message); }
        try { BuildRecent(); } catch (Exception ex) { Log.Write("Недавние файлы: " + ex.Message); }
        Ready = true;
        Interlocked.Exchange(ref _filesBuilding, 1);
        try { BuildFiles(); } catch (Exception ex) { Log.Write("Индекс файлов: " + ex.Message); }
        finally { Volatile.Write(ref _filesBuilding, 0); }
        _ = Task.Run(LoadStoreApps);
    }

    private void BuildApps()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        };

        var found = new Dictionary<string, IndexEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            var programs = System.IO.Path.Combine(root, "Programs");
            if (!Directory.Exists(programs)) continue;
            foreach (var file in EnumerateSafe(programs, "*.lnk"))
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(file);
                if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("удалить", StringComparison.OrdinalIgnoreCase)) continue;
                found.TryAdd(name, new IndexEntry(name, file, EntryKind.App));
            }
        }

        lock (_gate) _apps = [.. found.Values];
    }

    /// <summary>Приложения Магазина: их нет в меню «Пуск» как ярлыков.</summary>
    private async Task LoadStoreApps()
    {
        try
        {
            var json = await SystemControl.CaptureAsync("powershell.exe",
                "-NoProfile -NonInteractive -Command \"Get-StartApps | Select-Object Name,AppID | ConvertTo-Json -Compress\"");
            if (string.IsNullOrWhiteSpace(json)) return;

            using var doc = JsonDocument.Parse(json);
            var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];

            lock (_gate)
            {
                // Список подменяется целиком, а не дополняется: поиск читает его без блокировки,
                // и Add посреди перебора ронял поиск («коллекция изменилась»).
                var next = new List<IndexEntry>(_apps);
                var known = new HashSet<string>(next.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
                foreach (var it in items)
                {
                    var name = it.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                    var id = it.TryGetProperty("AppID", out var a) ? a.GetString() ?? "" : "";
                    // Классические программы уже есть по ярлыкам; берём только то, что с «!» (пакеты Магазина).
                    if (name.Length == 0 || id.Length == 0 || !id.Contains('!') || known.Contains(name)) continue;
                    next.Add(new IndexEntry(name, "", EntryKind.App, id));
                }
                _apps = next;
            }
        }
        catch (Exception ex) { Log.Write("Приложения Магазина не получены: " + ex.Message); }
    }

    private void BuildRecent()
    {
        var recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        if (!Directory.Exists(recent)) return;

        var list = new DirectoryInfo(recent).EnumerateFiles("*.lnk")
            .OrderByDescending(f => f.LastWriteTime).Take(60)
            .Select(f => new IndexEntry(System.IO.Path.GetFileNameWithoutExtension(f.Name), f.FullName, EntryKind.Recent))
            .ToList();
        lock (_gate) _recent = list;
    }

    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "bin", "obj", "AppData", "$RECYCLE.BIN", "System Volume Information", ".vs", "__pycache__", ".venv", "venv",
    };

    private void BuildFiles()
    {
        var roots = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        };

        var list = new List<IndexEntry>(20000);
        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            Walk(root, 0, list);

        lock (_gate) _files = list;
        _filesBuiltAt = DateTime.Now;
    }

    private static void Walk(string dir, int depth, List<IndexEntry> into)
    {
        if (depth > 5 || into.Count > 80000) return;
        try
        {
            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                if (entry is DirectoryInfo d)
                {
                    if (Skip.Contains(d.Name)) continue;
                    into.Add(new IndexEntry(d.Name, d.FullName, EntryKind.Folder));
                    Walk(d.FullName, depth + 1, into);
                }
                else
                {
                    into.Add(new IndexEntry(entry.Name, entry.FullName, EntryKind.File));
                }
            }
        }
        catch { /* нет доступа — пропускаем */ }
    }

    private static IEnumerable<string> EnumerateSafe(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories).ToList(); }
        catch { return []; }
    }

    /// <summary>Оценка совпадения: больше — лучше, 0 — не подходит.</summary>
    private static int Score(string name, string query)
    {
        if (name.Equals(query, StringComparison.OrdinalIgnoreCase)) return 100;
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 80;

        var idx = name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (idx > 0 && !char.IsLetterOrDigit(name[idx - 1])) return 65;
        if (idx >= 0) return 50;

        // Инициалы: «vs» → Visual Studio.
        var initials = string.Concat(name.Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries).Select(w => w[0]));
        if (initials.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 40;

        // Все слова запроса встречаются в названии.
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1 && words.All(w => name.Contains(w, StringComparison.OrdinalIgnoreCase))) return 35;

        return 0;
    }

    public List<IndexEntry> SearchApps(string query, int take)
    {
        List<IndexEntry> src;
        lock (_gate) src = _apps;
        return src.Select(e => (e, s: Score(e.Name, query))).Where(x => x.s > 0)
                  .OrderByDescending(x => x.s).ThenBy(x => x.e.Name.Length).Take(take).Select(x => x.e).ToList();
    }

    public List<IndexEntry> SearchFiles(string query, int take)
    {
        List<IndexEntry> files, recent;
        lock (_gate) { files = _files; recent = _recent; }

        var scored = recent.Select(e => (e, s: Score(e.Name, query) + 10)).Where(x => x.s > 10)
            .Concat(files.Select(e => (e, s: Score(e.Name, query))).Where(x => x.s > 0));

        return scored.OrderByDescending(x => x.s).ThenBy(x => x.e.Name.Length)
                     .Select(x => x.e).DistinctBy(e => e.Path).Take(take).ToList();
    }

    public static void Open(IndexEntry e, bool admin = false)
    {
        try
        {
            if (e.AppId is not null)
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{e.AppId}") { UseShellExecute = true });
                return;
            }

            var info = new ProcessStartInfo(e.Path) { UseShellExecute = true };
            if (admin) info.Verb = "runas";
            Process.Start(info);
        }
        catch (Exception ex) { Log.Write("Не удалось открыть " + e.Name + ": " + ex.Message); }
    }

    public static void ShowInFolder(IndexEntry e)
    {
        try
        {
            if (e.Path.Length == 0) return;
            var target = e.Path;
            // Для ярлыков показываем сам ярлык: путь назначения без COM не прочитать.
            Process.Start("explorer.exe", $"/select,\"{target}\"");
        }
        catch (Exception ex) { Log.Write("Папка не открылась: " + ex.Message); }
    }
}
