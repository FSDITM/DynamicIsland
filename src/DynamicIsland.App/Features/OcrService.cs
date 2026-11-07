using System.Drawing;
using System.IO;
using System.Text;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using ZXing;
using ZXing.Common;

namespace DynamicIsland.Features;

/// <summary>Текст с картинки через распознаватели Windows (русский и английский) и QR через ZXing.</summary>
internal static class OcrService
{
    private sealed record Line(string Text, double MidY, double Height, double Left);

    private static OcrEngine? Create(string tag)
    {
        try { return OcrEngine.TryCreateFromLanguage(new Language(tag)); }
        catch { return null; }
    }

    /// <summary>null — не установлен ни один язык распознавания.</summary>
    public static async Task<string?> RecognizeAsync(Bitmap bitmap)
    {
        var ru = Create("ru");
        var en = Create("en-US") ?? Create("en");
        if (ru is null && en is null) return null;

        using var software = await ToSoftwareBitmapAsync(bitmap);

        var lists = new List<(List<Line> Lines, bool Cyrillic)>();
        if (ru is not null) lists.Add((await ReadAsync(ru, software), true));
        if (en is not null) lists.Add((await ReadAsync(en, software), false));
        if (lists.Count == 1) return Join(lists[0].Lines);

        // Склейка построчно: для каждой строки берём вариант, где символы
        // «родного» для движка алфавита больше — русская строка не должна
        // превращаться в латинскую кашу, и наоборот.
        var all = new List<(Line Line, bool Cyr)>();
        foreach (var (lines, cyr) in lists)
            foreach (var l in lines) all.Add((l, cyr));

        var chosen = new List<Line>();
        foreach (var item in all.OrderBy(a => a.Line.MidY))
        {
            var existing = chosen.FindIndex(c => Math.Abs(c.MidY - item.Line.MidY) < Math.Max(c.Height, item.Line.Height) * 0.6);
            if (existing < 0) { chosen.Add(item.Line); continue; }

            if (Score(item.Line.Text, item.Cyr) > Score(chosen[existing].Text, !item.Cyr))
                chosen[existing] = item.Line;
        }

        return Join(chosen);
    }

    private static double Score(string text, bool cyrillicEngine)
    {
        if (text.Length == 0) return 0;
        var native = text.Count(c => cyrillicEngine
            ? c is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё' or >= '0' and <= '9'
            : c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9');
        var letters = text.Count(char.IsLetterOrDigit);
        if (letters == 0) return 0;
        return (double)native / letters * Math.Log(letters + 2);
    }

    private static string Join(List<Line> lines) =>
        string.Join("\n", lines.OrderBy(l => l.MidY).Select(l => l.Text)).Trim();

    private static async Task<List<Line>> ReadAsync(OcrEngine engine, SoftwareBitmap bitmap)
    {
        var result = await engine.RecognizeAsync(bitmap);
        var list = new List<Line>();
        foreach (var line in result.Lines)
        {
            if (line.Words.Count == 0) continue;
            var top = line.Words.Min(w => w.BoundingRect.Top);
            var bottom = line.Words.Max(w => w.BoundingRect.Bottom);
            var left = line.Words.Min(w => w.BoundingRect.Left);
            list.Add(new Line(line.Text, (top + bottom) / 2, bottom - top, left));
        }
        return list;
    }

    private static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);

        var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(ms.ToArray());
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        stream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(stream);
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }

    // ---------- QR ----------

    /// <summary>Находит QR-код на картинке; null — не найден.</summary>
    public static string? DecodeQr(Bitmap bitmap)
    {
        try
        {
            var width = bitmap.Width;
            var height = bitmap.Height;
            var data = bitmap.LockBits(new Rectangle(0, 0, width, height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var bytes = new byte[Math.Abs(data.Stride) * height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            bitmap.UnlockBits(data);

            var source = new RGBLuminanceSource(bytes, width, height, RGBLuminanceSource.BitmapFormat.BGRA32);
            var reader = new BarcodeReaderGeneric
            {
                AutoRotate = true,
                Options = new DecodingOptions { TryHarder = true, PossibleFormats = [BarcodeFormat.QR_CODE] },
            };
            return reader.Decode(source)?.Text;
        }
        catch (Exception ex)
        {
            Log.Write("QR не прочитан: " + ex.Message);
            return null;
        }
    }
}
