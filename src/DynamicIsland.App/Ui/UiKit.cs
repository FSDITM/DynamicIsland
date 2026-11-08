using SkiaSharp;

namespace DynamicIsland.Ui;

/// <summary>Цвета текущей темы островка.</summary>
internal readonly record struct Palette(SKColor Primary, SKColor Muted, SKColor Surface, SKColor Hover, SKColor Line)
{
    public static readonly Palette Dark = new(
        new SKColor(240, 237, 230), new SKColor(142, 138, 130),
        new SKColor(255, 250, 240, 14), new SKColor(255, 250, 240, 30), new SKColor(255, 250, 240, 34));

    public static readonly Palette Light = new(
        new SKColor(30, 28, 24), new SKColor(112, 108, 100),
        new SKColor(40, 30, 10, 12), new SKColor(40, 30, 10, 26), new SKColor(40, 30, 10, 36));
}

/// <summary>Области экрана, на которые можно нажать. Строится заново каждый кадр.</summary>
internal sealed class HitMap
{
    private readonly List<(SKRect Rect, Action? Left, Action? Right, Action? Press)> _items = [];

    public void Clear() => _items.Clear();

    public void Add(SKRect physicalRect, Action? left, Action? right = null, Action? press = null) =>
        _items.Add((physicalRect, left, right, press));

    /// <summary>Кнопка мыши нажата (до отпускания): нужно для начала перетаскивания.</summary>
    public bool Press(float x, float y)
    {
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            var it = _items[i];
            if (!it.Rect.Contains(x, y)) continue;
            if (it.Press is null) continue;
            it.Press();
            return true;
        }
        return false;
    }

    /// <summary>Нажатие: выполняет действие верхней области под точкой. true — область нашлась.</summary>
    public bool Click(float x, float y, bool right)
    {
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            var it = _items[i];
            if (!it.Rect.Contains(x, y)) continue;

            var action = right ? it.Right : it.Left;
            if (action is null) { if (right) continue; return true; }

            action();
            return true;
        }
        return false;
    }

    public bool HasAt(float x, float y) => _items.Any(i => i.Rect.Contains(x, y));
}

/// <summary>
/// Маленький набор для рисования интерфейса вкладок. Вкладка работает в
/// логических пикселях: <see cref="Begin"/> переносит начало координат в левый
/// верхний угол её области и включает масштаб, а нажатия переводятся в
/// физические координаты автоматически.
/// </summary>
internal sealed class UiKit : IDisposable
{
    public static readonly string[] IconFonts = ["Segoe Fluent Icons", "Segoe MDL2 Assets"];

    private readonly SKPaint _fill = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _stroke = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _text = new() { IsAntialias = true };
    private readonly SKFont _regular;
    private readonly SKFont _bold;
    private readonly SKFont _icons;

    private SKCanvas? _canvas;
    private int _saved;

    public HitMap Hits { get; } = new();
    public Palette Pal { get; set; } = Palette.Dark;
    public SKColor Accent { get; set; } = new(0xF0, 0xED, 0xE6);

    /// <summary>Свой цвет значков; null — по умолчанию.</summary>
    public SKColor? IconTint { get; set; }

    private SKFont? _emoji;
    private SKFont EmojiFont => _emoji ??= new SKFont(
        SKTypeface.FromFamilyName("Segoe UI Emoji") ?? SKTypeface.Default, 14) { Subpixel = true };

    /// <summary>Эмодзи по центру точки. Цветные глифы системного шрифта рисуются как есть.</summary>
    public void Emoji(string text, float centerX, float centerY, float size, float opacity = 1f)
    {
        if (_canvas is null || string.IsNullOrEmpty(text)) return;
        var font = EmojiFont;
        font.Size = size;
        _text.Color = SKColors.White.WithAlpha((byte)(Alpha * opacity));
        _canvas.DrawText(text, centerX, centerY + size * 0.36f, SKTextAlign.Center, font, _text);
    }

    public float Scale { get; private set; } = 1f;
    public byte Alpha { get; private set; } = 255;

    /// <summary>Размер области вкладки в логических пикселях.</summary>
    public float Width { get; private set; }
    public float Height { get; private set; }

    /// <summary>Курсор в координатах области вкладки (логические пиксели).</summary>
    public SKPoint Cursor { get; private set; }

    public bool CursorKnown { get; private set; }

    /// <summary>Левая кнопка мыши сейчас зажата (для ползунков). Ставит вызывающий.</summary>
    public bool MouseDown { get; set; }

    public UiKit(string fontFamily)
    {
        var regular = FontLibrary.FromFamilyOrDefault(fontFamily, bold: false);
        var bold = FontLibrary.FromFamilyOrDefault(fontFamily, bold: true);
        _regular = new SKFont(regular, 12) { Subpixel = true, Edging = SKFontEdging.Antialias };
        _bold = new SKFont(bold, 12) { Subpixel = true, Edging = SKFontEdging.Antialias };

        SKTypeface? icons = null;
        foreach (var name in IconFonts)
        {
            icons = SKTypeface.FromFamilyName(name);
            if (icons is not null && icons.FamilyName.Equals(name, StringComparison.OrdinalIgnoreCase)) break;
            icons = null;
        }
        _icons = new SKFont(icons ?? SKTypeface.Default, 14) { Subpixel = true };
    }

    /// <summary>Начало кадра: убираем прежние области нажатия.</summary>
    public void NewFrame() => Hits.Clear();

    public void Begin(SKCanvas canvas, SKRect physicalArea, float scale, float alpha, SKPoint cursorPhysical)
    {
        _canvas = canvas;
        Scale = scale;
        Alpha = (byte)Math.Clamp(alpha * 255f, 0f, 255f);
        Width = physicalArea.Width / scale;
        Height = physicalArea.Height / scale;
        Cursor = new SKPoint((cursorPhysical.X - physicalArea.Left) / scale, (cursorPhysical.Y - physicalArea.Top) / scale);
        CursorKnown = physicalArea.Contains(cursorPhysical);

        _saved = canvas.Save();
        canvas.Translate(physicalArea.Left, physicalArea.Top);
        canvas.Scale(scale);
        canvas.ClipRect(new SKRect(0, 0, Width, Height));
    }

    public void End()
    {
        _canvas?.RestoreToCount(_saved);
        _canvas = null;
    }

    private SKColor WithA(SKColor c) => c.WithAlpha((byte)(c.Alpha * Alpha / 255));

    public bool IsHover(SKRect local) => CursorKnown && local.Contains(Cursor);

    /// <summary>Регистрирует область нажатия (локальные логические координаты).</summary>
    public void Hit(SKRect local, Action? left, Action? right = null, Action? press = null)
    {
        if (_canvas is null) return;
        Hits.Add(_canvas.TotalMatrix.MapRect(local), left, right, press);
    }

    // ---------- Текст с запасными шрифтами ----------
    //
    // У Rubik нет эмодзи, стилизованных букв и многих символов из имён в мессенджерах:
    // без запасных шрифтов вместо них рисовались пустые квадраты. Каждый символ, которого нет
    // в основном шрифте, ищется у системы, а соседние символы одного шрифта рисуются одним куском.

    private readonly Dictionary<int, SKTypeface?> _fallbacks = [];
    private readonly Dictionary<SKTypeface, SKFont> _fallbackFonts = [];

    private SKTypeface? FallbackFor(int codepoint)
    {
        if (_fallbacks.TryGetValue(codepoint, out var cached)) return cached;
        SKTypeface? face = null;
        try { face = SKFontManager.Default.MatchCharacter(codepoint); }
        catch { /* нет подходящего шрифта */ }
        if (_fallbacks.Count > 2000) _fallbacks.Clear();
        _fallbacks[codepoint] = face;
        return face;
    }

    private SKFont FontFor(SKTypeface face, SKFont primary, float size)
    {
        if (ReferenceEquals(face, primary.Typeface)) return primary;
        if (!_fallbackFonts.TryGetValue(face, out var font))
        {
            font = new SKFont(face, size) { Subpixel = true };
            _fallbackFonts[face] = font;
        }
        font.Size = size;
        return font;
    }

    /// <summary>Строка, разбитая на куски по шрифтам. Невидимые управляющие символы отбрасываются.</summary>
    private List<(string Text, SKFont Font)> Runs(string text, SKFont primary, float size)
    {
        var runs = new List<(string, SKFont)>();
        var current = new System.Text.StringBuilder();
        SKFont? currentFont = null;

        foreach (var rune in text.EnumerateRunes())
        {
            var cat = System.Text.Rune.GetUnicodeCategory(rune);
            if (cat is System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.Control &&
                rune.Value != '\t') continue;

            var face = primary.Typeface;
            if (face is null || !face.ContainsGlyph(rune.Value))
                face = (rune.Value > 32 ? FallbackFor(rune.Value) : null) ?? primary.Typeface;

            var font = face is null ? primary : FontFor(face, primary, size);
            if (currentFont is not null && !ReferenceEquals(font, currentFont))
            {
                runs.Add((current.ToString(), currentFont));
                current.Clear();
            }
            currentFont = font;
            current.Append(rune.ToString());
        }

        if (currentFont is not null && current.Length > 0) runs.Add((current.ToString(), currentFont));
        return runs;
    }

    public float Measure(string text, float size, bool bold = false)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var font = bold ? _bold : _regular;
        font.Size = size;
        float total = 0;
        foreach (var (chunk, f) in Runs(text, font, size)) total += f.MeasureText(chunk);
        return total;
    }

    public string Fit(string text, float size, float maxWidth, bool bold = false)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (Measure(text, size, bold) <= maxWidth) return text;

        var limit = Math.Max(0, maxWidth - Measure("…", size, bold));
        var sb = new System.Text.StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            sb.Append(rune.ToString());
            if (Measure(sb.ToString(), size, bold) > limit)
            {
                sb.Length -= rune.Utf16SequenceLength;
                break;
            }
        }
        var cut = sb.ToString().TrimEnd();
        return cut.Length == 0 ? "…" : cut + "…";
    }

    public void Text(string text, float x, float baseline, float size, SKColor color,
                     SKTextAlign align = SKTextAlign.Left, bool bold = false)
    {
        if (_canvas is null || string.IsNullOrEmpty(text)) return;
        var font = bold ? _bold : _regular;
        font.Size = size;
        _text.Color = WithA(color);

        var runs = Runs(text, font, size);
        if (runs.Count == 1)
        {
            _canvas.DrawText(runs[0].Text, x, baseline, align, runs[0].Font, _text);
            return;
        }

        var width = 0f;
        foreach (var (chunk, f) in runs) width += f.MeasureText(chunk);
        var pen = align switch
        {
            SKTextAlign.Center => x - width / 2f,
            SKTextAlign.Right => x - width,
            _ => x,
        };
        foreach (var (chunk, f) in runs)
        {
            _canvas.DrawText(chunk, pen, baseline, SKTextAlign.Left, f, _text);
            pen += f.MeasureText(chunk);
        }
    }

    /// <summary>Несколько строк с переносом по словам. Возвращает занятую высоту.</summary>
    public float Paragraph(string text, float x, float top, float width, float size, SKColor color,
                           float lineHeight, int maxLines = 99, bool ellipsis = false)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var font = _regular;
        font.Size = size;

        var y = top + size;
        var lines = 0;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var words = raw.Split(' ');
            var line = "";
            foreach (var w in words)
            {
                var test = line.Length == 0 ? w : line + " " + w;
                if (Measure(test, size) > width && line.Length > 0)
                {
                    // Последняя разрешённая строка: остаток не помещается — обрезаем многоточием.
                    if (ellipsis && lines + 1 >= maxLines)
                    {
                        Text(Fit(line + " " + w + " " + string.Join(' ', words.SkipWhile(x2 => x2 != w).Skip(1)), size, width), x, y, size, color);
                        return y + lineHeight - top;
                    }
                    Text(line, x, y, size, color);
                    y += lineHeight;
                    if (++lines >= maxLines) return y - top;
                    line = w;
                }
                else line = test;
            }
            Text(line, x, y, size, color);
            y += lineHeight;
            if (++lines >= maxLines) break;
        }
        return y - top;
    }

    public void Glyph(string glyph, float centerX, float centerY, float size, SKColor color)
    {
        if (_canvas is null) return;
        _icons.Size = size;
        _text.Color = WithA(color);
        // Базовая линия: глифы шрифта значков центрируются по высоте em-квадрата.
        _canvas.DrawText(glyph, centerX, centerY + size * 0.36f, SKTextAlign.Center, _icons, _text);
    }

    public void RoundRect(SKRect r, float radius, SKColor color)
    {
        if (_canvas is null) return;
        _fill.Color = WithA(color);
        _canvas.DrawRoundRect(r, radius, radius, _fill);
    }

    public void Outline(SKRect r, float radius, SKColor color, float width = 1f)
    {
        if (_canvas is null) return;
        _stroke.Color = WithA(color);
        _stroke.StrokeWidth = width;
        _canvas.DrawRoundRect(r, radius, radius, _stroke);
    }

    public void Circle(float cx, float cy, float radius, SKColor color)
    {
        if (_canvas is null) return;
        _fill.Color = WithA(color);
        _canvas.DrawCircle(cx, cy, radius, _fill);
    }

    public void Line(float x1, float y1, float x2, float y2, SKColor color, float width = 1f)
    {
        if (_canvas is null) return;
        _stroke.Color = WithA(color);
        _stroke.StrokeWidth = width;
        _canvas.DrawLine(x1, y1, x2, y2, _stroke);
    }

    public void Image(SKImage image, SKRect dest, float radius)
    {
        if (_canvas is null) return;
        var saved = _canvas.Save();
        using (var clip = new SKRoundRect(dest, radius)) _canvas.ClipRoundRect(clip, antialias: true);
        _fill.Color = SKColors.White.WithAlpha(Alpha);
        _canvas.DrawImage(image, dest, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), _fill);
        _canvas.RestoreToCount(saved);
    }

    /// <summary>
    /// Кнопка: контур в одну линию и малое скругление. Главная — залита акцентом,
    /// опасная — красный текст. Никаких таблеток с заливкой под каждым словом.
    /// </summary>
    public void Button(SKRect r, string label, Action onClick, bool primary = false, bool danger = false,
                       string? glyph = null)
    {
        var hover = IsHover(r);
        SKColor fg;
        if (primary)
        {
            RoundRect(r, 6f, hover ? Accent.WithAlpha(225) : Accent);
            // Текст контрастен заливке: на светлой теме акцент тёмный, и тёмный текст пропадал.
            var luma = 0.299f * Accent.Red + 0.587f * Accent.Green + 0.114f * Accent.Blue;
            fg = luma > 140f ? new SKColor(14, 13, 11) : new SKColor(244, 241, 234);
        }
        else
        {
            fg = danger ? new SKColor(255, 105, 97) : Pal.Primary;
            if (hover) RoundRect(r, 6f, danger ? new SKColor(255, 69, 58, 34) : Pal.Surface);
            Outline(r, 6f, danger ? new SKColor(255, 105, 97, 90) : Pal.Line, 1f);
        }

        if (glyph is not null)
        {
            var labelW = Measure(label, 11.5f, true);
            var total = 14f + 6f + labelW;
            var start = r.MidX - total / 2f;
            Glyph(glyph, start + 7f, r.MidY, 12f, fg);
            Text(label, start + 20f, r.MidY + 4f, 11.5f, fg, SKTextAlign.Left, true);
        }
        else
        {
            Text(label, r.MidX, r.MidY + 4f, 11.5f, fg, SKTextAlign.Center, true);
        }

        Hit(r, onClick);
    }

    /// <summary>Значок без подложки: активный подчёркнут линией, неактивный приглушён.</summary>
    public void IconButton(SKRect r, string glyph, Action onClick, bool active = false, Action? right = null,
                           SKColor? color = null)
    {
        var hover = IsHover(r);
        var c = color ?? (IconTint is { } tint
            ? (active || hover ? tint : tint.WithAlpha(150))
            : (active ? Pal.Primary : (hover ? Pal.Primary : Pal.Muted)));
        Glyph(glyph, r.MidX, r.MidY, Math.Min(r.Width, r.Height) * 0.5f, c);
        if (active) RoundRect(new SKRect(r.MidX - 7f, r.Bottom - 1.5f, r.MidX + 7f, r.Bottom + 0.5f), 1f, Accent);
        Hit(r, onClick, right);
    }

    /// <summary>Строка списка: без карточки, только подсветка под курсором и линия снизу.</summary>
    public void Row(SKRect r, bool hover, bool line = true)
    {
        if (hover) RoundRect(r, 4f, Pal.Surface);
        if (line) Line(r.Left, r.Bottom + 0.5f, r.Right, r.Bottom + 0.5f, Pal.Line, 1f);
    }

    public void Toggle(SKRect r, bool on, Action onClick)
    {
        RoundRect(r, r.Height / 2f, on ? Accent : Pal.Hover);
        var knob = r.Height - 4f;
        var x = on ? r.Right - knob - 2f : r.Left + 2f;
        Circle(x + knob / 2f, r.MidY, knob / 2f, SKColors.White);
        Hit(r, onClick);
    }

    /// <summary>Полоса прокрутки справа от списка.</summary>
    public void Scrollbar(SKRect track, float scroll, float content, float view)
    {
        if (content <= view + 1f) return;
        var ratio = view / content;
        var h = Math.Max(18f, track.Height * ratio);
        var top = track.Top + (track.Height - h) * (scroll / (content - view));
        RoundRect(new SKRect(track.Left, top, track.Right, top + h), track.Width / 2f, Pal.Hover);
    }

    public void Dispose()
    {
        _fill.Dispose();
        _stroke.Dispose();
        _text.Dispose();
        _regular.Dispose();
        _bold.Dispose();
        _icons.Dispose();
        _emoji?.Dispose();
        foreach (var f in _fallbackFonts.Values) f.Dispose();
    }
}

/// <summary>Одна вкладка панели.</summary>
internal interface ITabView
{
    /// <summary>Рисует вкладку в области ui.Width × ui.Height (логические пиксели).</summary>
    void Draw(UiKit ui);

    /// <summary>Колесо мыши над вкладкой; положительное значение — вверх.</summary>
    void Wheel(int delta) { }

    /// <summary>Вкладку открыли — обновить данные.</summary>
    void Activated() { }
}

/// <summary>Коды значков Segoe MDL2 Assets / Segoe Fluent Icons.</summary>
internal static class G
{
    public const string Home = "";
    public const string Clipboard = "";
    public const string Copy = "";
    public const string Delete = "";
    public const string Pin = "";
    public const string Pinned = "";
    public const string Folder = "";
    public const string File = "";
    public const string Text = "";
    public const string Edit = "";
    public const string Notes = "";
    public const string Settings = "";
    public const string Cloud = "";
    public const string Sun = "";
    public const string Camera = "";
    public const string Video = "";
    public const string Mic = "";
    public const string Wifi = "";
    public const string Bluetooth = "";
    public const string Lock = "";
    public const string Power = "";
    public const string Moon = "";
    public const string Refresh = "";
    public const string Close = "";
    public const string Add = "";
    public const string Check = "";
    public const string Clock = "";
    public const string Alarm = "";
    public const string Stopwatch = "";
    public const string Crop = "";
    public const string Palette = "";
    public const string Globe = "";
    public const string Search = "";
    public const string Link = "";
    public const string Play = "";
    public const string Pause = "";
    public const string Stop = "";
    public const string Volume = "";
    public const string Mute = "";
    public const string Download = "";
    public const string OpenFile = "";
    public const string Save = "";
    public const string Up = "";
    public const string Down = "";
    public const string Chevron = "";
    public const string Sleep = "";
    public const string Restart = "";
    public const string Keyboard = "";
    public const string Shield = "";
    public const string Calendar = "";
    public const string Image = "";
    public const string Share = "";
    public const string Zoom = "";
    public const string Bold = "";
    public const string Italic = "";
}
