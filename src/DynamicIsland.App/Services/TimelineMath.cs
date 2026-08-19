namespace DynamicIsland.Services;

/// <summary>
/// Позиция и длительность трека по данным системного медиа-слоя.
///
/// Вынесено отдельно, потому что тут легко ошибиться незаметно. Плеер сообщает
/// позицию не непрерывно, а событиями: Яндекс Музыка, например, отдаёт её
/// в начале трека и при перемотке, а дальше молчит. Замер 19 августа поймал
/// позицию 0.37 секунды с отметкой девятисекундной давности — и если считать
/// эту позицию свежей, ползунок будет вечно отставать и прыгать назад при
/// каждом событии.
///
/// Правильная точка отсчёта — <c>LastUpdatedTime</c>, момент, когда плеер сам
/// сообщил позицию, а не момент, когда мы её прочитали.
/// </summary>
internal static class TimelineMath
{
    /// <summary>
    /// Отметка старше часа или из будущего — мусор. Мессенджеры отдают нулевую
    /// дату, и разница получается в столетия: замер поймал 13 431 627 323 секунды.
    /// </summary>
    public static TimeSpan SaneAge(TimeSpan age) =>
        age > TimeSpan.Zero && age < TimeSpan.FromHours(1) ? age : TimeSpan.Zero;

    public static (TimeSpan Position, TimeSpan Duration) Compute(
        TimeSpan start, TimeSpan end, TimeSpan reported, TimeSpan reportAge, bool isPlaying)
    {
        var duration = end - start;
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;

        var position = reported - start;

        // Догоняем только когда звучит: на паузе позиция не двигается.
        if (isPlaying) position += SaneAge(reportAge);

        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        if (duration > TimeSpan.Zero && position > duration) position = duration;

        return (position, duration);
    }
}
