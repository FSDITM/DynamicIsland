using Windows.Media.Control;

namespace DynamicIsland.Services;

/// <summary>
/// Режим <c>--mediatest</c>: прогоняет правило выбора источника по сценариям.
///
/// Сессию WinRT нельзя создать вручную, поэтому вживую такое правило
/// проверяется только счастливым стечением обстоятельств: надо дослушать
/// голосовое ровно тогда, когда музыка на паузе. Здесь те же расклады
/// задаются числами и проверяются за миллисекунду.
/// </summary>
internal static class MediaPolicyTest
{
    private const GlobalSystemMediaTransportControlsSessionPlaybackStatus Playing =
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
    private const GlobalSystemMediaTransportControlsSessionPlaybackStatus Paused =
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
    private const GlobalSystemMediaTransportControlsSessionPlaybackStatus Stopped =
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped;

    /// <summary>Полноценный проигрыватель: шкала, переключение треков, пауза.</summary>
    private static SessionFacts Player(string id, GlobalSystemMediaTransportControlsSessionPlaybackStatus status) =>
        new(id, status, HasTimeline: true, HasNext: true, HasPause: true);

    /// <summary>Одиночный клип: ни шкалы, ни следующего трека. Голосовое сообщение.</summary>
    private static SessionFacts Clip(string id, GlobalSystemMediaTransportControlsSessionPlaybackStatus status) =>
        new(id, status, HasTimeline: false, HasNext: false, HasPause: false);

    public static int Run()
    {
        var cases = new (string Name, SessionFacts[] Sessions, string CurrentId, string LastPlayingId, string Expected)[]
        {
            // Тот самый случай, ради которого всё затевалось. Замер 18 августа:
            // Telegram остановлен и назван текущим, музыка в Chrome на паузе.
            ("голосовое доиграло, музыка на паузе",
                [Clip("telegram", Stopped), Player("chrome", Paused)],
                "telegram", "chrome", "chrome"),

            // То же, но приложение только запустилось и не знает, кто играл.
            ("то же, но история пуста",
                [Clip("telegram", Stopped), Player("chrome", Paused)],
                "telegram", "", "chrome"),

            // Пока голосовое звучит, показывать надо его: с островка его можно
            // поставить на паузу. Отбирать управление у звучащего неправильно.
            ("голосовое звучит",
                [Clip("telegram", Playing), Player("chrome", Paused)],
                "telegram", "chrome", "telegram"),

            // Видео на паузе, музыка на паузе, музыка играла последней.
            ("оба на паузе, музыка играла последней",
                [Clip("telegram", Paused), Player("chrome", Paused)],
                "telegram", "chrome", "chrome"),

            // Оба на паузе, последним звучал клип — возвращаемся к нему.
            ("оба на паузе, клип звучал последним",
                [Clip("telegram", Paused), Player("chrome", Paused)],
                "chrome", "telegram", "telegram"),

            // Музыка играет, клип тоже: предпочитаем тот, что назвала Windows.
            ("играют оба",
                [Clip("telegram", Playing), Player("chrome", Playing)],
                "chrome", "telegram", "chrome"),

            // Ничего живого не осталось — берём то, что назвала Windows.
            ("всё остановлено",
                [Clip("telegram", Stopped), Player("chrome", Stopped)],
                "chrome", "telegram", "chrome"),

            // Единственный источник показывается всегда, даже если это клип.
            ("только голосовое",
                [Clip("telegram", Paused)],
                "telegram", "", "telegram"),

            // Музыка играет на фоне, а текущим Windows считает молчащий клип.
            ("музыка играет, текущим назван молчащий клип",
                [Clip("telegram", Paused), Player("chrome", Playing)],
                "telegram", "", "chrome"),

            // Три источника: браузер с музыкой должен победить два клипа.
            ("два клипа и проигрыватель",
                [Clip("telegram", Stopped), Clip("discord", Stopped), Player("chrome", Paused)],
                "discord", "", "chrome"),
        };

        var failed = 0;
        Log.Write("=== ПРОВЕРКА ВЫБОРА ИСТОЧНИКА ===");

        foreach (var (name, sessions, currentId, lastPlayingId, expected) in cases)
        {
            var index = MediaPolicy.Choose(sessions, currentId, lastPlayingId);
            var actual = index >= 0 ? sessions[index].Id : "—";
            var ok = actual == expected;
            if (!ok) failed++;

            Log.Write($"  {(ok ? "ok  " : "ПЛОХО")} {name}");
            Log.Write($"        сессии: {string.Join(", ", sessions.Select(s => $"{s.Id}/{s.Status}"))}");
            Log.Write($"        Windows считает текущим «{currentId}», последним звучал «{lastPlayingId}»");
            Log.Write($"        ожидали «{expected}», выбрано «{actual}»");
        }

        Log.Write($"=== ИТОГО: {cases.Length - failed} из {cases.Length} ===");
        return failed == 0 ? 0 : 1;
    }
}
