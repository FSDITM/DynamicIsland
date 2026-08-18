using Windows.Media.Control;

namespace DynamicIsland.Services;

/// <summary>Всё, что нужно знать о сессии, чтобы выбрать между ними.</summary>
internal readonly record struct SessionFacts(
    string Id,
    GlobalSystemMediaTransportControlsSessionPlaybackStatus Status,
    bool HasTimeline,
    bool HasNext,
    bool HasPause);

/// <summary>
/// Кого из звучащих источников показывать на островке.
///
/// Вынесено в чистую функцию не ради красоты: сессию WinRT нельзя создать
/// вручную, а значит правило иначе проверялось бы только вживую — дождавшись
/// нужного стечения обстоятельств. Здесь же оно прогоняется по сценариям
/// режимом <c>--mediatest</c>.
///
/// Задача, ради которой правило появилось: Windows называет «текущей» ту
/// сессию, которая шумела последней. Дослушал голосовое в мессенджере или
/// досмотрел ролик — «текущим» остаётся он, уже остановленный, хотя рядом
/// стоит на паузе музыка. Островок застревал на клипе, и вернуть трек было
/// нечем.
/// </summary>
internal static class MediaPolicy
{
    /// <summary>Индекс выбранной сессии или -1, если выбирать не из чего.</summary>
    public static int Choose(IReadOnlyList<SessionFacts> sessions, string currentId, string lastPlayingId)
    {
        if (sessions.Count == 0) return -1;

        // 1. Что-то звучит прямо сейчас — показываем это, что бы это ни было.
        //    Пока голосовое играет, показать его правильно: его можно поставить
        //    на паузу прямо с островка.
        var playing = IndexOfBest(sessions, currentId, s => s.Status == Playing);
        if (playing >= 0) return playing;

        // 2. Наступила тишина — возвращаемся к тому, кто звучал последним.
        //    Но только если он на паузе: доигранный до конца клип возвращать
        //    незачем, к нему уже не вернутся.
        for (var i = 0; i < sessions.Count; i++)
            if (sessions[i].Id == lastPlayingId && !Finished(sessions[i]))
                return i;

        // 3. Иначе берём самый похожий на настоящий проигрыватель из живых:
        //    у трека есть шкала времени и переключение, у голосового обычно нет.
        var best = -1;
        for (var i = 0; i < sessions.Count; i++)
        {
            if (Finished(sessions[i])) continue;
            if (best < 0 || Weight(sessions[i]) > Weight(sessions[best])) best = i;
        }
        if (best >= 0) return best;

        // 4. Живых не осталось — пусть решает Windows.
        var current = IndexOfBest(sessions, currentId, _ => true);
        return current >= 0 ? current : 0;
    }

    private const GlobalSystemMediaTransportControlsSessionPlaybackStatus Playing =
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

    /// <summary>Первый подходящий, но при равенстве предпочитаем тот, что назвала Windows.</summary>
    private static int IndexOfBest(IReadOnlyList<SessionFacts> sessions, string currentId,
                                   Func<SessionFacts, bool> fits)
    {
        var first = -1;
        for (var i = 0; i < sessions.Count; i++)
        {
            if (!fits(sessions[i])) continue;
            if (sessions[i].Id == currentId) return i;
            if (first < 0) first = i;
        }
        return first;
    }

    private static bool Finished(in SessionFacts s) =>
        s.Status is GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped
                 or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;

    private static int Weight(in SessionFacts s) =>
        (s.HasTimeline ? 4 : 0) + (s.HasNext ? 2 : 0) + (s.HasPause ? 1 : 0);
}
