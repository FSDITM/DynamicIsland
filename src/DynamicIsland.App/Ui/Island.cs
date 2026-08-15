using DynamicIsland.Configuration;
using SkiaSharp;

namespace DynamicIsland.Ui;

internal enum IslandMode
{
    /// <summary>Полный экран под нами — уходим с глаз.</summary>
    Hidden,

    /// <summary>Под островком развёрнутое окно — тонкая полоска у края.</summary>
    Notch,

    /// <summary>Обычное состояние: часы и заряд.</summary>
    Rest,

    /// <summary>Курсор на островке — раскрыт с плеером.</summary>
    Expanded,
}

internal enum IslandButton { None, PlayPause, Next, Previous }

/// <summary>
/// Геометрия островка и пружинная анимация перехода между состояниями.
/// Отрисовкой содержимого занимается <see cref="IslandContent"/>.
/// </summary>
internal sealed class Island(Settings settings)
{
    private readonly SecondOrder _sizeSpring = new(new Vec2(settings.RestWidth, settings.RestHeight), 2.5f, 0.6f, 0.1f);
    private readonly SecondOrder _offsetSpring = new(new Vec2(settings.TopOffset, 0), 3f, 0.9f, 0.1f);
    private readonly SecondOrder _fadeSpring = new(new Vec2(0, 1), 4f, 1f, 0f);

    public IslandMode Mode { get; private set; } = IslandMode.Rest;

    public Vec2 CurrentSize { get; private set; } = new(settings.RestWidth, settings.RestHeight);

    /// <summary>Отступ от края экрана, к которому привязан островок.</summary>
    public float CurrentEdgeOffset { get; private set; } = settings.TopOffset;

    /// <summary>0 — свёрнут, 1 — полностью раскрыт. Управляет проявлением содержимого.</summary>
    public float ExpandProgress { get; private set; }

    /// <summary>0 — скрыт полностью, 1 — виден.</summary>
    public float Visibility { get; private set; } = 1f;

    public bool IsAnimating => !_sizeSpring.IsSettled || !_offsetSpring.IsSettled || !_fadeSpring.IsSettled;

    public Vec2 NotchSize => new(settings.NotchWidth, settings.NotchHeight);
    public Vec2 RestSize => new(settings.RestWidth, settings.RestHeight);
    public Vec2 ExpandedSize => new(settings.ExpandedWidth, settings.ExpandedHeight);

    public void SetMode(IslandMode mode) => Mode = mode;

    private Vec2 TargetSize => Mode switch
    {
        IslandMode.Expanded => ExpandedSize,
        IslandMode.Notch or IslandMode.Hidden => NotchSize,
        _ => RestSize,
    };

    public void Update(float dt)
    {
        var speed = settings.AnimationSpeed;

        // Наружу — мягче и с лёгким перелётом, внутрь — собраннее.
        if (Mode == IslandMode.Expanded) _sizeSpring.SetValues(2.5f * speed, 0.6f, 0.1f);
        else _sizeSpring.SetValues(3f * speed, 0.9f, 0.1f);

        _offsetSpring.SetValues(3f * speed, 0.9f, 0.1f);
        _fadeSpring.SetValues(4f * speed, 1f, 0f);

        CurrentSize = _sizeSpring.Update(dt, TargetSize);

        var targetOffset = Mode switch
        {
            IslandMode.Hidden => -settings.NotchHeight * 3f,
            IslandMode.Notch => 0f,
            _ => settings.TopOffset,
        };
        CurrentEdgeOffset = _offsetSpring.Update(dt, new Vec2(targetOffset, 0)).X;

        // X — проявление содержимого, Y — общая видимость.
        var fade = _fadeSpring.Update(dt, new Vec2(
            Mode == IslandMode.Expanded ? 1f : 0f,
            Mode == IslandMode.Hidden ? 0f : 1f));

        ExpandProgress = Math.Clamp(fade.X, 0f, 1f);
        Visibility = Math.Clamp(fade.Y, 0f, 1f);
    }

    /// <summary>
    /// Прямоугольник островка в физических пикселях внутри сцены.
    /// При привязке к низу экрана отступ отсчитывается от нижнего края.
    /// </summary>
    public SKRect GetRect(float stageWidth, float stageHeight, float scale)
    {
        var w = CurrentSize.X * scale;
        var h = CurrentSize.Y * scale;
        var x = (stageWidth - w) / 2f + settings.HorizontalOffset * scale;

        var y = settings.Anchor == ScreenAnchor.Bottom
            ? stageHeight - CurrentEdgeOffset * scale - h
            : CurrentEdgeOffset * scale;

        return new SKRect(x, y, x + w, y + h);
    }

    public float GetRadius(SKRect rect, float scale) =>
        MathF.Min(rect.Height / 2f, settings.CornerRadius * scale);
}
