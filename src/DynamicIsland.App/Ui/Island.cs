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
internal sealed class Island
{
    public Vec2 NotchSize { get; set; } = new(190, 7);
    public Vec2 RestSize { get; set; } = new(250, 38);
    // Высота подобрана так, чтобы нижний край обложки и кнопок совпадал с
    // отступом: при 132 снизу оставалась заметная пустая полоса.
    public Vec2 ExpandedSize { get; set; } = new(450, 116);

    public float TopOffset { get; set; } = 8f;

    private readonly SecondOrder _sizeSpring;
    private readonly SecondOrder _offsetSpring;
    private readonly SecondOrder _fadeSpring;

    public IslandMode Mode { get; private set; } = IslandMode.Rest;

    public Vec2 CurrentSize { get; private set; }
    public float CurrentTop { get; private set; }

    /// <summary>0 — свёрнут, 1 — полностью раскрыт. Управляет проявлением содержимого.</summary>
    public float ExpandProgress { get; private set; }

    /// <summary>0 — скрыт полностью, 1 — виден.</summary>
    public float Visibility { get; private set; } = 1f;

    public bool IsAnimating => !_sizeSpring.IsSettled || !_offsetSpring.IsSettled || !_fadeSpring.IsSettled;

    public Island()
    {
        _sizeSpring = new SecondOrder(RestSize, 2.5f, 0.6f, 0.1f);
        _offsetSpring = new SecondOrder(new Vec2(TopOffset, 0), 3f, 0.9f, 0.1f);
        _fadeSpring = new SecondOrder(new Vec2(0, 1), 4f, 1f, 0f);
        CurrentSize = RestSize;
        CurrentTop = TopOffset;
    }

    public void SetMode(IslandMode mode) => Mode = mode;

    private Vec2 TargetSize => Mode switch
    {
        IslandMode.Expanded => ExpandedSize,
        IslandMode.Notch => NotchSize,
        IslandMode.Hidden => NotchSize,
        _ => RestSize,
    };

    public void Update(float dt)
    {
        // Наружу — мягче и с лёгким перелётом, внутрь — собраннее.
        if (Mode == IslandMode.Expanded) _sizeSpring.SetValues(2.5f, 0.6f, 0.1f);
        else _sizeSpring.SetValues(3f, 0.9f, 0.1f);

        CurrentSize = _sizeSpring.Update(dt, TargetSize);

        var targetTop = Mode switch
        {
            IslandMode.Hidden => -NotchSize.Y * 3f,
            IslandMode.Notch => 0f,
            _ => TopOffset,
        };
        CurrentTop = _offsetSpring.Update(dt, new Vec2(targetTop, 0)).X;

        // X — проявление содержимого, Y — общая видимость.
        var targets = new Vec2(
            Mode == IslandMode.Expanded ? 1f : 0f,
            Mode == IslandMode.Hidden ? 0f : 1f);
        var fade = _fadeSpring.Update(dt, targets);

        ExpandProgress = Math.Clamp(fade.X, 0f, 1f);
        Visibility = Math.Clamp(fade.Y, 0f, 1f);
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

    public float GetRadius(SKRect rect, float scale) =>
        MathF.Min(rect.Height / 2f, 30f * scale);
}
