namespace DynamicIsland.Ui;

/// <summary>
/// Второпорядковая пружинная динамика (модель t3ssel8r) — то, что даёт островку
/// «желейное» ощущение вместо линейного лерпа.
///
/// Отличия от оригинала DynamicWin:
///  • производная входа xd считается по-настоящему (в оригинале она всегда была null,
///    поэтому член упреждения не работал);
///  • убрано округление результата до одного знака — оно давало заметные ступеньки
///    на медленных участках анимации;
///  • есть <see cref="IsSettled"/>: планировщик кадров по нему понимает, что
///    анимация закончилась и можно уходить в простой с нулевым CPU.
/// </summary>
internal sealed class SecondOrder
{
    private Vec2 _xp;
    private Vec2 _y;
    private Vec2 _yd;
    private float _k1, _k2, _k3;

    /// <summary>Порог покоя в пикселях: ниже него движение уже не видно на экране.</summary>
    private const float SettleEpsilon = 0.05f;

    public SecondOrder(Vec2 x0, float f = 2f, float z = 0.4f, float r = 0.1f)
    {
        SetValues(f, z, r);
        _xp = x0;
        _y = x0;
        _yd = Vec2.Zero;
    }

    public Vec2 Value => _y;

    /// <summary>true, когда и смещение до цели, и скорость упали ниже порога.</summary>
    public bool IsSettled { get; private set; } = true;

    public void SetValues(float f = 2f, float z = 0.4f, float r = 0.1f)
    {
        var tau = 2f * MathF.PI * f;
        _k1 = z / (MathF.PI * f);
        _k2 = 1f / (tau * tau);
        _k3 = r * z / tau;
    }

    /// <summary>Мгновенно телепортирует систему в точку — без анимации и без скорости.</summary>
    public void Snap(Vec2 x)
    {
        _xp = x;
        _y = x;
        _yd = Vec2.Zero;
        IsSettled = true;
    }

    public Vec2 Update(float dt, Vec2 x)
    {
        if (dt <= 0f) return _y;

        // Оценка скорости входа — даёт упреждение, когда цель сама движется.
        var xd = (x - _xp) / dt;
        _xp = x;

        // Защита от расхождения при большом шаге (просадка кадра, выход из сна).
        var k2Stable = MathF.Max(_k2, MathF.Max(dt * dt / 2f + dt * _k1 / 2f, dt * _k1));

        _y += _yd * dt;
        _yd += (x + xd * _k3 - _y - _yd * _k1) * (dt / k2Stable);

        IsSettled = (x - _y).Magnitude < SettleEpsilon && _yd.Magnitude < SettleEpsilon;
        return _y;
    }
}
