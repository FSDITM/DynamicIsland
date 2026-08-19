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

        Log.Write($"=== ИТОГО ПО ВЫБОРУ: {cases.Length - failed} из {cases.Length} ===");

        var total = failed + RunTimeline();
        return total == 0 ? 0 : 1;
    }

    /// <summary>Позиция ползунка: расклады, снятые с живых плееров.</summary>
    private static int RunTimeline()
    {
        var sec = TimeSpan.FromSeconds(1);
        var cases = new (string Name, TimeSpan Start, TimeSpan End, TimeSpan Reported,
                         TimeSpan Age, bool Playing, TimeSpan Expected)[]
        {
            // Замер 19 августа: Яндекс Музыка через Chrome отдала позицию
            // 0.37 с с отметкой 8.8-секундной давности. Настоящая позиция ~9 с.
            ("Яндекс: отметка протухла на 9 секунд",
                TimeSpan.Zero, 97.8 * sec, 0.37 * sec, 8.8 * sec, true, 9.17 * sec),

            // На паузе позиция не двигается, сколько бы ни прошло.
            ("на паузе догонять нельзя",
                TimeSpan.Zero, 97.8 * sec, 30 * sec, 60 * sec, false, 30 * sec),

            // Свежая отметка — ничего не добавляем.
            ("отметка свежая",
                TimeSpan.Zero, 200 * sec, 42 * sec, TimeSpan.Zero, true, 42 * sec),

            // Мусорная отметка: Telegram отдал нулевую дату, разница вышла
            // 13 431 627 323 секунды. Догонять по такой нельзя.
            ("мусорная отметка из 1601 года",
                TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 13431627323 * sec, true, TimeSpan.Zero),

            // Догон не должен выносить ползунок за конец трека.
            ("догон упирается в конец",
                TimeSpan.Zero, 100 * sec, 95 * sec, 30 * sec, true, 100 * sec),

            // Поток с ненулевым началом: позиция считается от него.
            ("трек начинается не с нуля",
                60 * sec, 160 * sec, 75 * sec, TimeSpan.Zero, true, 15 * sec),

            // Отметка из будущего (часы разъехались) — не доверяем.
            ("отметка из будущего",
                TimeSpan.Zero, 100 * sec, 10 * sec, -5 * sec, true, 10 * sec),
        };

        var failed = 0;
        Log.Write("=== ПРОВЕРКА ПОЗИЦИИ ПОЛЗУНКА ===");

        foreach (var (name, start, end, reported, age, playing, expected) in cases)
        {
            var (position, duration) = TimelineMath.Compute(start, end, reported, age, playing);
            var ok = Math.Abs((position - expected).TotalSeconds) < 0.05;
            if (!ok) failed++;

            Log.Write($"  {(ok ? "ok  " : "ПЛОХО")} {name}");
            Log.Write($"        сообщено {reported.TotalSeconds:0.00} с, отметке {age.TotalSeconds:0.0} с, " +
                      $"играет={playing}, длительность {duration.TotalSeconds:0.0} с");
            Log.Write($"        ожидали {expected.TotalSeconds:0.00} с, вышло {position.TotalSeconds:0.00} с");
        }

        Log.Write($"=== ИТОГО ПО ПОЗИЦИИ: {cases.Length - failed} из {cases.Length} ===");
        return failed;
    }
}
