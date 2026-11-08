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

    /// <summary>
    /// Насколько строка похожа на настоящий текст: доля символов в «чистых» словах.
    /// Чистое слово — одной письменности, без цифр посреди букв и без знаков внутри.
    /// Английский движок на русском слове выдаёт кашу вроде «IIPL.4BeT», и такая каша
    /// проваливается по этому правилу, даже если буквы в ней латинские.
    /// </summary>
    private static double Score(string text, bool cyrillicEngine)
    {
        var total = 0;
        var clean = 0;
        foreach (var raw in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var word = raw.Trim('.', ',', ';', ':', '!', '?', '«', '»', '(', ')', '"', '\'', '—', '-', '–');
            if (word.Length == 0) continue;
            total += word.Length;

            var cyr = word.Count(c => c is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё');
            var lat = word.Count(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z');
            var digits = word.Count(char.IsDigit);
            var other = word.Length - cyr - lat - digits;

            var ok = other == 0 && !(cyr > 0 && lat > 0) && !(digits > 0 && cyr + lat > 0);
            if (ok) clean += word.Length;
        }

        if (total == 0) return 0;
        var score = (double)clean / total;

        // При равенстве читаем тем движком, чей алфавит встречается в строке чаще.
        var nativeShare = text.Count(c => cyrillicEngine
            ? c is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё'
            : c is >= 'a' and <= 'z' or >= 'A' and <= 'Z') / (double)Math.Max(1, text.Length);
        return score + nativeShare * 0.05;
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
