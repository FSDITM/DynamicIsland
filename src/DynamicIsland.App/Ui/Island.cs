using SkiaSharp;

namespace DynamicIsland.Ui;

/// <summary>
/// Островок: форма, пружинная анимация размера и отрисовка.
///
/// Все SKPaint / SKImageFilter создаются один раз и переиспользуются. В оригинале
/// они создавались на каждый кадр для каждого объекта и не освобождались — это давало
/// поток нативных аллокаций и работу финализатору, то есть микрофризы.
/// </summary>
public sealed class Island : IDisposable
{
    // Логические размеры (независимые от DPI).
    public Vec2 RestSize { get; set; } = new(260, 42);
    public Vec2 HoverSize { get; set; } = new(330, 68);
    public Vec2 NotchSize { get; set; } = new(180, 8);

    public float TopOffset { get; set; } = 8f;

    private readonly SecondOrder _sizeSpring;
    private readonly SecondOrder _offsetSpring;

    private readonly SKPaint _fillPaint;
    private readonly SKPaint _borderPaint;
    private readonly SKPaint _shadowPaint;

    // Фильтр тени — самый дорогой объект в кадре. Пересобираем только когда
    // радиус реально изменился на заметную величину, а не каждый кадр.
    private SKImageFilter? _shadowFilter;
    private float _shadowFilterRadius = -1f;

    public bool IsHovered { get; set; }

    /// <summary>Свёрнут в тонкую полоску, потому что под ним развёрнутое окно.</summary>
    public bool Collapsed { get; set; }

    public Vec2 CurrentSize { get; private set; }
    public float CurrentTop { get; private set; }

    /// <summary>Пока false — планировщик кадров может уснуть.</summary>
    public bool IsAnimating => !_sizeSpring.IsSettled || !_offsetSpring.IsSettled;

    public Island()
    {
        _sizeSpring = new SecondOrder(RestSize, 2.5f, 0.6f, 0.1f);
        _offsetSpring = new SecondOrder(new Vec2(TopOffset, 0), 3f, 0.9f, 0.1f);
        CurrentSize = RestSize;
        CurrentTop = TopOffset;

        _fillPaint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true, Color = new SKColor(12, 12, 14, 255) };
        _borderPaint = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeWidth = 1.25f, Color = new SKColor(255, 255, 255, 26) };
        _shadowPaint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true, Color = new SKColor(0, 0, 0, 255) };
    }

    private Vec2 TargetSize => Collapsed && !IsHovered ? NotchSize
                             : IsHovered ? HoverSize
                             : RestSize;

    public void Update(float dt)
    {
        // Разворачивание и сворачивание ощущаются по-разному: наружу — мягче и с
        // лёгким перелётом, внутрь — собраннее. Ровно как в оригинале.
        if (IsHovered) _sizeSpring.SetValues(2.5f, 0.6f, 0.1f);
        else _sizeSpring.SetValues(3f, 0.9f, 0.1f);

        CurrentSize = _sizeSpring.Update(dt, TargetSize);

        var targetTop = Collapsed && !IsHovered ? 0f : TopOffset;
        CurrentTop = _offsetSpring.Update(dt, new Vec2(targetTop, 0)).X;
    }

    /// <summary>Прямоугольник островка в физических пикселях внутри сцены.</summary>
    public SKRect GetRect(float stageWidth, float scale)
    {
        var w = CurrentSize.X * scale;
        var h = CurrentSize.Y * scale;
        var x = (stageWidth - w) / 2f;
        var y = CurrentTop * scale;
        return new SKRect(x, y, x + w, y + h);
    }

    public void Draw(SKCanvas canvas, float stageWidth, float scale)
    {
        var rect = GetRect(stageWidth, scale);
        var radius = MathF.Min(rect.Height / 2f, 30f * scale);
        var round = new SKRoundRect(rect, radius);

        // Тень: сильнее и шире, когда островок раскрыт.
        var shadowRadius = (IsHovered ? 26f : 9f) * scale;
        var shadowAlpha = (byte)(IsHovered ? 150 : 90);
        EnsureShadowFilter(shadowRadius);

        _shadowPaint.Color = new SKColor(0, 0, 0, shadowAlpha);
        _shadowPaint.ImageFilter = _shadowFilter;
        canvas.DrawRoundRect(round, _shadowPaint);

        canvas.DrawRoundRect(round, _fillPaint);
        canvas.DrawRoundRect(round, _borderPaint);

        round.Dispose();
    }

    private void EnsureShadowFilter(float radius)
    {
        // Квантуем радиус: перестраивать нативный фильтр ради 0.1 px бессмысленно.
        var quantized = MathF.Round(radius * 2f) / 2f;
        if (_shadowFilter is not null && MathF.Abs(quantized - _shadowFilterRadius) < 0.01f) return;

        _shadowFilter?.Dispose();
        _shadowFilter = SKImageFilter.CreateBlur(quantized, quantized);
        _shadowFilterRadius = quantized;
    }

    public void Dispose()
    {
        _fillPaint.Dispose();
        _borderPaint.Dispose();
        _shadowPaint.Dispose();
        _shadowFilter?.Dispose();
    }
}
