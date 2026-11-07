using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using DynamicIsland.Configuration;
using DynamicIsland.Interop;
using DynamicIsland.Platform;
using SkiaSharp;

namespace DynamicIsland.Features;

internal enum ClipKind { Text, Image, Files }

internal sealed class ClipEntry : IDisposable
{
    public long Id { get; init; }
    public DateTime Time { get; set; } = DateTime.Now;
    public ClipKind Kind { get; init; }
    public string Text { get; init; } = "";
    public byte[]? Png { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string[] Files { get; init; } = [];
    public bool Pinned { get; set; }
    public SKImage? Thumb { get; set; }

    public string Preview => Kind switch
    {
        ClipKind.Image => $"Изображение {Width}×{Height}",
        ClipKind.Files => Files.Length == 1 ? Path.GetFileName(Files[0]) : $"{Files.Length} файлов",
        _ => Text.Replace("\r", "").Replace('\n', ' ').Replace('\t', ' ').Trim(),
    };

    public void Dispose() => Thumb?.Dispose();
}

/// <summary>
/// История буфера обмена. Читает и пишет буфер на UI-потоке (ему нужен STA),
/// вставка кликом работает без отъёма фокуса: окно островка не активируется,
/// и Ctrl+V уходит туда, где человек печатал.
/// </summary>
internal sealed class ClipboardService
{
    private const long ImageBudgetBytes = 48L * 1024 * 1024;

    private readonly Settings _settings;
    private readonly Lock _gate = new();
    private readonly List<ClipEntry> _items = [];
    private long _nextId = 1;
    private uint _ignoreSeq;

    public event Action? Changed;

    public static string StorePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DynamicIsland", "clipboard.json");

    public ClipboardService(Settings settings)
    {
        _settings = settings;
        LoadPinned();
    }

    public List<ClipEntry> Snapshot()
    {
        lock (_gate) return [.. _items];
    }

    public int Count { get { lock (_gate) return _items.Count; } }

    /// <summary>Буфер изменился — читаем его (вызывается из потока окна).</summary>
    public void OnSystemChanged()
    {
        if (!_settings.ClipboardHistory) return;
        UiThread.Post(Capture);
    }

    private void Capture()
    {
        var seq = Native.GetClipboardSequenceNumber();
        if (seq == _ignoreSeq) return;

        IDataObject? data = null;
        for (var attempt = 0; attempt < 6 && data is null; attempt++)
        {
            try { data = Clipboard.GetDataObject(); }
            catch (COMException) { Thread.Sleep(30); }
        }
        if (data is null) return;

        try
        {
            // Менеджеры паролей помечают секреты — их в историю не берём.
            if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing")) return;
            if (data.GetDataPresent("CanIncludeInClipboardHistory") &&
                data.GetData("CanIncludeInClipboardHistory") is MemoryStream flag &&
                flag.Length >= 4 && BitConverter.ToInt32(flag.ToArray(), 0) == 0) return;

            ClipEntry? entry = null;

            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files &&
                files.Length > 0)
            {
                entry = new ClipEntry { Id = _nextId++, Kind = ClipKind.Files, Files = files, Text = string.Join("\n", files) };
            }
            else if (data.GetDataPresent(DataFormats.UnicodeText) &&
                     data.GetData(DataFormats.UnicodeText) is string text && !string.IsNullOrWhiteSpace(text))
            {
                if (text.Length > 200_000) text = text[..200_000];
                entry = new ClipEntry { Id = _nextId++, Kind = ClipKind.Text, Text = text };
            }
            else if (Clipboard.ContainsImage())
            {
                var source = Clipboard.GetImage();
                if (source is not null) entry = MakeImageEntry(source);
            }

            if (entry is null) return;
            Add(entry);
        }
        catch (Exception ex)
        {
            Log.Write("Буфер обмена не прочитан: " + ex.Message);
        }
    }

    private ClipEntry? MakeImageEntry(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        var png = ms.ToArray();

        var entry = new ClipEntry
        {
            Id = _nextId++,
            Kind = ClipKind.Image,
            Png = png,
            Width = source.PixelWidth,
            Height = source.PixelHeight,
        };
        entry.Thumb = MakeThumb(png);
        return entry;
    }

    public static SKImage? MakeThumb(byte[] png, int maxSide = 160)
    {
        try
        {
            using var bmp = SKBitmap.Decode(png);
            if (bmp is null) return null;
            var k = Math.Min(1f, maxSide / (float)Math.Max(bmp.Width, bmp.Height));
            if (k >= 1f) return SKImage.FromBitmap(bmp);
            using var small = bmp.Resize(new SKImageInfo((int)(bmp.Width * k), (int)(bmp.Height * k)),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            return SKImage.FromBitmap(small);
        }
        catch { return null; }
    }

    /// <summary>Кладёт запись наверх списка; одинаковый текст не дублируется.</summary>
    public void Add(ClipEntry entry)
    {
        lock (_gate)
        {
            if (entry.Kind != ClipKind.Image)
            {
                var same = _items.FindIndex(e => e.Kind == entry.Kind && e.Text == entry.Text);
                if (same >= 0)
                {
                    var old = _items[same];
                    old.Time = DateTime.Now;
                    _items.RemoveAt(same);
                    _items.Insert(0, old);
                    entry.Dispose();
                    goto done;
                }
            }

            _items.Insert(0, entry);
            Trim();
        }
    done:
        Changed?.Invoke();
    }

    private void Trim()
    {
        var limit = _settings.ClipboardLimit;
        while (_items.Count(e => !e.Pinned) > limit)
        {
            var idx = _items.FindLastIndex(e => !e.Pinned);
            if (idx < 0) break;
            _items[idx].Dispose();
            _items.RemoveAt(idx);
        }

        long total = _items.Where(e => e.Png is not null).Sum(e => (long)e.Png!.Length);
        while (total > ImageBudgetBytes)
        {
            var idx = _items.FindLastIndex(e => e.Png is not null && !e.Pinned);
            if (idx < 0) break;
            total -= _items[idx].Png!.Length;
            _items[idx].Dispose();
            _items.RemoveAt(idx);
        }
    }

    public void TogglePin(ClipEntry entry)
    {
        entry.Pinned = !entry.Pinned;
        SavePinned();
        Changed?.Invoke();
    }

    public void Remove(ClipEntry entry)
    {
        lock (_gate)
        {
            _items.Remove(entry);
            entry.Dispose();
        }
        SavePinned();
        Changed?.Invoke();
    }

    public void ClearUnpinned()
    {
        lock (_gate)
        {
            foreach (var e in _items.Where(e => !e.Pinned)) e.Dispose();
            _items.RemoveAll(e => !e.Pinned);
        }
        Changed?.Invoke();
    }

    /// <summary>Положить запись в буфер и нажать Ctrl+V в активном окне.</summary>
    public void Paste(ClipEntry entry, bool plain = false) => UiThread.Post(() =>
    {
        WriteToClipboard(entry, plain);
        Thread.Sleep(40);
        Native.SendPaste();
    });

    public void CopyOnly(ClipEntry entry) => UiThread.Post(() => WriteToClipboard(entry, false));

    private void WriteToClipboard(ClipEntry entry, bool plain)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                switch (entry.Kind)
                {
                    case ClipKind.Image when entry.Png is not null:
                        using (var ms = new MemoryStream(entry.Png))
                        {
                            var img = new BitmapImage();
                            img.BeginInit();
                            img.CacheOption = BitmapCacheOption.OnLoad;
                            img.StreamSource = ms;
                            img.EndInit();
                            img.Freeze();
                            Clipboard.SetImage(img);
                        }
                        break;
                    case ClipKind.Files when !plain:
                        var list = new System.Collections.Specialized.StringCollection();
                        list.AddRange(entry.Files);
                        Clipboard.SetFileDropList(list);
                        break;
                    default:
                        Clipboard.SetText(entry.Text);
                        break;
                }
                _ignoreSeq = Native.GetClipboardSequenceNumber();
                return;
            }
            catch (COMException) { Thread.Sleep(30); }
        }
    }

    /// <summary>Вставить текст из буфера без форматирования.</summary>
    public void PastePlainFromClipboard() => UiThread.Post(() =>
    {
        string? text = null;
        for (var attempt = 0; attempt < 6 && text is null; attempt++)
        {
            try { text = Clipboard.ContainsText() ? Clipboard.GetText() : null; break; }
            catch (COMException) { Thread.Sleep(30); }
        }
        if (string.IsNullOrEmpty(text)) return;

        WriteToClipboard(new ClipEntry { Kind = ClipKind.Text, Text = text }, true);
        Thread.Sleep(40);
        Native.SendPaste();
    });

    /// <summary>Текст из буфера прямо сейчас (для вкладки «Текст»).</summary>
    public static string? ReadText()
    {
        return UiThread.Run(() =>
        {
            for (var attempt = 0; attempt < 6; attempt++)
            {
                try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
                catch (COMException) { Thread.Sleep(30); }
            }
            return null;
        });
    }

    public void SetText(string text) => UiThread.Post(() =>
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                _ignoreSeq = Native.GetClipboardSequenceNumber();
                break;
            }
            catch (COMException) { Thread.Sleep(30); }
        }
        // Результат обработки тоже попадает в историю.
        Add(new ClipEntry { Id = _nextId++, Kind = ClipKind.Text, Text = text });
    });

    public void SetImage(byte[] png) => UiThread.Post(() =>
    {
        var entry = new ClipEntry { Id = _nextId++, Kind = ClipKind.Image, Png = png };
        using var bmp = SKBitmap.Decode(png);
        var withSize = new ClipEntry
        {
            Id = entry.Id,
            Kind = ClipKind.Image,
            Png = png,
            Width = bmp?.Width ?? 0,
            Height = bmp?.Height ?? 0,
            Thumb = MakeThumb(png),
        };
        WriteToClipboard(withSize, false);
        Add(withSize);
    });

    // ---------- Закреплённые записи переживают перезапуск ----------

    private sealed record PinnedRecord(string Text, DateTime Time);

    private void LoadPinned()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            var list = JsonSerializer.Deserialize<List<PinnedRecord>>(File.ReadAllText(StorePath)) ?? [];
            foreach (var p in list)
                _items.Add(new ClipEntry { Id = _nextId++, Kind = ClipKind.Text, Text = p.Text, Time = p.Time, Pinned = true });
        }
        catch (Exception ex) { Log.Write("Закреплённые записи не прочитаны: " + ex.Message); }
    }

    private void SavePinned()
    {
        try
        {
            List<PinnedRecord> list;
            lock (_gate)
                list = _items.Where(e => e.Pinned && e.Kind == ClipKind.Text)
                             .Select(e => new PinnedRecord(e.Text, e.Time)).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(list));
        }
        catch (Exception ex) { Log.Write("Закреплённые записи не сохранены: " + ex.Message); }
    }
}
