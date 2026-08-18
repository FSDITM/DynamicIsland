using System.Reflection;
using Windows.Media.Control;

namespace DynamicIsland.Services;

/// <summary>
/// Режим <c>--mediainfo</c>: выкладывает в лог всё, что системный медиа-слой
/// знает о текущем воспроизведении.
///
/// Нужен, чтобы вопрос «а можно ли отсюда поставить лайк» решался списком
/// доступных методов, а не рассуждением по памяти. Заодно показывает, каким
/// идентификатором представляется каждый плеер, — по нему приложение сможет
/// понять, какому сервису предлагать сердечко.
/// </summary>
internal static class MediaProbe
{
    public static async Task<int> RunAsync()
    {
        Log.Write("=== РАЗБОР МЕДИА-СЕССИЙ ===");

        // 1. Что вообще умеет тип сессии. Список методов и есть ответ про лайк.
        Log.Write("--- методы GlobalSystemMediaTransportControlsSession ---");
        foreach (var m in typeof(GlobalSystemMediaTransportControlsSession)
                     .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                     .Where(m => !m.IsSpecialName)
                     .OrderBy(m => m.Name))
            Log.Write("    " + m.Name);

        Log.Write("--- флаги GlobalSystemMediaTransportControlsSessionPlaybackControls ---");
        foreach (var p in typeof(GlobalSystemMediaTransportControlsSessionPlaybackControls)
                     .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .OrderBy(p => p.Name))
            Log.Write("    " + p.Name);

        Log.Write("--- поля GlobalSystemMediaTransportControlsSessionMediaProperties ---");
        foreach (var p in typeof(GlobalSystemMediaTransportControlsSessionMediaProperties)
                     .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .OrderBy(p => p.Name))
            Log.Write("    " + p.Name);

        // 2. Что происходит прямо сейчас.
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        var current = manager.GetCurrentSession();
        var sessions = manager.GetSessions();

        Log.Write($"--- сессий сейчас: {sessions.Count} ---");
        foreach (var session in sessions)
        {
            var isCurrent = current is not null &&
                            session.SourceAppUserModelId == current.SourceAppUserModelId;
            Log.Write($"  [{(isCurrent ? "текущая" : "фоновая")}] {session.SourceAppUserModelId}");

            var playback = session.GetPlaybackInfo();
            Log.Write($"      состояние: {playback.PlaybackStatus}");

            var controls = playback.Controls;
            var enabled = typeof(GlobalSystemMediaTransportControlsSessionPlaybackControls)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType == typeof(bool) && (bool)(p.GetValue(controls) ?? false))
                .Select(p => p.Name);
            Log.Write("      разрешено: " + string.Join(", ", enabled));

            try
            {
                var props = await session.TryGetMediaPropertiesAsync();
                Log.Write($"      трек: «{props.Title}» — {props.Artist}");
                Log.Write($"      альбом: «{props.AlbumTitle}» / {props.AlbumArtist}, " +
                          $"номер {props.TrackNumber} из {props.AlbumTrackCount}");
                Log.Write($"      подзаголовок: «{props.Subtitle}», жанры: " +
                          $"{string.Join("|", props.Genres)}, тип: {props.PlaybackType}");
                Log.Write($"      обложка: {(props.Thumbnail is null ? "нет" : "есть")}");
            }
            catch (Exception ex)
            {
                Log.Write("      свойства не прочитались: " + ex.Message);
            }
        }

        // 3. И главное: что из всего этого выберет само приложение.
        Log.Write("--- выбор приложения ---");
        await using var service = new MediaService();
        await service.StartAsync();
        await Task.Delay(1200);

        var snap = service.Snapshot;
        Log.Write(snap.HasSession
            ? $"    ПОКАЖЕТ: «{snap.Title}» — {snap.Artist}   источник {snap.AppId}, " +
              $"играет={snap.IsPlaying}, перемотка={snap.CanSeek}"
            : "    ПОКАЖЕТ: ничего");

        Log.Write("=== КОНЕЦ РАЗБОРА ===");
        return 0;
    }
}
