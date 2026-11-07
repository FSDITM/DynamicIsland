using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using DynamicIsland.Configuration;
using DynamicIsland.Ui;
using DynamicIsland.Ui.Tabs;
using SkiaSharp;

namespace DynamicIsland.Features;

/// <summary>Снимок области, текст и QR с экрана, пипетка и запись: порядок действий и карточки с результатом.</summary>
internal sealed class CaptureController
{
    private readonly Settings _settings;
    private readonly ClipboardService _clipboard;
    private readonly PillHub _hub;
    private readonly Timer _recTimer;
    private int _busy;

    public ScreenRecorder Recorder { get; }
    public List<string> Colors { get; } = [];

    public event Action? Changed;

    public CaptureController(Settings settings, ClipboardService clipboard, PillHub hub)
    {
        _settings = settings;
        _clipboard = clipboard;
        _hub = hub;
        Recorder = new ScreenRecorder(settings);
        Recorder.Finished += OnRecordingFinished;
        _recTimer = new Timer(_ => TickRecording(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Run(string kind)
    {
        switch (kind)
        {
            case "shot": _ = Guard(ScreenshotAsync); break;
            case "ocr": _ = Guard(OcrAsync); break;
            case "qr": _ = Guard(QrAsync); break;
            case "color": _ = Guard(ColorAsync); break;
            case "record": _ = ToggleRecordingAsync(); break;
        }
    }

    /// <summary>Два выделения одновременно не нужны: пока одно открыто, остальные команды игнорируются.</summary>
    private async Task Guard(Func<Task> action)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try { await action(); }
        catch (Exception ex)
        {
            Log.Write("Захват экрана: " + ex);
            _hub.Post(new Card { Glyph = G.Camera, Title = "Не получилось", Subtitle = ex.Message, Tint = new SKColor(255, 69, 58) });
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    // ---------- Снимок ----------

    private async Task ScreenshotAsync()
    {
        var shots = ScreenGrab.AllMonitors();
        try
        {
            var region = await CaptureOverlay.SelectRegionAsync(shots);
            if (region is null) return;

            using var crop = ScreenGrab.Crop(region.Shot.Bitmap, region.Rect);
            var source = MonitorShot.ToSource(crop);

            MarkupHost.Open(source, _settings, _clipboard, (path, png) =>
            {
                var thumb = ClipboardService.MakeThumb(png, 120);
                _hub.Post(new Card
                {
                    Glyph = G.Camera,
                    Title = path.Length > 0 ? "Снимок сохранён и скопирован" : "Снимок скопирован",
                    Subtitle = path.Length > 0 ? Path.GetFileName(path) : "Можно вставлять",
                    Image = thumb,
                    Seconds = 4f,
                    OnClick = path.Length > 0 ? () => RevealFile(path) : null,
                });
            });
        }
        finally
        {
            foreach (var s in shots) s.Dispose();
        }
    }

    // ---------- Текст с экрана ----------

    private async Task OcrAsync()
    {
        var shots = ScreenGrab.AllMonitors();
        try
        {
            var region = await CaptureOverlay.SelectRegionAsync(shots, "Выделите текст · Esc — отмена");
            if (region is null) return;

            using var crop = ScreenGrab.Crop(region.Shot.Bitmap, region.Rect);
            var text = await OcrService.RecognizeAsync(crop);

            if (text is null)
            {
                _hub.Post(new Card
                {
                    Glyph = G.Text, Title = "Нет языка распознавания",
                    Subtitle = "Добавьте русский в «Параметры → Время и язык»", Tint = new SKColor(255, 159, 10), Seconds = 6f,
                });
                return;
            }

            if (text.Length == 0)
            {
                _hub.Post(new Card { Glyph = G.Text, Title = "Текст не найден", Subtitle = "Попробуйте выделить крупнее", Tint = new SKColor(255, 159, 10) });
                return;
            }

            _clipboard.SetText(text);
            var first = text.Replace('\n', ' ');
            _hub.Post(new Card
            {
                Glyph = G.Text,
                Title = $"Текст скопирован · {text.Length} симв.",
                Subtitle = first.Length > 70 ? first[..70] + "…" : first,
                Seconds = 5f,
            });
        }
        finally
        {
            foreach (var s in shots) s.Dispose();
        }
    }

    // ---------- QR ----------

    private async Task QrAsync()
    {
        var shots = ScreenGrab.AllMonitors();
        try
        {
            string? text = null;
            foreach (var s in shots)
            {
                text = await Task.Run(() => OcrService.DecodeQr(s.Bitmap));
                if (text is not null) break;
            }

            if (text is null)
            {
                var region = await CaptureOverlay.SelectRegionAsync(shots, "QR не найден сам. Выделите его · Esc — отмена");
                if (region is not null)
                {
                    using var crop = ScreenGrab.Crop(region.Shot.Bitmap, region.Rect);
                    text = await Task.Run(() => OcrService.DecodeQr(crop));
                }
                else return;
            }

            if (text is null)
            {
                _hub.Post(new Card { Glyph = G.Zoom, Title = "QR-код не найден", Subtitle = "Выделите код покрупнее", Tint = new SKColor(255, 159, 10) });
                return;
            }

            _clipboard.SetText(text);
            var isUrl = Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
            _hub.Post(new Card
            {
                Glyph = G.Zoom,
                Title = "QR-код считан",
                Subtitle = text.Length > 70 ? text[..70] + "…" : text,
                Seconds = 6f,
                Buttons = isUrl
                    ? [new CardButton("Открыть", () => OpenUrl(text), Primary: true), new CardButton("Готово", () => { })]
                    : [],
            });
        }
        finally
        {
            foreach (var s in shots) s.Dispose();
        }
    }

    // ---------- Пипетка ----------

    private async Task ColorAsync()
    {
        var shots = ScreenGrab.AllMonitors();
        try
        {
            var picked = await CaptureOverlay.PickColorAsync(shots);
            if (picked is not { } c) return;

            var hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            Colors.Remove(hex);
            Colors.Insert(0, hex);
            if (Colors.Count > 12) Colors.RemoveAt(Colors.Count - 1);

            _clipboard.SetText(hex);
            Changed?.Invoke();
            _hub.Post(new Card
            {
                Glyph = G.Palette,
                Title = hex,
                Subtitle = $"Цвет скопирован · RGB {c.R}, {c.G}, {c.B}",
                Tint = new SKColor(c.R, c.G, c.B),
                Seconds = 4f,
            });
        }
        finally
        {
            foreach (var s in shots) s.Dispose();
        }
    }

    // ---------- Запись ----------

    private async Task ToggleRecordingAsync()
    {
        if (Recorder.Active)
        {
            Recorder.Stop();
            return;
        }

        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var shots = ScreenGrab.AllMonitors();
            RegionResult? region;
            try { region = await CaptureOverlay.SelectRegionAsync(shots, "Выделите область записи · Esc — отмена"); }
            finally { /* кадры нужны только для выделения */ }

            if (region is null)
            {
                foreach (var s in shots) s.Dispose();
                return;
            }

            var x = region.Shot.Bounds.Left + region.Rect.X;
            var y = region.Shot.Bounds.Top + region.Rect.Y;
            var w = region.Rect.Width;
            var h = region.Rect.Height;
            foreach (var s in shots) s.Dispose();

            // Дать окну выделения закрыться, чтобы рамка не попала в первый кадр.
            await Task.Delay(250);
            Recorder.Start(x, y, w, h);
            _recTimer.Change(0, 1000);
            Changed?.Invoke();
        }
        catch (Exception ex) { Log.Write("Запись не началась: " + ex); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private void TickRecording()
    {
        if (!Recorder.Active)
        {
            _recTimer.Change(Timeout.Infinite, Timeout.Infinite);
            return;
        }

        if (!_settings.EventRecording) return;
        var t = DateTime.Now - Recorder.StartedAt;
        _hub.SetChip("rec", new Chip("rec", "", $"● {(int)t.TotalMinutes}:{t.Seconds:00}", new SKColor(255, 69, 58), Priority: 100, Pulse: true));
    }

    private void OnRecordingFinished(string? path)
    {
        _recTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _hub.SetChip("rec", null);
        Changed?.Invoke();

        if (path is null || !File.Exists(path))
        {
            _hub.Post(new Card { Glyph = G.Video, Title = "Запись не удалась", Subtitle = "Подробности в dynamicisland.log", Tint = new SKColor(255, 69, 58) });
            return;
        }

        _hub.Post(new Card
        {
            Glyph = G.Video,
            Title = "Запись сохранена",
            Subtitle = Path.GetFileName(path) + " · " + Fmt.Size(new FileInfo(path).Length),
            Seconds = 6f,
            OnClick = () => RevealFile(path),
        });
    }

    // ---------- Утилиты ----------

    private static void RevealFile(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch (Exception ex) { Log.Write("Папка не открылась: " + ex.Message); }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("Ссылка не открылась: " + ex.Message); }
    }

    public void OpenFolder(bool recordings)
    {
        try
        {
            var dir = recordings ? ScreenshotPaths.RecordFolder(_settings) : ScreenshotPaths.Folder(_settings);
            Process.Start("explorer.exe", $"\"{dir}\"");
        }
        catch (Exception ex) { Log.Write("Папка не открылась: " + ex.Message); }
    }

    public void CopyColor(string hex) => _clipboard.SetText(hex);
}
