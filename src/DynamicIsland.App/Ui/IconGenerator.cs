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

        // Плитка со скруглением как у иконок системы.
        var corner = size * 0.225f;
        using var tile = new SKRoundRect(rect, corner);

        using (var background = new SKPaint { IsAntialias = true })
        {
            background.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(size, size),
                [new SKColor(0x5B, 0x8C, 0xFF), new SKColor(0x8B, 0x5C, 0xF6)],
                [0f, 1f], SKShaderTileMode.Clamp);
            canvas.DrawRoundRect(tile, background);
        }

        // Блик по верхней кромке — иконка перестаёт выглядеть плоской заливкой.
        using (var gloss = new SKPaint { IsAntialias = true })
        {
            gloss.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(0, size * 0.55f),
                [new SKColor(255, 255, 255, 64), new SKColor(255, 255, 255, 0)],
                [0f, 1f], SKShaderTileMode.Clamp);
            canvas.DrawRoundRect(tile, gloss);
        }

        // Сам островок.
        //
        // Он смещён в верхнюю треть, а не по центру: плитка читается как экран,
        // а пилюля — как вырез у его верхней кромки. По центру и с крупным
        // кружком у края фигура превращалась в тумблер из iOS.
        var pillWidth = size * 0.70f;
        var pillHeight = size * (size < 32 ? 0.28f : 0.225f);
        var pill = SKRect.Create((size - pillWidth) / 2f, size * 0.40f - pillHeight / 2f, pillWidth, pillHeight);
        var pillRadius = pillHeight / 2f;

        // Свечение под пилюлей: намёк, что островок светится поверх экрана.
        if (size >= 32)
        {
            using var glow = new SKPaint
            {
                IsAntialias = true,
                Color = new SKColor(0, 0, 0, 90),
                ImageFilter = SKImageFilter.CreateBlur(size * 0.045f, size * 0.045f),
            };
            var glowRect = pill;
            glowRect.Offset(0, size * 0.02f);
            canvas.DrawRoundRect(new SKRoundRect(glowRect, pillRadius), glow);
        }

        using (var body = new SKPaint { IsAntialias = true, Color = new SKColor(0x0A, 0x0A, 0x0C) })
            canvas.DrawRoundRect(new SKRoundRect(pill, pillRadius), body);

        // «Глазок» — объектив в вырезе. Маленький: стоит сделать его во всю
        // высоту пилюли, и получается рукоятка тумблера.
        if (size >= 32)
        {
            var lens = pillHeight * 0.23f;
            var cx = pill.Right - pillHeight * 0.55f;
            var cy = pill.MidY;

            using var ring = new SKPaint { IsAntialias = true };
            ring.Shader = SKShader.CreateRadialGradient(
                new SKPoint(cx - lens * 0.3f, cy - lens * 0.3f), lens * 1.6f,
                [new SKColor(0x6A, 0xA8, 0xFF), new SKColor(0x16, 0x2C, 0x63)],
                [0f, 1f], SKShaderTileMode.Clamp);
            canvas.DrawCircle(cx, cy, lens, ring);
        }

        // Три столбика слева — то, что островок показывает чаще всего.
        // Заодно окончательно уводят силуэт от тумблера.
        if (size >= 48)
        {
            using var bars = new SKPaint { IsAntialias = true, Color = new SKColor(0x7C, 0xC4, 0xFF) };
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

        // Намёк на экран под островком: мягкое свечение, как от подсветки.
        if (size >= 64)
        {
            using var screenGlow = new SKPaint { IsAntialias = true };
            screenGlow.Shader = SKShader.CreateRadialGradient(
                new SKPoint(size / 2f, size * 0.78f), size * 0.42f,
                [new SKColor(255, 255, 255, 46), new SKColor(255, 255, 255, 0)],
                [0f, 1f], SKShaderTileMode.Clamp);
            canvas.DrawRoundRect(tile, screenGlow);
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
