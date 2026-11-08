using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media.Imaging;
using DynamicIsland.Configuration;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;

namespace DynamicIsland.Features;

/// <summary>
/// Запись области экрана в MP4 (H.264 через Media Foundation: штатный MediaTranscoder) или в GIF.
/// Кадры снимаются GDI и отдаются перекодировщику как несжатые BGRA.
/// </summary>
internal sealed class ScreenRecorder
{
    private readonly Settings _settings;
    private CancellationTokenSource? _cts;
    private Task? _worker;

    public bool Active => _cts is not null;
    public DateTime StartedAt { get; private set; }
    public string? OutputPath { get; private set; }

    /// <summary>Запись закончилась: путь к файлу или null при ошибке.</summary>
    public event Action<string?>? Finished;

    public ScreenRecorder(Settings settings) => _settings = settings;

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO { public int cbSize; public int flags; public nint hCursor; public int x; public int y; }

    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO info);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(nint hdc, int x, int y, nint icon, int cx, int cy, int step, nint brush, int flags);

    public void Start(int x, int y, int width, int height)
    {
        if (Active) return;

        // Кодеку нужны чётные размеры.
        width &= ~1;
        height &= ~1;
        if (width < 16 || height < 16) { Finished?.Invoke(null); return; }

        var folder = Ui.Tabs.ScreenshotPaths.RecordFolder(_settings);
        var gif = _settings.RecordGif;
        OutputPath = Path.Combine(folder, $"Запись {DateTime.Now:yyyy-MM-dd HH-mm-ss}.{(gif ? "gif" : "mp4")}");
        StartedAt = DateTime.Now;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var path = OutputPath;

        _worker = Task.Run(async () =>
        {
            string? result = path;
            try
            {
                if (gif) RecordGif(x, y, width, height, path, token);
                else await RecordMp4Async(x, y, width, height, path, token);
            }
            catch (Exception ex)
            {
                Log.Write("Запись экрана не удалась: " + ex);
                result = null;
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
            }
            Finished?.Invoke(result);
        });
    }

    public void Stop() => _cts?.Cancel();

    private void DrawCursor(Graphics g, int originX, int originY)
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref info) || (info.flags & 1) == 0) return;

        var hdc = g.GetHdc();
        try { DrawIconEx(hdc, info.x - originX - 4, info.y - originY - 4, info.hCursor, 0, 0, 0, 0, 3); }
        finally { g.ReleaseHdc(hdc); }
    }

    private Bitmap Frame(int x, int y, int width, int height)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(width, height), CopyPixelOperation.SourceCopy);
        DrawCursor(g, x, y);
        return bmp;
    }

    // ---------- MP4 ----------

    private async Task RecordMp4Async(int x, int y, int width, int height, string path, CancellationToken token)
    {
        var fps = _settings.RecordFps;
        var interval = TimeSpan.FromSeconds(1.0 / fps);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var video = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)width, (uint)height);
        video.FrameRate.Numerator = (uint)fps;
        video.FrameRate.Denominator = 1;
        var descriptor = new VideoStreamDescriptor(video);
        var source = new MediaStreamSource(descriptor)
        {
            BufferTime = TimeSpan.Zero,
            CanSeek = false,
        };

        TimeSpan next = TimeSpan.Zero;
        var stopped = false;

        source.SampleRequested += (_, args) =>
        {
            var deferral = args.Request.GetDeferral();
            try
            {
                if (token.IsCancellationRequested)
                {
                    stopped = true;
                    args.Request.Sample = null;
                    return;
                }

                // Выдерживаем частоту кадров по настоящим часам.
                var wait = next - clock.Elapsed;
                if (wait > TimeSpan.Zero) Thread.Sleep(wait);

                using var frame = Frame(x, y, width, height);
                var data = frame.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                // Несжатый RGB в Media Foundation идёт снизу вверх, а кадр GDI — сверху вниз:
                // без переворота строк видео получалось вверх ногами.
                var stride = data.Stride;
                var bytes = new byte[stride * height];
                for (var row = 0; row < height; row++)
                    Marshal.Copy(data.Scan0 + row * stride, bytes, (height - 1 - row) * stride, stride);
                frame.UnlockBits(data);

                var sample = MediaStreamSample.CreateFromBuffer(bytes.AsBuffer(), clock.Elapsed);
                sample.Duration = interval;
                args.Request.Sample = sample;
                next += interval;
                if (clock.Elapsed - next > TimeSpan.FromSeconds(1)) next = clock.Elapsed; // не догоняем отставание
            }
            finally { deferral.Complete(); }
        };

        var profile = MediaEncodingProfile.CreateMp4(_settings.RecordHighQuality ? VideoEncodingQuality.HD1080p : VideoEncodingQuality.HD720p);
        profile.Audio = null;
        profile.Video.Width = (uint)width;
        profile.Video.Height = (uint)height;
        profile.Video.FrameRate.Numerator = (uint)fps;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.Bitrate = (uint)(_settings.RecordHighQuality ? 14_000_000 : 6_000_000) * (uint)(fps >= 60 ? 2 : 1) / 1;

        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)!);
        var file = await folder.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);

        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
        var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(source, stream, profile);
        if (!prepared.CanTranscode)
            throw new InvalidOperationException("Кодек не готов: " + prepared.FailureReason);

        await prepared.TranscodeAsync();
        _ = stopped;
    }

    // ---------- GIF ----------

    private void RecordGif(int x, int y, int width, int height, string path, CancellationToken token)
    {
        // GIF тяжёлый: 12 кадров/с, ширина не больше 800.
        const int fps = 12;
        var scale = Math.Min(1.0, 800.0 / width);
        var gw = Math.Max(2, (int)(width * scale));
        var gh = Math.Max(2, (int)(height * scale));
        var frames = new List<BitmapSource>();
        var interval = TimeSpan.FromSeconds(1.0 / fps);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var next = TimeSpan.Zero;

        while (!token.IsCancellationRequested && frames.Count < fps * 60)
        {
            var wait = next - clock.Elapsed;
            if (wait > TimeSpan.Zero) Thread.Sleep(wait);

            using var frame = Frame(x, y, width, height);
            using var small = new Bitmap(gw, gh);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(frame, 0, 0, gw, gh);
            }
            frames.Add(MonitorShot.ToSource(small));
            next += interval;
        }

        var encoder = new GifBitmapEncoder();
        foreach (var f in frames) encoder.Frames.Add(BitmapFrame.Create(f));
        // Файл надо закрыть до правки: иначе PatchGif не сможет его прочитать.
        using (var fs = File.Create(path))
            encoder.Save(fs);

        // Стандартный кодировщик WPF не пишет задержку между кадрами и цикл: добавляем сами.
        PatchGif(path, (ushort)(100 / fps));
    }

    private static void PatchGif(string path, ushort delayCs)
    {
        var data = File.ReadAllBytes(path);
        using var ms = new MemoryStream();
        var i = 0;
        // Заголовок и логический экран.
        ms.Write(data, 0, 13);
        i = 13;
        if ((data[10] & 0x80) != 0)
        {
            var size = 3 * (1 << ((data[10] & 7) + 1));
            ms.Write(data, i, size);
            i += size;
        }

        // Бесконечный цикл.
        ms.Write([0x21, 0xFF, 0x0B, (byte)'N', (byte)'E', (byte)'T', (byte)'S', (byte)'C', (byte)'A', (byte)'P', (byte)'E', (byte)'2', (byte)'.', (byte)'0',
                  0x03, 0x01, 0x00, 0x00, 0x00]);

        var pendingGce = false;
        while (i < data.Length)
        {
            if (data[i] == 0x21 && i + 1 < data.Length && data[i + 1] == 0xF9)
            {
                // Расширение управления графикой: подставляем задержку.
                ms.Write([0x21, 0xF9, 0x04, (byte)(data[i + 3] & 0xFC | 0x04), (byte)(delayCs & 0xFF), (byte)(delayCs >> 8), data[i + 6], 0x00]);
                i += 8;
                pendingGce = true;
                continue;
            }
            if (data[i] == 0x21)
            {
                // Прочие расширения копируем как есть (кроме комментариев).
                var start = i;
                i += 2;
                while (i < data.Length && data[i] != 0) i += data[i] + 1;
                i++;
                if (data[start + 1] != 0xFE) ms.Write(data, start, i - start);
                continue;
            }
            if (data[i] == 0x2C)
            {
                // Перед каждым кадром — своё расширение с задержкой.
                if (!pendingGce)
                    ms.Write([0x21, 0xF9, 0x04, 0x04, (byte)(delayCs & 0xFF), (byte)(delayCs >> 8), 0x00, 0x00]);

                pendingGce = false;
                var start = i;
                i += 10;
                if ((data[start + 9] & 0x80) != 0) i += 3 * (1 << ((data[start + 9] & 7) + 1));
                i++; // LZW min code size
                while (i < data.Length && data[i] != 0) i += data[i] + 1;
                i++;
                ms.Write(data, start, i - start);
                continue;
            }
            ms.WriteByte(data[i++]);
        }

        File.WriteAllBytes(path, ms.ToArray());
    }
}
