namespace DynamicIsland.Ui;

/// <summary>
/// Двумерный вектор.
///
/// В оригинале это был class — каждый расчёт позиции создавал объект в куче,
/// а позиция считается по несколько раз на объект за кадр. Здесь readonly struct:
/// ноль аллокаций, ноль работы для GC.
/// </summary>
public readonly record struct Vec2(float X, float Y)
{
    public static readonly Vec2 Zero = new(0, 0);
    public static readonly Vec2 One = new(1, 1);

    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator *(Vec2 a, Vec2 b) => new(a.X * b.X, a.Y * b.Y);
    public static Vec2 operator *(Vec2 a, float s) => new(a.X * s, a.Y * s);
    public static Vec2 operator *(float s, Vec2 a) => new(a.X * s, a.Y * s);
    public static Vec2 operator /(Vec2 a, float s) => new(a.X / s, a.Y / s);

    public float Magnitude => MathF.Sqrt(X * X + Y * Y);

    public static Vec2 Lerp(Vec2 a, Vec2 b, float t) => new(
        a.X + (b.X - a.X) * t,
        a.Y + (b.Y - a.Y) * t);

    public override string ToString() => $"({X:0.##}, {Y:0.##})";
}
