using DynamicIsland.Services;
using SkiaSharp;

namespace DynamicIsland.Ui;

/// <summary>
/// Рисует содержимое островка: часы, заряд, плеер с обложкой и кнопками.
///
/// Все кисти, шрифты и фильтры создаются один раз в конструкторе. В оригинале
/// DynamicWin каждый объект на каждом кадре делал <c>new SKPaint</c> и
/// <c>SKImageFilter.CreateBlur</c> без освобождения — это нативные объекты,
/// и такой поток аллокаций давал микрофризы через финализатор.
/// </summary>
internal sealed class IslandContent : IDisposable
{
    private static readonly SKColor Background = new(10, 10, 12, 255);
    private static readonly SKColor Border = new(255, 255, 255, 28);
    private static readonly SKColor TextPrimary = new(245, 245, 247, 255);
    private static readonly SKColor TextMuted = new(150, 150, 158, 255);
    private static readonly SKColor Accent = new(72, 190, 255, 255);

    private readonly SKPaint _fill = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _stroke = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _shadow = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _text = new() { IsAntialias = true };
    private readonly SKPaint _image = new() { IsAntialias = true };

    private readonly SKFont _fontClock;
    private readonly SKFont _fontTitle;
    private readonly SKFont _fontSmall;

    private SKImageFilter? _shadowFilter;
    private float _shadowFilterRadius = -1f;

    // Иконки в единичных координатах: строятся один раз, в кадре только матрица.
    private readonly SKPath _iconPlay = BuildPlayIcon();
    private readonly SKPath _iconPause = BuildPauseIcon();
    private readonly SKPath _iconNext = BuildNextIcon();
    private readonly SKPath _iconPrevious = BuildPreviousIcon();
    private readonly SKPath _iconNote = BuildNoteIcon();
    private readonly SKPath _iconBolt = BuildBoltIcon();

    // Прямоугольники кнопок в физических пикселях — заполняются при отрисовке
    // и используются для попадания мышью.
    private SKRect _btnPrev, _btnPlay, _btnNext;
    private bool _controlsVisible;

    /// <summary>Кнопка под курсором — подсвечивается кружком. Ставит вызывающий.</summary>
    public IslandButton HoveredButton { get; set; } = IslandButton.None;

    public IslandContent()
    {
        var ui = SKTypeface.FromFamilyName("Segoe UI", SKFontStyleWeight.Normal,
            SKFontStyleWidth.Normal, SKFontStyleSlant.Upright) ?? SKTypeface.Default;
        var uiBold = SKTypeface.FromFamilyName("Segoe UI", SKFontStyleWeight.SemiBold,
            SKFontStyleWidth.Normal, SKFontStyleSlant.Upright) ?? ui;

        _fontClock = new SKFont(uiBold, 15) { Subpixel = true, Edging = SKFontEdging.SubpixelAntialias };
        _fontTitle = new SKFont(uiBold, 14) { Subpixel = true, Edging = SKFontEdging.SubpixelAntialias };
        _fontSmall = new SKFont(ui, 12) { Subpixel = true, Edging = SKFontEdging.SubpixelAntialias };
    }

    public void Draw(SKCanvas canvas, Island island, float stageWidth, float scale,
                     MediaSnapshot media, SKImage? artwork, PowerSnapshot power)
    {
        if (island.Visibility <= 0.01f) return;

        var rect = island.GetRect(stageWidth, scale);
        var radius = island.GetRadius(rect, scale);
        var alpha = island.Visibility;

        DrawShell(canvas, rect, radius, scale, island.Mode, alpha);

        if (island.Mode == IslandMode.Notch || island.Mode == IslandMode.Hidden)
        {
            _controlsVisible = false;
            return;
        }

        // Содержимое живёт внутри скруглённой формы — иначе при сжатии текст
        // вылезал бы за края.
        var saved = canvas.Save();
        using (var clip = new SKRoundRect(rect, radius))
            canvas.ClipRoundRect(clip, antialias: true);

        var expand = island.ExpandProgress;

        // Компактный слой гаснет по мере раскрытия, развёрнутый — проявляется.
        if (expand < 0.99f) DrawRestLayer(canvas, rect, scale, media, power, alpha * (1f - expand));
        if (expand > 0.01f) DrawExpandedLayer(canvas, rect, scale, media, artwork, power, alpha * expand);

        _controlsVisible = expand > 0.5f;
        canvas.RestoreToCount(saved);
    }

    private void DrawShell(SKCanvas canvas, SKRect rect, float radius, float scale,
                           IslandMode mode, float alpha)
    {
        var shadowRadius = (mode == IslandMode.Expanded ? 26f : 9f) * scale;
        EnsureShadowFilter(shadowRadius);

        using var round = new SKRoundRect(rect, radius);

        _shadow.Color = new SKColor(0, 0, 0, (byte)(alpha * (mode == IslandMode.Expanded ? 160 : 95)));
        _shadow.ImageFilter = _shadowFilter;
        canvas.DrawRoundRect(round, _shadow);

        _fill.ImageFilter = null;
        _fill.Color = Background.WithAlpha((byte)(alpha * 255));
        canvas.DrawRoundRect(round, _fill);

        _stroke.StrokeWidth = 1.25f * scale;
        _stroke.Color = Border.WithAlpha((byte)(alpha * Border.Alpha));
        canvas.DrawRoundRect(round, _stroke);
    }

    /// <summary>Компактный вид: часы слева, заряд или индикатор плеера справа.</summary>
    private void DrawRestLayer(SKCanvas canvas, SKRect rect, float scale,
                               MediaSnapshot media, PowerSnapshot power, float alpha)
    {
        if (alpha <= 0.01f) return;

        var pad = 16f * scale;
        var midY = rect.MidY + 5f * scale;

        _fontClock.Size = 15f * scale;
        _text.Color = TextPrimary.WithAlpha((byte)(alpha * 255));
        canvas.DrawText(DateTime.Now.ToString("HH:mm"), rect.Left + pad, midY,
            SKTextAlign.Left, _fontClock, _text);

        _fontSmall.Size = 12f * scale;

        if (media.HasSession && media.IsPlaying)
        {
            DrawEqualizer(canvas, rect.Right - pad - 14f * scale, rect.MidY, scale, alpha);
        }
        else if (power.HasBattery)
        {
            DrawBatteryLabel(canvas, rect.Right - pad, midY, scale, power, (byte)(alpha * 255));
        }
        else
        {
            _text.Color = TextMuted.WithAlpha((byte)(alpha * 255));
            canvas.DrawText(DateTime.Now.ToString("ddd, d MMM"), rect.Right - pad, midY,
                SKTextAlign.Right, _fontSmall, _text);
        }
    }

    /// <summary>Развёрнутый вид: обложка, название, исполнитель, управление.</summary>
    private void DrawExpandedLayer(SKCanvas canvas, SKRect rect, float scale,
                                   MediaSnapshot media, SKImage? artwork,
                                   PowerSnapshot power, float alpha)
    {
        if (alpha <= 0.01f) return;

        var pad = 16f * scale;
        var a = (byte)(alpha * 255);

        // Верхняя строка: часы и дата слева, заряд справа.
        _fontSmall.Size = 11.5f * scale;
        _text.Color = TextMuted.WithAlpha(a);
        canvas.DrawText(DateTime.Now.ToString("HH:mm  ddd, d MMM"), rect.Left + pad,
            rect.Top + 22f * scale, SKTextAlign.Left, _fontSmall, _text);

        DrawBatteryLabel(canvas, rect.Right - pad, rect.Top + 22f * scale, scale, power, a);

        // Обложка слева, кнопки справа, текст между ними. Нижние края обложки и
        // кнопок совпадают — это задаёт нижнюю границу содержимого.
        var artSize = 64f * scale;
        var artTop = rect.Top + 34f * scale;
        var artRect = new SKRect(rect.Left + pad, artTop, rect.Left + pad + artSize, artTop + artSize);

        DrawArtwork(canvas, artRect, scale, artwork, alpha);

        var controlsWidth = 26f * 3f * scale + 12f * 2f * scale;
        var controlsLeft = rect.Right - pad - controlsWidth;
        DrawControls(canvas, controlsLeft, artRect.MidY, scale, media, alpha);

        var textLeft = artRect.Right + 14f * scale;
        var available = controlsLeft - 16f * scale - textLeft;

        if (media.HasSession)
        {
            _fontTitle.Size = 14f * scale;
            _text.Color = TextPrimary.WithAlpha(a);
            canvas.DrawText(Truncate(media.Title, _fontTitle, available), textLeft,
                artTop + 24f * scale, SKTextAlign.Left, _fontTitle, _text);

            _fontSmall.Size = 12f * scale;
            _text.Color = TextMuted.WithAlpha(a);
            canvas.DrawText(Truncate(media.Artist, _fontSmall, available), textLeft,
                artTop + 44f * scale, SKTextAlign.Left, _fontSmall, _text);
        }
        else
        {
            _fontSmall.Size = 12f * scale;
            _text.Color = TextMuted.WithAlpha(a);
            canvas.DrawText("Ничего не воспроизводится", textLeft, artTop + 36f * scale,
                SKTextAlign.Left, _fontSmall, _text);
        }
    }

    private void DrawArtwork(SKCanvas canvas, SKRect rect, float scale, SKImage? artwork, float alpha)
    {
        var radius = 10f * scale;
        using var round = new SKRoundRect(rect, radius);

        if (artwork is not null)
        {
            var saved = canvas.Save();
            canvas.ClipRoundRect(round, antialias: true);
            _image.Color = SKColors.White.WithAlpha((byte)(alpha * 255));
            canvas.DrawImage(artwork, rect, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), _image);
            canvas.RestoreToCount(saved);
        }
        else
        {
            _fill.Color = new SKColor(38, 38, 44, (byte)(alpha * 255));
            canvas.DrawRoundRect(round, _fill);

            // Ноту рисуем вектором, а не символом ♪: в Segoe UI такого глифа нет,
            // и вместо него получался пустой квадрат.
            _fill.Color = TextMuted.WithAlpha((byte)(alpha * 170));
            DrawIcon(canvas, _iconNote, rect);
        }
    }

    private void DrawControls(SKCanvas canvas, float left, float centerY, float scale,
                              MediaSnapshot media, float alpha)
    {
        var size = 26f * scale;
        var gap = 12f * scale;

        _btnPrev = SKRect.Create(left, centerY - size / 2f, size, size);
        _btnPlay = SKRect.Create(_btnPrev.Right + gap, centerY - size / 2f, size, size);
        _btnNext = SKRect.Create(_btnPlay.Right + gap, centerY - size / 2f, size, size);

        var a = (byte)(alpha * (media.HasSession ? 255 : 90));

        // Подсветка кнопки под курсором: кружок за иконкой.
        if (media.HasSession && HoveredButton != IslandButton.None)
        {
            var target = HoveredButton switch
            {
                IslandButton.Previous => _btnPrev,
                IslandButton.PlayPause => _btnPlay,
                IslandButton.Next => _btnNext,
                _ => SKRect.Empty,
            };

            if (!target.IsEmpty)
            {
                _fill.Color = new SKColor(255, 255, 255, (byte)(alpha * 30));
                canvas.DrawCircle(target.MidX, target.MidY, size * 0.72f, _fill);
            }
        }

        _fill.Color = TextPrimary.WithAlpha(a);

        DrawIcon(canvas, _iconPrevious, _btnPrev);
        DrawIcon(canvas, media.IsPlaying ? _iconPause : _iconPlay, _btnPlay);
        DrawIcon(canvas, _iconNext, _btnNext);
    }

    /// <summary>
    /// Рисует иконку, построенную в единичных координатах, вписывая её в кнопку.
    /// Пути строятся один раз в конструкторе — в кадре остаётся только матрица.
    /// </summary>
    private void DrawIcon(SKCanvas canvas, SKPath icon, SKRect target)
    {
        var saved = canvas.Save();
        canvas.Translate(target.Left, target.Top);
        canvas.Scale(target.Width, target.Height);
        canvas.DrawPath(icon, _fill);
        canvas.RestoreToCount(saved);
    }

    private static SKPath BuildPlayIcon()
    {
        var b = new SKPathBuilder();
        b.MoveTo(0.30f, 0.17f);
        b.LineTo(0.79f, 0.50f);
        b.LineTo(0.30f, 0.83f);
        b.Close();
        return b.Detach();
    }

    private static SKPath BuildPauseIcon()
    {
        var b = new SKPathBuilder();
        b.AddRect(new SKRect(0.30f, 0.19f, 0.44f, 0.81f));
        b.AddRect(new SKRect(0.56f, 0.19f, 0.70f, 0.81f));
        return b.Detach();
    }

    private static SKPath BuildNextIcon()
    {
        var b = new SKPathBuilder();
        b.MoveTo(0.22f, 0.19f);
        b.LineTo(0.64f, 0.50f);
        b.LineTo(0.22f, 0.81f);
        b.Close();
        b.AddRect(new SKRect(0.68f, 0.19f, 0.78f, 0.81f));
        return b.Detach();
    }

    /// <summary>Молния зарядки — тоже вектором, символ ⚡ есть не во всех шрифтах.</summary>
    private static SKPath BuildBoltIcon()
    {
        var b = new SKPathBuilder();
        b.MoveTo(0.58f, 0.04f);
        b.LineTo(0.20f, 0.56f);
        b.LineTo(0.44f, 0.56f);
        b.LineTo(0.38f, 0.96f);
        b.LineTo(0.78f, 0.42f);
        b.LineTo(0.52f, 0.42f);
        b.Close();
        return b.Detach();
    }

    /// <summary>
    /// Заряд, выровненный по правому краю: при зарядке слева от процентов
    /// добавляется молния, и цвет меняется на акцентный.
    /// </summary>
    private void DrawBatteryLabel(SKCanvas canvas, float rightX, float baselineY,
                                  float scale, PowerSnapshot power, byte alpha)
    {
        if (!power.HasBattery) return;

        var label = $"{power.Percent}%";
        var color = power.IsCharging ? Accent : TextMuted;

        _text.Color = color.WithAlpha(alpha);
        canvas.DrawText(label, rightX, baselineY, SKTextAlign.Right, _fontSmall, _text);

        if (!power.IsCharging) return;

        var boltSize = _fontSmall.Size * 0.85f;
        var textWidth = _fontSmall.MeasureText(label);
        var boltLeft = rightX - textWidth - 3f * scale - boltSize;

        _fill.Color = color.WithAlpha(alpha);
        DrawIcon(canvas, _iconBolt, SKRect.Create(boltLeft, baselineY - boltSize * 0.85f, boltSize, boltSize));
    }

    /// <summary>Восьмая нота в единичных координатах — заглушка для обложки.</summary>
    private static SKPath BuildNoteIcon()
    {
        var b = new SKPathBuilder();
        b.AddOval(new SKRect(0.34f, 0.60f, 0.52f, 0.74f));   // головка
        b.AddRect(new SKRect(0.49f, 0.28f, 0.53f, 0.68f));   // штиль
        b.MoveTo(0.53f, 0.28f);                              // флажок
        b.LineTo(0.68f, 0.34f);
        b.LineTo(0.68f, 0.44f);
        b.LineTo(0.53f, 0.38f);
        b.Close();
        return b.Detach();
    }

    private static SKPath BuildPreviousIcon()
    {
        var b = new SKPathBuilder();
        b.MoveTo(0.78f, 0.19f);
        b.LineTo(0.36f, 0.50f);
        b.LineTo(0.78f, 0.81f);
        b.Close();
        b.AddRect(new SKRect(0.22f, 0.19f, 0.32f, 0.81f));
        return b.Detach();
    }

    /// <summary>Три столбика — намёк, что музыка играет, без анимации ради простоя.</summary>
    private void DrawEqualizer(SKCanvas canvas, float x, float centerY, float scale, float alpha)
    {
        _fill.Color = Accent.WithAlpha((byte)(alpha * 255));
        var w = 3f * scale;
        var gap = 4f * scale;
        Span<float> heights = [8f, 14f, 10f];

        for (var i = 0; i < heights.Length; i++)
        {
            var h = heights[i] * scale;
            canvas.DrawRoundRect(
                SKRect.Create(x + i * (w + gap), centerY - h / 2f, w, h),
                w / 2f, w / 2f, _fill);
        }
    }

    private static string Truncate(string text, SKFont font, float maxWidth)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (font.MeasureText(text) <= maxWidth) return text;

        var fitted = font.BreakText(text, maxWidth - font.MeasureText("…"));
        return fitted <= 0 ? "…" : string.Concat(text.AsSpan(0, fitted).TrimEnd(), "…");
    }

    private void EnsureShadowFilter(float radius)
    {
        // Квантуем: пересобирать нативный фильтр ради 0.1 px бессмысленно.
        var q = MathF.Round(radius * 2f) / 2f;
        if (_shadowFilter is not null && MathF.Abs(q - _shadowFilterRadius) < 0.01f) return;

        _shadowFilter?.Dispose();
        _shadowFilter = SKImageFilter.CreateBlur(q, q);
        _shadowFilterRadius = q;
    }

    /// <summary>Какая кнопка под точкой (физические пиксели сцены).</summary>
    public IslandButton HitButton(float x, float y)
    {
        if (!_controlsVisible) return IslandButton.None;
        if (_btnPlay.Contains(x, y)) return IslandButton.PlayPause;
        if (_btnNext.Contains(x, y)) return IslandButton.Next;
        if (_btnPrev.Contains(x, y)) return IslandButton.Previous;
        return IslandButton.None;
    }

    public void Dispose()
    {
        _fill.Dispose();
        _stroke.Dispose();
        _shadow.Dispose();
        _text.Dispose();
        _image.Dispose();
        _fontClock.Dispose();
        _fontTitle.Dispose();
        _fontSmall.Dispose();
        _shadowFilter?.Dispose();
        _iconPlay.Dispose();
        _iconPause.Dispose();
        _iconNext.Dispose();
        _iconPrevious.Dispose();
        _iconNote.Dispose();
        _iconBolt.Dispose();
    }
}
