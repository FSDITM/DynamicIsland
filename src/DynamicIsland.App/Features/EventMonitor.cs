using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicIsland.Configuration;
using DynamicIsland.Interop;
using DynamicIsland.Ui;
using DynamicIsland.Ui.Tabs;
using Microsoft.Win32;
using SkiaSharp;

namespace DynamicIsland.Features;

/// <summary>
/// События системы для пилюли: микрофон и камера в работе, загрузки, раскладка и Caps Lock,
/// закачки Steam, запись и трансляция в OBS. Каждое включается отдельно в настройках.
/// </summary>
internal sealed partial class EventMonitor : IDisposable
{
    private readonly Settings _settings;
    private readonly PillHub _hub;
    private readonly Timer _fast;
    private readonly Timer _slow;
    private readonly CancellationTokenSource _cts = new();

    private nint _lastLayout;
    private nint _lastForeground;
    private bool _lastCaps;
    private bool _first = true;

    // Загрузки
    private FileSystemWatcher? _watcher;

    // Steam
    private long _steamLastBytes = -1;
    private int _slowTicks;

    // OBS
    private Task? _obsTask;

    public EventMonitor(Settings settings, PillHub hub)
    {
        _settings = settings;
        _hub = hub;
        _fast = new Timer(_ => TickFast(), null, Timeout.Infinite, Timeout.Infinite);
        _slow = new Timer(_ => TickSlow(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        _fast.Change(500, 300);
        _slow.Change(1500, 2500);
        StartDownloadWatcher();
        _obsTask = Task.Run(() => ObsLoop(_cts.Token));
    }

    // ---------- Быстрые: раскладка и Caps Lock ----------

    private void TickFast()
    {
        try
        {
            if (_settings.EventCaps)
            {
                var caps = (Native.GetKeyState(Native.VK_CAPITAL) & 1) != 0;
                if (caps != _lastCaps)
                {
                    _lastCaps = caps;
                    _hub.SetChip("caps", caps ? new Chip("caps", G.Keyboard, "CAPS", new SKColor(255, 159, 10), Priority: 60) : null);
                    if (!_first)
                        _hub.Post(new Card { Glyph = G.Keyboard, Title = caps ? "Caps Lock включён" : "Caps Lock выключен", Tint = new SKColor(255, 159, 10), Seconds = 1.4f });
                }
            }
            else _hub.SetChip("caps", null);

            if (_settings.EventLayout)
            {
                var fg = Native.GetForegroundWindow();
                var thread = fg == 0 ? 0 : Native.GetWindowThreadProcessId(fg, out _);
                var layout = Native.GetKeyboardLayout(thread);
                var sameWindow = fg == _lastForeground;
                _lastForeground = fg;
                if (layout != _lastLayout)
                {
                    var was = _lastLayout;
                    _lastLayout = layout;
                    // Смена окна тоже меняет раскладку (у каждого потока своя) — это не повод показывать карточку.
                    if (was != 0 && !_first && sameWindow)
                    {
                        var lang = (int)((long)layout & 0xFFFF);
                        var code = lang switch { 0x0419 => "RU", 0x0409 or 0x0809 => "EN", 0x0422 => "UK", 0x0407 => "DE", _ => CultureCode(lang) };
                        _hub.Post(new Card { Glyph = G.Keyboard, Title = $"Раскладка: {code}", Tint = new SKColor(100, 180, 255), Seconds = 1.2f });
                    }
                }
            }
            _first = false;
        }
        catch (Exception ex) { Log.Write("События (быстрые): " + ex.Message); }
    }

    private static string CultureCode(int lcid)
    {
        try { return new CultureInfo(lcid).TwoLetterISOLanguageName.ToUpperInvariant(); }
        catch { return "??"; }
    }

    // ---------- Медленные: микрофон/камера, загрузки, Steam ----------

    private void TickSlow()
    {
        try
        {
            if (_settings.EventMicCam)
            {
                var mic = InUse("microphone");
                var cam = InUse("webcam");
                _hub.SetChip("mic", mic ? new Chip("mic", G.Mic, "", new SKColor(255, 159, 10), Priority: 80) : null);
                _hub.SetChip("cam", cam ? new Chip("cam", G.Video, "", new SKColor(52, 199, 89), Priority: 85) : null);
            }
            else
            {
                _hub.SetChip("mic", null);
                _hub.SetChip("cam", null);
            }

            UpdateDownloads();
            if (_settings.EventSteam)
            {
                if (_slowTicks++ % 5 == 0) UpdateSteam();
            }
            else _hub.SetChip("steam", null);
        }
        catch (Exception ex) { Log.Write("События (медленные): " + ex.Message); }
    }

    /// <summary>Приложение сейчас использует устройство: в реестре согласий у него «начало» есть, а «конца» нет.</summary>
    internal static bool InUse(string capability)
    {
        foreach (var path in new[]
        {
            $@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\{capability}",
            $@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\{capability}\NonPackaged",
        })
        {
            using var root = Registry.CurrentUser.OpenSubKey(path);
            if (root is null) continue;

            foreach (var name in root.GetSubKeyNames())
            {
                if (name == "NonPackaged") continue;
                using var app = root.OpenSubKey(name);
                if (app?.GetValue("LastUsedTimeStart") is not long start || start == 0) continue;
                var stop = app.GetValue("LastUsedTimeStop") as long? ?? 0;
                if (stop == 0 || stop < start) return true;
            }
        }
        return false;
    }

    // ---------- Загрузки ----------

    private static readonly string[] PartialExt = [".crdownload", ".part", ".opdownload", ".download", ".partial"];

    private static string DownloadsDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    private static bool IsPartial(string? name) =>
        name is not null && PartialExt.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);

    private readonly Lock _dlGate = new();
    private readonly Dictionary<string, DateTime> _liveParts = new(StringComparer.OrdinalIgnoreCase);

    private void StartDownloadWatcher()
    {
        try
        {
            if (!Directory.Exists(DownloadsDir)) return;

            // Папка «Загрузки» бывает огромной, поэтому её не перечитывают по таймеру:
            // события файловой системы говорят, какие недокачанные файлы сейчас растут.
            _watcher = new FileSystemWatcher(DownloadsDir)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                InternalBufferSize = 16 * 1024,
                EnableRaisingEvents = true,
            };

            void Touch(string path)
            {
                lock (_dlGate) _liveParts[path] = DateTime.Now;
            }

            _watcher.Created += (_, e) => { if (IsPartial(e.Name)) Touch(e.FullPath); };
            _watcher.Changed += (_, e) => { if (IsPartial(e.Name)) Touch(e.FullPath); };
            _watcher.Deleted += (_, e) => { lock (_dlGate) _liveParts.Remove(e.FullPath); };
            _watcher.Renamed += (_, e) =>
            {
                if (!IsPartial(e.OldName)) { if (IsPartial(e.Name)) Touch(e.FullPath); return; }

                lock (_dlGate) _liveParts.Remove(e.OldFullPath);
                if (IsPartial(e.Name)) { Touch(e.FullPath); return; }
                if (!_settings.EventDownloads) return;

                try
                {
                    var path = e.FullPath;
                    var size = new FileInfo(path).Length;
                    _hub.Post(new Card
                    {
                        Glyph = G.Download,
                        Title = "Файл загружен",
                        Subtitle = $"{Path.GetFileName(path)} · {Fmt.Size(size)}",
                        Tint = new SKColor(52, 199, 89),
                        Seconds = 5f,
                        OnClick = () => System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""),
                    });
                }
                catch { /* файл мог быть сразу перемещён */ }
            };
        }
        catch (Exception ex) { Log.Write("Слежение за загрузками: " + ex.Message); }
    }

    private void UpdateDownloads()
    {
        if (!_settings.EventDownloads) { _hub.SetChip("download", null); return; }

        long total = 0;
        var count = 0;
        lock (_dlGate)
        {
            foreach (var path in _liveParts.Keys.ToList())
            {
                // Давно не росла — загрузка остановилась или файл исчез.
                if (DateTime.Now - _liveParts[path] > TimeSpan.FromSeconds(12)) { _liveParts.Remove(path); continue; }
                try { total += new FileInfo(path).Length; count++; }
                catch { _liveParts.Remove(path); }
            }
        }

        _hub.SetChip("download", count > 0
            ? new Chip("download", G.Download, Fmt.Size(total), new SKColor(100, 180, 255), Priority: 70)
            : null);
    }

    // ---------- Steam ----------

    private void UpdateSteam()
    {
        var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (string.IsNullOrEmpty(steam)) return;

        var libraries = new List<string> { Path.Combine(steam, "steamapps") };
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        try
        {
            if (File.Exists(vdf))
                foreach (Match m in PathLine().Matches(File.ReadAllText(vdf)))
                    libraries.Add(Path.Combine(m.Groups[1].Value.Replace("\\\\", "\\"), "steamapps"));
        }
        catch { /* необязательно */ }

        long bytes = 0, need = 0;
        var title = "";
        foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
        {
            foreach (var acf in Directory.EnumerateFiles(lib, "appmanifest_*.acf"))
            {
                try
                {
                    var text = File.ReadAllText(acf);
                    var flags = Field(text, "StateFlags");
                    var toDownload = Field(text, "BytesToDownload");
                    var downloaded = Field(text, "BytesDownloaded");
                    if ((flags & 2) == 0 || toDownload <= 0) continue;
                    bytes += downloaded;
                    need += toDownload;
                    title = Regex.Match(text, "\"name\"\\s+\"([^\"]+)\"").Groups[1].Value;
                }
                catch { /* файл мог быть занят Steam */ }
            }
        }

        var active = need > 0 && bytes != _steamLastBytes;
        _steamLastBytes = need > 0 ? bytes : -1;
        _hub.SetChip("steam", active ? new Chip("steam", G.Download, $"Steam {bytes * 100 / need}%", new SKColor(102, 192, 244), Priority: 50) : null);
        _ = title;
    }

    private static long Field(string text, string name)
    {
        var m = Regex.Match(text, $"\"{name}\"\\s+\"(\\d+)\"");
        return m.Success ? long.Parse(m.Groups[1].Value) : 0;
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex PathLine();

    // ---------- OBS: запись и трансляция по obs-websocket (без пароля) ----------

    private async Task ObsLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                // Сначала дешёвая проверка порта: каждая неудачная попытка WebSocket стоит системных дескрипторов.
                if (!await PortOpenAsync(4455, token))
                {
                    await Task.Delay(15000, token);
                    continue;
                }

                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri("ws://127.0.0.1:4455"), token);

                var hello = await ReceiveAsync(ws, token);
                if (hello is null) continue;
                using (var doc = JsonDocument.Parse(hello))
                {
                    // С паролем без него не войти — молча выходим, чтобы не мучить сервер.
                    if (doc.RootElement.GetProperty("d").TryGetProperty("authentication", out _))
                    {
                        await Task.Delay(TimeSpan.FromMinutes(5), token);
                        continue;
                    }
                }

                await SendAsync(ws, "{\"op\":1,\"d\":{\"rpcVersion\":1}}", token);
                await ReceiveAsync(ws, token);

                while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    var rec = await Request(ws, "GetRecordStatus", token);
                    var stream = await Request(ws, "GetStreamStatus", token);
                    var recording = rec?.GetProperty("outputActive").GetBoolean() ?? false;
                    var live = stream?.GetProperty("outputActive").GetBoolean() ?? false;

                    _hub.SetChip("obsrec", recording ? new Chip("obsrec", "", "OBS ●", new SKColor(255, 69, 58), Priority: 95, Pulse: true) : null);
                    _hub.SetChip("obslive", live ? new Chip("obslive", "", "LIVE", new SKColor(255, 69, 58), Priority: 96, Pulse: true) : null);
                    await Task.Delay(2000, token);
                }
            }
            catch (OperationCanceledException) { return; }
            catch
            {
                // OBS не запущен — обычное дело.
            }

            _hub.SetChip("obsrec", null);
            _hub.SetChip("obslive", null);
            try { await Task.Delay(8000, token); } catch (OperationCanceledException) { return; }
        }
    }

    private static async Task<bool> PortOpenAsync(int port, CancellationToken token)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(400);
            await client.ConnectAsync(System.Net.IPAddress.Loopback, port, timeout.Token);
            return client.Connected;
        }
        catch { return false; }
    }

    private static async Task<JsonElement?> Request(ClientWebSocket ws, string type, CancellationToken token)
    {
        var id = Guid.NewGuid().ToString("N");
        await SendAsync(ws, $"{{\"op\":6,\"d\":{{\"requestType\":\"{type}\",\"requestId\":\"{id}\"}}}}", token);

        for (var i = 0; i < 6; i++)
        {
            var text = await ReceiveAsync(ws, token);
            if (text is null) return null;
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.GetProperty("op").GetInt32() != 7) continue;
            var d = root.GetProperty("d");
            if (d.GetProperty("requestId").GetString() != id) continue;
            return d.TryGetProperty("responseData", out var data) ? data.Clone() : null;
        }
        return null;
    }

    private static Task SendAsync(ClientWebSocket ws, string text, CancellationToken token) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, token);

    private static async Task<string?> ReceiveAsync(ClientWebSocket ws, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public void Dispose()
    {
        _cts.Cancel();
        _fast.Dispose();
        _slow.Dispose();
        _watcher?.Dispose();
        _cts.Dispose();
    }
}
