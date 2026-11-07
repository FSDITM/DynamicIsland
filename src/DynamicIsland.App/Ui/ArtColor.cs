using SkiaSharp;

namespace DynamicIsland.Ui;

/// <summary>Главный цвет обложки: им островок красит полосу, эквалайзер и подчёркивание.</summary>
internal static class ArtColor
{
    /// <summary>
    /// Берёт насыщенный цвет, а не средний: средний у большинства обложек — грязно-серый.
    /// Затем поднимает яркость, чтобы цвет читался на чёрном.
    /// </summary>
    public static SKColor? Dominant(SKImage image)
    {
        try
        {
            const int n = 24;
            using var surface = SKSurface.Create(new SKImageInfo(n, n, SKColorType.Bgra8888, SKAlphaType.Premul));
            surface.Canvas.DrawImage(image, new SKRect(0, 0, n, n),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            using var snapshot = surface.Snapshot();
            using var small = SKBitmap.FromImage(snapshot);

            double best = 0;
            SKColor? pick = null;
            for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
            {
                var c = small.GetPixel(x, y);
                c.ToHsv(out _, out var sat, out var val);
                // Серые и почти чёрные пиксели в счёт не идут.
                var score = sat / 100.0 * Math.Min(val, 85) / 100.0;
                if (val < 25) score = 0;
                if (score > best) { best = score; pick = c; }
            }

            if (pick is not { } p || best < 0.08) return null;

            p.ToHsv(out var hue, out var s, out var v);
            return SKColor.FromHsv(hue, Math.Clamp(s, 45, 90), Math.Clamp(v, 78, 100));
        }
        catch { return null; }
    }
}
