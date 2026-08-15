using DynamicIsland.Configuration;
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
    private readonly Settings _settings;

    private static readonly SKColor Border = new(255, 255, 255, 28);
    private static readonly SKColor TextPrimary = new(245, 245, 247, 255);
    private static readonly SKColor TextMuted = new(150, 150, 158, 255);

    private SKColor Background => new(_settings.BackgroundColor);
    private SKColor Accent => new(_settings.AccentColor);

    private readonly SKPaint _fill = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _stroke = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _shadow = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _text = new() { IsAntialias = true };
    private readonly SKPaint _image = new() { IsAntialias = true };

    // Rubik — только для показаний: время, дата, заряд, тайминги на полосе.
    private readonly SKFont _fontClock;
    private readonly SKFont _fontMeta;

    // Название и исполнитель остаются на системном шрифте.
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

    /// <summary>Время в секундах для анимации эквалайзера. Ставит вызывающий.</summary>
    public float MusicPhase { get; set; }

    /// <summary>
    /// Пока полосу тянут мышью — позиция под курсором, иначе null.
    /// Показываем её вместо реальной, чтобы ползунок шёл за пальцем, а не
    /// возвращался на место до ответа плеера.
    /// </summary>
    public float? ScrubProgress { get; set; }

    /// <summary>Зона захвата полосы перемотки. Пустая, если перемотка недоступна.</summary>
    public SKRect SeekHitRect { get; private set; }

    /// <summary>Доля трека по координате X внутри полосы.</summary>
    public float ProgressFromX(float x)
    {
        if (_seekTrack.Width <= 0) return 0f;
        return Math.Clamp((x - _seekTrack.Left) / _seekTrack.Width, 0f, 1f);
    }

    private SKRect _seekTrack;

    public IslandContent(Settings settings)
    {
        _settings = settings;

        // Весь островок набран одним шрифтом — так это и выглядит на маках.
        // Смесь двух гарнитур в одном элементе читается как случайность.
        var regular = FontLibrary.FromFamilyOrDefault(settings.FontFamily, bold: false);
        var bold = FontLibrary.FromFamilyOrDefault(settings.FontFamily, bold: true);

        _fontClock = new SKFont(bold, 15) { Subpixel = true, Edging = SKFontEdging.SubpixelAntialias };
        _fontMeta = new SKFont(regular, 12) { Subpixel = true, Edging = SKFontEdging.SubpixelAntialias };
        _fontTitle = new SKFont(bold, 14) { Subpixel = true, Edging = SKFontEdging.SubpixelAntialias };
        _fontSmall = new SKFont(regular, 12) { Subpixel = true, Edging = SKFontEdging.SubpixelAntialias };
    }

    public void Draw(SKCanvas canvas, Island island, float stageWidth, float stageHeight, float scale,
                     MediaSnapshot media, SKImage? artwork, PowerSnapshot power)
    {
        if (island.Visibility <= 0.01f) return;

        var rect = island.GetRect(stageWidth, stageHeight, scale);
        var radius = island.GetRadius(rect, scale);
        var alpha = island.Visibility * _settings.Opacity;

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
        using var round = new SKRoundRect(rect, radius);

        // В свёрнутом виде тени нет: окно там обрезано ровно по полоске, чтобы
        // не отбирать нажатия у чужого окна, и тень всё равно была бы срезана.
        if (mode != IslandMode.Notch)
        {
            var strength = _settings.ShadowStrength;
            EnsureShadowFilter((mode == IslandMode.Expanded ? 26f : 9f) * strength * scale);

            var shadowAlpha = alpha * (mode == IslandMode.Expanded ? 160 : 95) * Math.Min(1f, strength);
            _shadow.Color = new SKColor(0, 0, 0, (byte)Math.Clamp(shadowAlpha, 0f, 255f));
            _shadow.ImageFilter = _shadowFilter;
            canvas.DrawRoundRect(round, _shadow);
        }

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
        var a = (byte)(alpha * 255);

        if (_settings.ShowClock)
        {
            _fontClock.Size = 15f * scale;
            _text.Color = TextPrimary.WithAlpha(a);
            canvas.DrawText(ClockText(), rect.Left + pad, midY, SKTextAlign.Left, _fontClock, _text);
        }

        _fontMeta.Size = 12f * scale;

        // Справа показываем самое актуальное: играющую музыку, иначе заряд,
        // иначе дату. Что именно доступно — решают настройки.
        if (_settings.ShowEqualizer && media.HasSession && media.IsPlaying)
        {
            DrawEqualizer(canvas, rect.Right - pad, rect.MidY, scale, alpha);
        }
        else if (_settings.ShowBattery && power.HasBattery)
        {
            DrawBatteryLabel(canvas, rect.Right - pad, midY, scale, power, a);
        }
        else if (_settings.ShowDate)
        {
            _text.Color = TextMuted.WithAlpha(a);
            canvas.DrawText(DateTime.Now.ToString("ddd, d MMM"), rect.Right - pad, midY,
                SKTextAlign.Right, _fontMeta, _text);
        }
    }

    private string ClockText() => DateTime.Now.ToString(_settings.Use24HourClock ? "HH:mm" : "h:mm tt");

    /// <summary>Развёрнутый вид: обложка, название, исполнитель, управление.</summary>
    private void DrawExpandedLayer(SKCanvas canvas, SKRect rect, float scale,
                                   MediaSnapshot media, SKImage? artwork,
                                   PowerSnapshot power, float alpha)
    {
        if (alpha <= 0.01f) return;

        var pad = 16f * scale;
        var a = (byte)(alpha * 255);

        // Верхняя строка: часы и дата слева, заряд справа.
        _fontMeta.Size = 11.5f * scale;
        _text.Color = TextMuted.WithAlpha(a);

        var header = (_settings.ShowClock, _settings.ShowDate) switch
        {
            (true, true) => ClockText() + DateTime.Now.ToString("  ddd, d MMM"),
            (true, false) => ClockText(),
            (false, true) => DateTime.Now.ToString("ddd, d MMM"),
            _ => "",
        };
        if (header.Length > 0)
            canvas.DrawText(header, rect.Left + pad, rect.Top + 22f * scale,
                SKTextAlign.Left, _fontMeta, _text);

        if (_settings.ShowBattery)
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

        DrawSeekBar(canvas, rect, scale, media, alpha);
    }

    /// <summary>
    /// Полоса перемотки со временем по краям. Рисуется только когда плеер
    /// сообщил длительность и разрешил перемотку — не все источники это умеют.
    /// </summary>
    private void DrawSeekBar(SKCanvas canvas, SKRect rect, float scale, MediaSnapshot media, float alpha)
    {
        if (!media.HasTimeline)
        {
            SeekHitRect = SKRect.Empty;
            _seekTrack = SKRect.Empty;
            return;
        }

        var pad = 16f * scale;
        var a = (byte)(alpha * 255);
        var progress = ScrubProgress ?? media.Progress;

        _fontMeta.Size = 11f * scale;
        var elapsed = FormatTime(ScrubProgress is { } s
            ? TimeSpan.FromSeconds(media.Duration.TotalSeconds * s)
            : media.EffectivePosition);
        var total = FormatTime(media.Duration);

        var elapsedWidth = _fontMeta.MeasureText(elapsed);
        var totalWidth = _fontMeta.MeasureText(total);

        // Привязка к нижнему краю, а не к фиксированной высоте: раскрытый
        // размер настраивается, и полоса должна следовать за ним.
        var centerY = rect.Bottom - 20f * scale;
        var gap = 9f * scale;
        var trackLeft = rect.Left + pad + elapsedWidth + gap;
        var trackRight = rect.Right - pad - totalWidth - gap;
        if (trackRight <= trackLeft) return;

        var thickness = 4f * scale;
        _seekTrack = new SKRect(trackLeft, centerY - thickness / 2f, trackRight, centerY + thickness / 2f);

        // Зона захвата заметно выше самой полосы — иначе в 4 пикселя не попасть.
        SeekHitRect = new SKRect(trackLeft - gap, centerY - 11f * scale,
                                 trackRight + gap, centerY + 11f * scale);

        _text.Color = TextMuted.WithAlpha(a);
        canvas.DrawText(elapsed, rect.Left + pad, centerY + 4f * scale, SKTextAlign.Left, _fontMeta, _text);
        canvas.DrawText(total, rect.Right - pad, centerY + 4f * scale, SKTextAlign.Right, _fontMeta, _text);

        var radius = thickness / 2f;

        _fill.Color = new SKColor(255, 255, 255, (byte)(alpha * 38));
        canvas.DrawRoundRect(_seekTrack, radius, radius, _fill);

        var filled = _seekTrack;
        filled.Right = _seekTrack.Left + _seekTrack.Width * progress;
        if (filled.Width > 0.5f)
        {
            _fill.Color = Accent.WithAlpha(a);
            canvas.DrawRoundRect(filled, radius, radius, _fill);
        }

        // Кружок-бегунок появляется при наведении и при перетаскивании.
        if (SeekHovered || ScrubProgress is not null)
        {
            _fill.Color = SKColors.White.WithAlpha(a);
            canvas.DrawCircle(filled.Right, centerY, (ScrubProgress is not null ? 6.5f : 5f) * scale, _fill);
        }
    }

    /// <summary>Курсор над полосой перемотки. Ставит вызывающий.</summary>
    public bool SeekHovered { get; set; }

    private static string FormatTime(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes}:{value.Seconds:00}";
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
        canvas.DrawText(label, rightX, baselineY, SKTextAlign.Right, _fontMeta, _text);

        if (!power.IsCharging) return;

        var boltSize = _fontMeta.Size * 0.85f;
        var textWidth = _fontMeta.MeasureText(label);
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

    // Столбики ходят с разными частотами — при кратных они двигались бы
    // синхронно и выглядели как одна прыгающая полоска.
    private static readonly float[] BarSpeeds = [6.1f, 8.3f, 4.7f, 7.2f];
    private static readonly float[] BarPhases = [0f, 1.9f, 3.4f, 5.1f];

    /// <summary>
    /// Живой эквалайзер, выровненный по правому краю. Рисуется только когда
    /// музыка действительно играет — в паузе и без сессии его нет, и приложение
    /// снова уходит в простой.
    /// </summary>
    private void DrawEqualizer(SKCanvas canvas, float rightX, float centerY, float scale, float alpha)
    {
        _fill.Color = Accent.WithAlpha((byte)(alpha * 255));

        var w = 3f * scale;
        var gap = 3.5f * scale;
        var totalWidth = BarSpeeds.Length * w + (BarSpeeds.Length - 1) * gap;
        var left = rightX - totalWidth;

        // При выключенной анимации берём фиксированную фазу — столбики просто
        // стоят на разной высоте.
        var phase = _settings.AnimateEqualizer ? MusicPhase : 0.4f;

        for (var i = 0; i < BarSpeeds.Length; i++)
        {
            var wave = 0.5f + 0.5f * MathF.Sin(phase * BarSpeeds[i] + BarPhases[i]);
            var h = (5f + 11f * wave) * scale;

            canvas.DrawRoundRect(
                SKRect.Create(left + i * (w + gap), centerY - h / 2f, w, h),
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
        _fontMeta.Dispose();
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
