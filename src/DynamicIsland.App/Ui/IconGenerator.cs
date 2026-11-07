using System.IO;
using SkiaSharp;

namespace DynamicIsland.Ui;

/// <summary>
/// Рисует логотип приложения и собирает из него .ico.
///
/// Иконка сделана кодом, а не картинкой в репозитории: так её можно
/// пересобрать в любом размере, и она всегда согласована с фирменным цветом
/// островка. Форма — сам островок: тёмная пилюля с «глазком», как вырез
/// в верхней части экрана.
/// </summary>
internal static class IconGenerator
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

    public static void Generate(string icoPath, string? previewPath = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(icoPath)!);

        var frames = new List<(int Size, byte[] Png)>();
        foreach (var size in Sizes)
            frames.Add((size, RenderPng(size)));

        WriteIco(icoPath, frames);

        if (previewPath is not null) WritePreviewSheet(previewPath);
    }

    private static byte[] RenderPng(int size)
    {
        var info = new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        DrawLogo(canvas, size);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Логотип в квадрате заданной стороны.</summary>
    public static void DrawLogo(SKCanvas canvas, float size)
    {
        var rect = new SKRect(0, 0, size, size);

        // Плоская тёплая плитка: ни градиента, ни блика, ни свечения.
        var corner = size * 0.225f;
        using var tile = new SKRoundRect(rect, corner);
        using (var background = new SKPaint { IsAntialias = true, Color = new SKColor(0x16, 0x14, 0x11) })
            canvas.DrawRoundRect(tile, background);

        // Светлая капсула в верхней трети — остров у кромки экрана.
        var pillWidth = size * 0.70f;
        var pillHeight = size * (size < 32 ? 0.30f : 0.24f);
        var pill = SKRect.Create((size - pillWidth) / 2f, size * 0.40f - pillHeight / 2f, pillWidth, pillHeight);
        var pillRadius = pillHeight / 2f;

        using (var body = new SKPaint { IsAntialias = true, Color = new SKColor(0xF0, 0xED, 0xE6) })
            canvas.DrawRoundRect(new SKRoundRect(pill, pillRadius), body);

        // Единственный цветной знак — красновато-оранжевая точка справа: «сейчас что-то происходит».
        if (size >= 24)
        {
            using var dot = new SKPaint { IsAntialias = true, Color = new SKColor(0xFF, 0x6B, 0x4A) };
            canvas.DrawCircle(pill.Right - pillHeight * 0.52f, pill.MidY, pillHeight * 0.20f, dot);
        }

        // Три столбика слева — то, что остров показывает чаще всего.
        if (size >= 48)
        {
            using var bars = new SKPaint { IsAntialias = true, Color = new SKColor(0x16, 0x14, 0x11) };
            var barWidth = pillHeight * 0.13f;
            var gap = barWidth * 1.5f;
            var x = pill.Left + pillHeight * 0.55f;

            ReadOnlySpan<float> heights = [0.34f, 0.62f, 0.44f];
            foreach (var h in heights)
            {
                var barHeight = pillHeight * h;
                canvas.DrawRoundRect(
                    SKRect.Create(x, pill.MidY - barHeight / 2f, barWidth, barHeight),
                    barWidth / 2f, barWidth / 2f, bars);
                x += barWidth + gap;
            }
        }
    }

    /// <summary>
    /// Формат .ico: заголовок, таблица записей, следом сами кадры.
    /// Кадры кладём как PNG — Windows понимает это начиная с Vista, а размер
    /// файла выходит в разы меньше, чем при упаковке в BMP.
    /// </summary>
    private static void WriteIco(string path, List<(int Size, byte[] Png)> frames)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        writer.Write((ushort)0);              // зарезервировано
        writer.Write((ushort)1);              // тип: иконка
        writer.Write((ushort)frames.Count);

        var offset = 6 + frames.Count * 16;
        foreach (var (size, png) in frames)
        {
            writer.Write((byte)(size >= 256 ? 0 : size));  // 0 означает 256
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);            // палитра не используется
            writer.Write((byte)0);            // зарезервировано
            writer.Write((ushort)1);          // плоскостей
            writer.Write((ushort)32);         // бит на пиксель
            writer.Write(png.Length);
            writer.Write(offset);
            offset += png.Length;
        }

        foreach (var (_, png) in frames) writer.Write(png);
    }

    /// <summary>Лист со всеми размерами — чтобы глазами проверить читаемость.</summary>
    private static void WritePreviewSheet(string path)
    {
        const int cell = 272;
        var columns = Sizes.Length;
        var info = new SKImageInfo(cell * columns, cell + 40, SKColorType.Bgra8888, SKAlphaType.Premul);

        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(0x1A, 0x1A, 0x20));

        using var label = new SKPaint { Color = new SKColor(180, 180, 190), IsAntialias = true };
        using var font = new SKFont(SKTypeface.Default, 18);

        for (var i = 0; i < columns; i++)
        {
            var size = Sizes[i];
            var x = i * cell;

            var saved = canvas.Save();
            canvas.Translate(x + (cell - size) / 2f, (cell - size) / 2f + 20);
            DrawLogo(canvas, size);
            canvas.RestoreToCount(saved);

            canvas.DrawText($"{size}px", x + cell / 2f, cell + 26, SKTextAlign.Center, font, label);
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(path);
        data.SaveTo(file);
    }
}
