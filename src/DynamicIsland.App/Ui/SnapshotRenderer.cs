using DynamicIsland.Services;
using SkiaSharp;

namespace DynamicIsland.Ui;

/// <summary>
/// Рендерит состояния островка в PNG тем же кодом, что рисует на экране.
///
/// Нужно, чтобы вёрстку можно было проверить без снимков экрана: подложка
/// светлая и тёмная показывает, как читается текст и края на разном фоне.
/// </summary>
internal static class SnapshotRenderer
{
    public static void RenderAll(string directory, float scale = 1.5f)
    {
        Directory.CreateDirectory(directory);

        var cases = new (string Name, IslandMode Mode, MediaSnapshot Media, bool WithArt)[]
        {
            ("01-покой-батарея", IslandMode.Rest, MediaSnapshot.Empty, false),
            ("02-покой-играет", IslandMode.Rest,
                new MediaSnapshot(true, true, "Bohemian Rhapsody", "Queen", "spotify"), false),
            ("03-раскрыт-пусто", IslandMode.Expanded, MediaSnapshot.Empty, false),
            ("04-раскрыт-играет", IslandMode.Expanded,
                new MediaSnapshot(true, true, "Bohemian Rhapsody", "Queen", "spotify"), true),
            ("05-раскрыт-пауза", IslandMode.Expanded,
                new MediaSnapshot(true, false, "Очень длинное название трека которое обязано обрезаться", "Исполнитель с длинным именем", "spotify"), true),
            ("06-полоска", IslandMode.Notch, MediaSnapshot.Empty, false),
        };

        // Один из снимков делаем на зарядке, чтобы проверить молнию и акцентный цвет.
        var power = new PowerSnapshot(72, false, true);
        var charging = new PowerSnapshot(41, true, true);

        foreach (var (name, mode, media, withArt) in cases)
        {
            using var art = withArt ? MakeFakeArtwork() : null;
            Render(Path.Combine(directory, name + ".png"), mode, media, art, power, scale);
        }

        Render(Path.Combine(directory, "07-покой-зарядка.png"), IslandMode.Rest,
            MediaSnapshot.Empty, null, charging, scale);

        // Эквалайзер в трёх фазах — проверяем, что столбики не ходят синхронно.
        for (var i = 0; i < 3; i++)
        {
            Render(Path.Combine(directory, $"10-эквалайзер-фаза{i + 1}.png"), IslandMode.Rest,
                new MediaSnapshot(true, true, "", "", "spotify"), null, power, scale,
                phase: 0.17f * (i + 1));
        }
        using (var art = MakeFakeArtwork())
            Render(Path.Combine(directory, "08-раскрыт-зарядка.png"), IslandMode.Expanded,
                new MediaSnapshot(true, true, "Дискотека Авария", "Заколебал ты", "spotify"),
                art, charging, scale);

        using (var art = MakeFakeArtwork())
            Render(Path.Combine(directory, "09-наведение-пауза.png"), IslandMode.Expanded,
                new MediaSnapshot(true, true, "Bohemian Rhapsody", "Queen", "spotify"),
                art, power, scale, IslandButton.PlayPause);
    }

    private static void Render(string path, IslandMode mode, MediaSnapshot media,
                               SKImage? artwork, PowerSnapshot power, float scale,
                               IslandButton hovered = IslandButton.None, float phase = 0f)
    {
        var width = (int)(620 * scale);
        var height = (int)(210 * scale);

        // Три полосы фона: тёмный, светлый и «шахматка» — чтобы увидеть края и тень.
        var info = new SKImageInfo(width, height * 3, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;

        var island = new Island();
        island.SetMode(mode);

        // Прогоняем пружины до состояния покоя — снимок должен показывать
        // установившуюся форму, а не первый кадр анимации.
        for (var i = 0; i < 600; i++) island.Update(1f / 120f);

        using var content = new IslandContent { MusicPhase = phase };

        // Подсветка кнопки требует, чтобы её прямоугольники уже были посчитаны:
        // первый проход в никуда задаёт раскладку, второй рисует с подсветкой.
        if (hovered != IslandButton.None)
        {
            using var probe = SKSurface.Create(new SKImageInfo(width, height));
            content.Draw(probe.Canvas, island, width, scale, media, artwork, power);
            content.HoveredButton = hovered;
        }

        DrawBand(canvas, 0, width, height, new SKColor(24, 24, 28), island, content, scale, media, artwork, power);
        DrawBand(canvas, height, width, height, new SKColor(238, 238, 242), island, content, scale, media, artwork, power);
        DrawCheckerBand(canvas, height * 2, width, height, island, content, scale, media, artwork, power);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.OpenWrite(path);
        data.SaveTo(stream);
    }

    private static void DrawBand(SKCanvas canvas, int offsetY, int width, int height, SKColor background,
                                 Island island, IslandContent content, float scale,
                                 MediaSnapshot media, SKImage? artwork, PowerSnapshot power)
    {
        var saved = canvas.Save();
        canvas.Translate(0, offsetY);
        canvas.ClipRect(SKRect.Create(0, 0, width, height));

        using (var bg = new SKPaint { Color = background })
            canvas.DrawRect(SKRect.Create(0, 0, width, height), bg);

        content.Draw(canvas, island, width, scale, media, artwork, power);
        canvas.RestoreToCount(saved);
    }

    private static void DrawCheckerBand(SKCanvas canvas, int offsetY, int width, int height,
                                        Island island, IslandContent content, float scale,
                                        MediaSnapshot media, SKImage? artwork, PowerSnapshot power)
    {
        var saved = canvas.Save();
        canvas.Translate(0, offsetY);
        canvas.ClipRect(SKRect.Create(0, 0, width, height));

        using var a = new SKPaint { Color = new SKColor(120, 120, 130) };
        using var b = new SKPaint { Color = new SKColor(160, 160, 170) };
        const int cell = 16;
        for (var y = 0; y < height; y += cell)
        for (var x = 0; x < width; x += cell)
            canvas.DrawRect(SKRect.Create(x, y, cell, cell), ((x / cell + y / cell) % 2 == 0) ? a : b);

        content.Draw(canvas, island, width, scale, media, artwork, power);
        canvas.RestoreToCount(saved);
    }

    /// <summary>Заглушка обложки: градиент, чтобы видеть скругление и вписывание.</summary>
    private static SKImage MakeFakeArtwork()
    {
        var info = new SKImageInfo(300, 300);
        using var surface = SKSurface.Create(info);
        using var shader = SKShader.CreateLinearGradient(
            new SKPoint(0, 0), new SKPoint(300, 300),
            [new SKColor(255, 94, 98), new SKColor(255, 195, 113)],
            null, SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = shader };
        surface.Canvas.DrawRect(SKRect.Create(0, 0, 300, 300), paint);
        return surface.Snapshot();
    }
}
