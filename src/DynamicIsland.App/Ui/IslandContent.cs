using DynamicIsland.Configuration;
using DynamicIsland.Features;
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

    private Palette _pal = Palette.Dark;
    private SKColor Border => _pal.Line;
    private SKColor TextPrimary => _pal.Primary;
    private SKColor TextMuted => _pal.Muted;

    /// <summary>Светлая тема островка (по настройке или по теме Windows).</summary>
    public bool Light { get; private set; }

    private SKColor Background => Light ? new SKColor(0xF2, 0xF4, 0xF7) : new SKColor(_settings.BackgroundColor);
    /// <summary>Главный цвет обложки играющего трека. Ставит вызывающий; null — обложки нет.</summary>
    public SKColor? ArtAccent { get; set; }

    private static readonly SKColor Ink = new(0xF0, 0xED, 0xE6);

    private SKColor Accent =>
        !_settings.AccentFromArt ? new SKColor(_settings.AccentColor)
        : ArtAccent ?? (Light ? new SKColor(0x1E, 0x1C, 0x18) : Ink);

    // ---------- 2.0: вкладки ----------

    public static readonly string[] TabGlyphs =
        [G.Home, G.Clipboard, G.Folder, G.Text, G.Notes, G.Settings, G.Cloud, G.Crop];

    public static readonly string[] TabNames =
        ["Главная", "Буфер", "Полка", "Текст", "Заметки", "Управление", "Погода", "Инструменты"];

    public UiKit Ui { get; }

    /// <summary>Чипы и карточки событий. Ставит вызывающий.</summary>
    public PillHub? Hub { get; set; }

    /// <summary>Вкладки панели; нулевая («Главная») рисуется самим островком.</summary>
    public ITabView?[] Views { get; } = new ITabView?[8];

    private int _activeTab;
    public int ActiveTab
    {
        get => _activeTab;
        set
        {
            value = Math.Clamp(value, 0, Views.Length - 1);
            if (value == _activeTab) return;
            _activeTab = value;
            Views[value]?.Activated();
        }
    }

    /// <summary>Курсор в физических пикселях сцены. Ставит вызывающий.</summary>
    public SKPoint CursorPhys { get; set; }

    /// <summary>Левая кнопка зажата. Ставит вызывающий.</summary>
    public bool MouseDown { get => Ui.MouseDown; set => Ui.MouseDown = value; }

    /// <summary>Подсказка поверх вкладок: что сейчас играет, чьи слова и т. п. Рисуется на «Главной».</summary>
    public Func<(string Text, float Seconds, float Length)?>? LyricLine { get; set; }

    // Время играющей строки идёт по часам кадров, а данные плеера лишь мягко подтягивают его:
    // плеер сообщает позицию толчками, и без сглаживания текст дёргался бы при каждой поправке.
    private string _smoothKey = "";
    private float _smoothTime;
    private float _smoothPhase;

    private float SmoothLineTime(string line, float reported)
    {
        var dt = Math.Clamp(MusicPhase - _smoothPhase, 0f, 0.1f);
        _smoothPhase = MusicPhase;

        if (line != _smoothKey || MathF.Abs(reported - _smoothTime) > 1.2f)
        {
            _smoothKey = line;
            _smoothTime = reported;
            return _smoothTime;
        }

        _smoothTime += dt;
        _smoothTime += (reported - _smoothTime) * MathF.Min(1f, dt * 2.5f);
        return _smoothTime;
    }

    /// <summary>
    /// Путь 0..1 по времени 0..1: разгон на первых <paramref name="ramp"/>, ровный ход, торможение на последних.
    /// Скорость нигде не прыгает, а на ровном участке постоянна.
    /// </summary>
    private static float Trapezoid(float t, float ramp)
    {
        var v = 1f / (1f - ramp);
        if (t < ramp) return v * t * t / (2f * ramp);
        if (t > 1f - ramp)
        {
            var r = 1f - t;
            return 1f - v * r * r / (2f * ramp);
        }
        return v * (ramp / 2f + (t - ramp));
    }

    /// <summary>В этом кадре строка песни движется: цикл отрисовки идёт в такт развёртке экрана.</summary>
    public bool LyricScrolling { get; private set; }

    /// <summary>
    /// Рисует строку в окне заданной ширины. Не влезает — плавно сдвигает её по мере пения,
    /// так что к концу строки виден её хвост; края окна мягко гаснут.
    /// </summary>
    private void DrawMarquee(SKCanvas canvas, string text, SKFont font, float x, float baseline,
                             float maxWidth, float seconds, float length, SKColor color, byte alpha, float scale)
    {
        var width = font.MeasureText(text);
        _text.Color = color.WithAlpha(alpha);

        if (width <= maxWidth)
        {
            canvas.DrawText(text, x, baseline, SKTextAlign.Left, font, _text);
            return;
        }

        LyricScrolling = true;

        // Текст едет в темпе песни: конец строки показывается ровно к тому моменту, когда её допели.
        // Время строки сглажено (SmoothLineTime), а кривая — трапеция: плавный разгон, ровный ход,
        // плавное торможение. Ни рывков, ни резкого старта.
        var travel = width - maxWidth;
        var from = length * 0.06f;
        var span = Math.Max(0.4f, length * 0.88f);
        var p = Math.Clamp((SmoothLineTime(text, seconds) - from) / span, 0f, 1f);
        var offset = travel * Trapezoid(p, 0.18f);

        var fade = Math.Min(14f * scale, maxWidth / 4f);
        var area = new SKRect(x, baseline - font.Size * 1.3f, x + maxWidth, baseline + font.Size * 0.5f);

        canvas.Save();
        canvas.ClipRect(area);

        // Край гаснет только там, где текст обрезан: в начале строки левый край остаётся чётким.
        var leftEdge = offset > 1f ? 0f : 1f;
        var rightEdge = width - maxWidth - offset > 1f ? 0f : 1f;
        using var shader = SKShader.CreateLinearGradient(
            new SKPoint(area.Left, 0), new SKPoint(area.Right, 0),
            [color.WithAlpha((byte)(alpha * leftEdge)), color.WithAlpha(alpha), color.WithAlpha(alpha), color.WithAlpha((byte)(alpha * rightEdge))],
            [0f, fade / maxWidth, 1f - fade / maxWidth, 1f], SKShaderTileMode.Clamp);
        var saved = _text.Shader;
        _text.Shader = shader;
        canvas.DrawText(text, x - offset, baseline, SKTextAlign.Left, font, _text);
        _text.Shader = saved;

        canvas.Restore();
    }

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

        _fontClock = new SKFont(bold, 15) { Subpixel = true, Edging = SKFontEdging.Antialias };
        _fontMeta = new SKFont(regular, 12) { Subpixel = true, Edging = SKFontEdging.Antialias };
        _fontTitle = new SKFont(bold, 14) { Subpixel = true, Edging = SKFontEdging.Antialias };
        _fontSmall = new SKFont(regular, 12) { Subpixel = true, Edging = SKFontEdging.Antialias };

        Ui = new UiKit(settings.FontFamily);
    }

    private void UpdateTheme()
    {
        Light = _settings.Theme switch
        {
            Configuration.IslandTheme.Light => true,
            Configuration.IslandTheme.System => ThemeProbe.AppsUseLightTheme(),
            _ => false,
        };
        _pal = Light ? Palette.Light : Palette.Dark;
        Ui.Pal = _pal;
        Ui.Accent = Accent;
        Ui.IconTint = _settings.IconColor != 0 ? new SKColor(_settings.IconColor) : null;
    }

    public void Draw(SKCanvas canvas, Island island, float stageWidth, float stageHeight, float scale,
                     MediaSnapshot media, SKImage? artwork, PowerSnapshot power)
    {
        if (island.Visibility <= 0.01f) return;

        UpdateTheme();
        Ui.NewFrame();
        LyricScrolling = false;

        var rect = island.GetRect(stageWidth, stageHeight, scale);
        var radius = island.GetRadius(rect, scale);
        var alpha = island.Visibility * _settings.Opacity;

        DrawShell(canvas, rect, radius, scale, island.Mode, alpha);

        if (island.Mode == IslandMode.Notch || island.Mode == IslandMode.Hidden)
        {
            _controlsVisible = false;
            if (island.Mode == IslandMode.Notch) DrawNotchInfo(canvas, rect, scale, media, alpha);
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

    /// <summary>
    /// Полоска у края не пустая: тонкая линия внутри показывает, что происходит.
    /// Играет музыка — ход трека; идёт запись — красная пульсация; тикает таймер — оранжевая.
    /// Если ничего не происходит, полоска остаётся гладкой.
    /// </summary>
    private void DrawNotchInfo(SKCanvas canvas, SKRect rect, float scale, MediaSnapshot media, float alpha)
    {
        var chips = Hub?.TopChips(8) ?? [];
        var rec = chips.FirstOrDefault(c => c.Key is "rec" or "obsrec" or "obslive");
        var timer = chips.FirstOrDefault(c => c.Key == "timer");

        var h = Math.Max(1.5f, 2f * scale);
        var inset = rect.Height * 0.5f;
        var track = new SKRect(rect.Left + inset, rect.MidY - h / 2f, rect.Right - inset, rect.MidY + h / 2f);
        if (track.Width < 8f) return;

        SKColor color;
        float fill;
        if (rec is not null)
        {
            var k = 0.5f + 0.5f * MathF.Sin(MusicPhase * 4.2f);
            color = new SKColor(255, 69, 58, (byte)(255 * (0.45f + 0.55f * k)));
            fill = 1f;
        }
        else if (timer is not null)
        {
            color = new SKColor(255, 159, 10);
            fill = 1f;
        }
        else if (media.HasSession && media.IsPlaying && media.HasTimeline)
        {
            color = Accent;
            fill = Math.Clamp(media.Progress, 0f, 1f);
        }
        else return;

        _fill.Color = new SKColor(255, 255, 255, (byte)(alpha * 38));
        canvas.DrawRoundRect(track, h / 2f, h / 2f, _fill);

        _fill.Color = color.WithAlpha((byte)(color.Alpha * alpha));
        canvas.DrawRoundRect(new SKRect(track.Left, track.Top, track.Left + track.Width * fill, track.Bottom), h / 2f, h / 2f, _fill);
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

            var shadowAlpha = alpha * (mode == IslandMode.Expanded ? 140 : 70) * Math.Min(1f, strength);
            _shadow.Color = new SKColor(0, 0, 0, (byte)Math.Clamp(shadowAlpha, 0f, 255f));
            _shadow.ImageFilter = _shadowFilter;
            canvas.DrawRoundRect(round, _shadow);
        }

        _fill.ImageFilter = null;
        _fill.Color = Background.WithAlpha((byte)(alpha * 255));
        canvas.DrawRoundRect(round, _fill);

        // Контура нет: чёрная капсула на чёрном экране с рамкой читается как наклейка.
        // Он нужен только светлой теме, где форму держит сам цвет.
        if (Light)
        {
            _stroke.StrokeWidth = 1f * scale;
            _stroke.Color = Border.WithAlpha((byte)(alpha * Border.Alpha));
            canvas.DrawRoundRect(round, _stroke);
        }
    }

    /// <summary>Компактный вид: часы слева, заряд или индикатор плеера справа.</summary>
    private void DrawRestLayer(SKCanvas canvas, SKRect rect, float scale,
                               MediaSnapshot media, PowerSnapshot power, float alpha)
    {
        if (alpha <= 0.01f) return;

        var pad = 16f * scale;
        var midY = rect.MidY + 5f * scale;
        var a = (byte)(alpha * 255);
        var gap = 12f * scale;

        var left = rect.Left + pad;
        if (_settings.ShowClock)
        {
            _fontClock.Size = 15f * scale;
            _text.Color = TextPrimary.WithAlpha(a);
            var clock = ClockText();
            canvas.DrawText(clock, left, midY, SKTextAlign.Left, _fontClock, _text);
            left += _fontClock.MeasureText(clock) + gap;
        }

        _fontMeta.Size = 12f * scale;

        // Справа показываем то, что помещается. Порядок важности: события (таймер, запись,
        // микрофон…), затем музыка, иначе заряд, иначе дата. Что не влезает рядом с часами —
        // не рисуется вовсе, чтобы ничего не наезжало друг на друга.
        var rightEdge = rect.Right - pad;
        var playing = _settings.ShowEqualizer && media.HasSession && media.IsPlaying;
        var room = rightEdge - left;
        var x = rightEdge;
        var drawn = 0;

        if (Hub is not null)
        {
            foreach (var chip in Hub.TopChips(3).Where(c => !(playing && c.Key == "weather")).Take(2))
            {
                var w = ChipWidth(chip) * scale;
                if (w > room) continue;

                Ui.Begin(canvas, rect, scale, alpha, CursorPhys);
                var leftLogical = DrawChip(chip, (x - rect.Left) / scale, Ui.Height / 2f);
                Ui.End();

                x = rect.Left + leftLogical * scale - gap;
                room = x - left;
                drawn++;
            }
        }

        if (drawn >= 2) return;

        if (playing)
        {
            var eqW = EqualizerWidth(scale);
            if (eqW <= room) DrawEqualizer(canvas, x, rect.MidY, scale, alpha);
        }
        else if (_settings.ShowBattery && power.HasBattery)
        {
            _fontMeta.Size = 12f * scale;
            var w = _fontMeta.MeasureText($"{power.Percent}") + 6f * scale + 16f * scale;
            if (w <= room) DrawBatteryLabel(canvas, x, midY, scale, power, a);
        }
        else if (_settings.ShowDate && drawn == 0)
        {
            var date = DateTime.Now.ToString("ddd, d MMM");
            if (_fontMeta.MeasureText(date) <= room)
            {
                _text.Color = TextMuted.WithAlpha(a);
                canvas.DrawText(date, x, midY, SKTextAlign.Right, _fontMeta, _text);
            }
        }

        // Слова песни — в оставшемся промежутке, только если он заметный.
        if (_settings.ShowClock && media.HasSession && media.IsPlaying &&
            LyricLine?.Invoke() is { } lyric)
        {
            var used = rightEdge - x + (playing ? EqualizerWidth(scale) + gap : 0f);
            var max = rightEdge - used - left;
            if (max > 60f * scale)
            {
                _fontMeta.Size = 11f * scale;
                DrawMarquee(canvas, lyric.Text, _fontMeta, left, midY, max, lyric.Seconds, lyric.Length, Accent, a, scale);
            }
        }
    }

    /// <summary>Ширина чипа в логических пикселях.</summary>
    private float ChipWidth(Chip chip) =>
        (chip.Text.Length > 0 ? Ui.Measure(chip.Text, 12f, true) + 5f : 0f) + (chip.Glyph.Length > 0 ? 13f : 0f);

    private static float EqualizerWidth(float scale) =>
        (BarSpeeds.Length * 3f + (BarSpeeds.Length - 1) * 3.5f) * scale;

    /// <summary>Чип справа налево: значок и текст. Возвращает левый край.</summary>
    private float DrawChip(Chip chip, float rightX, float centerY)
    {
        var color = chip.Color ?? Accent;
        if (chip.Pulse)
        {
            var k = 0.55f + 0.45f * MathF.Sin(MusicPhase * 4.2f);
            color = color.WithAlpha((byte)(255 * k));
        }

        var textW = chip.Text.Length > 0 ? Ui.Measure(chip.Text, 12f, true) : 0f;
        var x = rightX;
        if (textW > 0)
        {
            Ui.Text(chip.Text, x, centerY + 4.2f, 12f, chip.Pulse ? _pal.Primary : color, SKTextAlign.Right, true);
            x -= textW + 5f;
        }
        if (chip.Glyph.Length > 0)
        {
            Ui.Glyph(chip.Glyph, x - 6f, centerY, 13f, color);
            x -= 13f;
        }
        return x;
    }

    /// <summary>Карточка события вместо привычного содержимого развёрнутого островка.</summary>
    private void DrawCard(SKCanvas canvas, SKRect rect, float scale, float alpha, Card card)
    {
        Ui.Begin(canvas, rect, scale, alpha, CursorPhys);
        var w = Ui.Width;
        var h = Ui.Height;
        var tint = card.Tint ?? Accent;
        var hub = Hub;

        var hasButtons = card.Buttons.Count > 0;
        var bodyH = hasButtons ? h - 40f : h;

        // Тело карточки целиком кликабельно.
        if (card.OnClick is not null)
            Ui.Hit(new SKRect(0, 0, w, bodyH), () => { card.OnClick(); hub?.Dismiss(); });

        // Слева полоса цвета события; у снимка — миниатюра. Значок без круглой подложки.
        Ui.RoundRect(new SKRect(0, 14f, 3f, bodyH - 14f), 1.5f, tint);
        var avatarBox = card.RoundImage;
        var iconBox = avatarBox
            ? new SKRect(20f, bodyH / 2f - 22f, 64f, bodyH / 2f + 22f)
            : new SKRect(22f, bodyH / 2f - 24f, 70f, bodyH / 2f + 24f);
        if (card.Image is not null)
            Ui.Image(card.Image, iconBox, card.RoundImage ? 24f : 6f);
        else
            Ui.Glyph(card.Glyph, iconBox.Left + 12f, iconBox.MidY, 22f, tint);

        var tx = (card.Image is not null ? iconBox.Right : iconBox.Left + 34f) + 12f;
        var tw = w - tx - 16f;
        var titleY = card.Subtitle.Length > 0 || card.Progress is not null ? bodyH / 2f - 4f : bodyH / 2f + 5f;
        Ui.Text(Ui.Fit(card.Title, 14.5f, tw, bold: true), tx, titleY, 14.5f, _pal.Primary, bold: true);
        if (card.Subtitle.Length > 0)
        {
            // Сообщение может занять две строки: обрезать его до одной значило бы терять смысл.
            var lines = card.TwoLines ? 2 : 1;
            if (lines == 2) Ui.Paragraph(card.Subtitle, tx, titleY + 5f, tw, 12f, _pal.Muted, 15f, 2, ellipsis: true);
            else Ui.Text(Ui.Fit(card.Subtitle, 12f, tw), tx, titleY + 18f, 12f, _pal.Muted);
        }

        if (card.Progress is { } p)
        {
            var bar = new SKRect(tx, bodyH / 2f + 12f, tx + tw, bodyH / 2f + 16f);
            Ui.RoundRect(bar, 2f, _pal.Hover);
            Ui.RoundRect(new SKRect(bar.Left, bar.Top, bar.Left + bar.Width * Math.Clamp(p, 0f, 1f), bar.Bottom), 2f, tint);
        }

        if (hasButtons)
        {
            var bw = 88f;
            var x = w - 16f;
            for (var i = card.Buttons.Count - 1; i >= 0; i--)
            {
                var b = card.Buttons[i];
                var r = new SKRect(x - bw, h - 36f, x, h - 10f);
                Ui.Button(r, b.Label, () => { b.Click(); hub?.Dismiss(); }, primary: b.Primary);
                x -= bw + 8f;
            }
        }

        Ui.End();
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

        if (Hub?.Current is { } card)
        {
            DrawCard(canvas, rect, scale, alpha, card);
            return;
        }

        DrawTabStrip(canvas, rect, scale, alpha, power);

        // Полоса перемотки есть только на «Главной»: на других вкладках её зона не должна ловить нажатия.
        if (ActiveTab != 0) { SeekHitRect = SKRect.Empty; _seekTrack = SKRect.Empty; }

        // Остальные вкладки рисуют себя сами в отведённой области.
        if (ActiveTab != 0 && Views[ActiveTab] is { } view)
        {
            var area = new SKRect(rect.Left + pad, rect.Top + 44f * scale, rect.Right - pad, rect.Bottom - 12f * scale);
            Ui.Begin(canvas, area, scale, alpha, CursorPhys);
            view.Draw(Ui);
            Ui.End();
            return;
        }

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

            // Строка песни в такт музыке.
            if (media.IsPlaying && LyricLine?.Invoke() is { } lyric)
            {
                _fontSmall.Size = 11.5f * scale;
                DrawMarquee(canvas, lyric.Text, _fontSmall, textLeft, artTop + 61f * scale, available,
                    lyric.Seconds, lyric.Length, Accent, a, scale);
            }
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

    private string EmojiFor(int index)
    {
        var parts = _settings.TabEmoji.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return index < parts.Length ? parts[index] : TabGlyphs[index];
    }

    /// <summary>Верхняя строка: вкладки слева, часы и заряд справа.</summary>
    private void DrawTabStrip(SKCanvas canvas, SKRect rect, float scale, float alpha, PowerSnapshot power)
    {
        var area = new SKRect(rect.Left, rect.Top, rect.Right, rect.Top + 40f * scale);
        Ui.Begin(canvas, area, scale, alpha, CursorPhys);

        const float tabW = 28f, tabH = 26f, left = 14f;
        for (var i = 0; i < TabGlyphs.Length; i++)
        {
            var r = new SKRect(left + i * tabW, 7f, left + (i + 1) * tabW - 2f, 7f + tabH);
            var index = i;
            if (_settings.TabStyle == Configuration.TabStyle.Emoji)
            {
                var emoji = EmojiFor(i);
                var active = i == ActiveTab;
                if (active) Ui.RoundRect(r, 8f, _pal.Surface);
                Ui.Emoji(emoji, r.MidX, r.MidY, 15f, active || Ui.IsHover(r) ? 1f : 0.62f);
                Ui.Hit(r, () => ActiveTab = index);
            }
            else
            {
                Ui.IconButton(r, TabGlyphs[i], () => ActiveTab = index, active: i == ActiveTab);
            }
        }

        // Часы справа — только если хватает места.
        var rightX = Ui.Width - 16f;
        if (Ui.Width >= 420f)
        {
            if (_settings.ShowBattery && power.HasBattery)
            {
                var label = $"{power.Percent}%";
                Ui.Text(label, rightX, 25f, 11.5f, power.IsCharging ? Accent : _pal.Muted, SKTextAlign.Right);
                rightX -= Ui.Measure(label, 11.5f) + 10f;
            }

            var header = (_settings.ShowClock, _settings.ShowDate) switch
            {
                (true, true) => ClockText() + DateTime.Now.ToString("  ddd, d MMM"),
                (true, false) => ClockText(),
                (false, true) => DateTime.Now.ToString("ddd, d MMM"),
                _ => "",
            };
            if (header.Length > 0) Ui.Text(header, rightX, 25f, 11.5f, _pal.Muted, SKTextAlign.Right);
        }

        Ui.End();
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
            // Обложка вписывается с обрезкой по центру: у видео и радио она бывает 16:9,
            // и растяжение в квадрат её уродовало.
            var side = Math.Min(artwork.Width, artwork.Height);
            var source = SKRect.Create((artwork.Width - side) / 2f, (artwork.Height - side) / 2f, side, side);
            canvas.DrawImage(artwork, source, rect, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), _image);
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

        var charging = power.IsCharging;
        var color = charging ? Accent : TextMuted;

        // Число и тонкая шкала: заряд читается по длине линии, а не по тексту.
        var label = $"{power.Percent}";
        _text.Color = color.WithAlpha(alpha);
        canvas.DrawText(label, rightX, baselineY, SKTextAlign.Right, _fontMeta, _text);

        var textW = _fontMeta.MeasureText(label);
        var gaugeW = 16f * scale;
        var gaugeH = 3f * scale;
        var gx = rightX - textW - 6f * scale - gaugeW;
        var gy = baselineY - _fontMeta.Size * 0.32f - gaugeH / 2f;

        _fill.Color = Light ? new SKColor(0, 0, 0, (byte)(alpha * 0.16f)) : new SKColor(255, 255, 255, (byte)(alpha * 0.18f));
        canvas.DrawRoundRect(new SKRect(gx, gy, gx + gaugeW, gy + gaugeH), gaugeH / 2f, gaugeH / 2f, _fill);

        var low = !charging && power.Percent <= 20;
        _fill.Color = (low ? new SKColor(255, 105, 97) : color).WithAlpha(alpha);
        canvas.DrawRoundRect(new SKRect(gx, gy, gx + gaugeW * Math.Clamp(power.Percent / 100f, 0.04f, 1f), gy + gaugeH),
            gaugeH / 2f, gaugeH / 2f, _fill);
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
        var eq = _settings.EqualizerColor != 0 ? new SKColor(_settings.EqualizerColor) : Accent;
        _fill.Color = eq.WithAlpha((byte)(alpha * 255));

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
        if (!_controlsVisible || ActiveTab != 0) return IslandButton.None;
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
        Ui.Dispose();
    }
}
