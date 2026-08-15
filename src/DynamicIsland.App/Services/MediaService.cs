using SkiaSharp;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DynamicIsland.Services;

/// <summary>Снимок состояния проигрывателя. Неизменяемый — читается из потока отрисовки.</summary>
internal sealed record MediaSnapshot(
    bool HasSession,
    bool IsPlaying,
    string Title,
    string Artist,
    string AppId,
    TimeSpan Position = default,
    TimeSpan Duration = default,
    long PositionStamp = 0,
    bool CanSeek = false)
{
    public static readonly MediaSnapshot Empty = new(false, false, "", "", "");

    /// <summary>
    /// Позиция с поправкой на время, прошедшее с момента её получения.
    ///
    /// Windows присылает позицию событиями, а не непрерывно: между событиями
    /// могут пройти секунды. Без экстраполяции ползунок дёргался бы рывками.
    /// </summary>
    public TimeSpan EffectivePosition
    {
        get
        {
            if (!IsPlaying || PositionStamp == 0) return Position;

            var elapsed = (System.Diagnostics.Stopwatch.GetTimestamp() - PositionStamp)
                          / (double)System.Diagnostics.Stopwatch.Frequency;
            var value = Position + TimeSpan.FromSeconds(elapsed);
            return value > Duration ? Duration : value;
        }
    }

    /// <summary>Доля проигранного, 0..1.</summary>
    public float Progress => Duration > TimeSpan.Zero
        ? Math.Clamp((float)(EffectivePosition.TotalSeconds / Duration.TotalSeconds), 0f, 1f)
        : 0f;

    public bool HasTimeline => Duration > TimeSpan.Zero;
}

/// <summary>
/// Информация о воспроизведении через штатный Windows-API
/// (GlobalSystemMediaTransportControls) — тот же источник, что у всплывашки
/// громкости. Работает со Spotify, браузерами, плеерами без плагинов.
///
/// Всё на событиях: опроса нет, поток отрисовки читает готовый снимок.
/// </summary>
internal sealed class MediaService : IAsyncDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;

    private volatile MediaSnapshot _snapshot = MediaSnapshot.Empty;
    private SKImage? _pendingArtwork;
    private int _artworkVersion;
    private readonly Lock _artworkGate = new();
    private string _artworkKey = "";

    /// <summary>Данные изменились — пора перерисовать кадр.</summary>
    public event Action? Changed;

    public MediaSnapshot Snapshot => _snapshot;

    /// <summary>
    /// Забирает обложку во владение вызывающего, если она сменилась.
    /// Декодирует её поток WinRT, а рисует поток UI — поэтому владение передаётся
    /// явно, а не отдаётся ссылкой: иначе сервис освободил бы SKImage ровно тогда,
    /// когда отрисовка его читает.
    /// </summary>
    public bool TryTakeArtwork(ref int seenVersion, out SKImage? image)
    {
        lock (_artworkGate)
        {
            if (seenVersion == _artworkVersion) { image = null; return false; }
            seenVersion = _artworkVersion;
            image = _pendingArtwork;
            _pendingArtwork = null;
            return true;
        }
    }

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => AttachSession();
            AttachSession();
        }
        catch (Exception ex)
        {
            // Нет медиа-сессий или API недоступен — не повод падать.
            Log.Write("MediaService недоступен: " + ex.Message);
        }
    }

    private void AttachSession()
    {
        try
        {
            if (_session is not null)
            {
                _session.MediaPropertiesChanged -= OnPropertiesChanged;
                _session.PlaybackInfoChanged -= OnPlaybackChanged;
                _session.TimelinePropertiesChanged -= OnTimelineChanged;
            }

            _session = _manager?.GetCurrentSession();

            if (_session is not null)
            {
                _session.MediaPropertiesChanged += OnPropertiesChanged;
                _session.PlaybackInfoChanged += OnPlaybackChanged;
                _session.TimelinePropertiesChanged += OnTimelineChanged;
            }

            _ = RefreshAsync();
        }
        catch (Exception ex) { Log.Write("AttachSession: " + ex.Message); }
    }

    private void OnPropertiesChanged(GlobalSystemMediaTransportControlsSession s, object e) => _ = RefreshAsync();
    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession s, object e) => _ = RefreshAsync();
    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession s, object e) => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        try
        {
            var session = _session;
            if (session is null)
            {
                _snapshot = MediaSnapshot.Empty;
                SetArtwork(null, "");
                Changed?.Invoke();
                return;
            }

            var playback = session.GetPlaybackInfo();
            var props = await session.TryGetMediaPropertiesAsync();

            var title = props?.Title ?? "";
            var artist = props?.Artist ?? "";

            // Отметку времени ставим сразу после чтения: по ней потом
            // экстраполируется позиция между событиями.
            var timeline = session.GetTimelineProperties();
            var stamp = System.Diagnostics.Stopwatch.GetTimestamp();

            var duration = timeline is null ? TimeSpan.Zero : timeline.EndTime - timeline.StartTime;
            var position = timeline is null ? TimeSpan.Zero : timeline.Position - timeline.StartTime;
            if (position < TimeSpan.Zero) position = TimeSpan.Zero;
            if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;

            _snapshot = new MediaSnapshot(
                HasSession: true,
                IsPlaying: playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                Title: title,
                Artist: artist,
                AppId: session.SourceAppUserModelId ?? "",
                Position: position,
                Duration: duration,
                PositionStamp: stamp,
                CanSeek: playback?.Controls.IsPlaybackPositionEnabled ?? false);

            // Обложка декодируется только когда сменился трек — это самая
            // дорогая операция в сервисе.
            var key = $"{session.SourceAppUserModelId}|{title}|{artist}";
            if (key != _artworkKey && props?.Thumbnail is { } thumb)
                SetArtwork(await DecodeThumbnailAsync(thumb), key);

            Changed?.Invoke();
        }
        catch (Exception ex) { Log.Write("MediaService.Refresh: " + ex.Message); }
    }

    private static async Task<SKImage?> DecodeThumbnailAsync(IRandomAccessStreamReference reference)
    {
        try
        {
            using var stream = await reference.OpenReadAsync();
            using var net = stream.AsStreamForRead();
            using var buffer = new MemoryStream();
            await net.CopyToAsync(buffer);
            buffer.Position = 0;

            using var data = SKData.Create(buffer);
            using var bitmap = SKBitmap.Decode(data);
            return bitmap is null ? null : SKImage.FromBitmap(bitmap);
        }
        catch { return null; }
    }

    private void SetArtwork(SKImage? image, string key)
    {
        lock (_artworkGate)
        {
            // Освобождаем только то, что потребитель ещё не забрал.
            _pendingArtwork?.Dispose();
            _pendingArtwork = image;
            _artworkKey = key;
            _artworkVersion++;
        }
    }

    /// <summary>Перемотать на долю трека 0..1.</summary>
    public Task SeekAsync(float progress)
    {
        var snapshot = _snapshot;
        if (!snapshot.HasTimeline) return Task.CompletedTask;

        var target = TimeSpan.FromSeconds(snapshot.Duration.TotalSeconds * Math.Clamp(progress, 0f, 1f));

        // Обновляем снимок сразу, не дожидаясь события от плеера: иначе
        // ползунок прыгал бы обратно на старое место на пару кадров.
        _snapshot = snapshot with { Position = target, PositionStamp = System.Diagnostics.Stopwatch.GetTimestamp() };

        return Safe(s => s.TryChangePlaybackPositionAsync(target.Ticks).AsTask());
    }

    public Task TogglePlayPauseAsync() => Safe(s => s.TryTogglePlayPauseAsync().AsTask());
    public Task NextAsync() => Safe(s => s.TrySkipNextAsync().AsTask());
    public Task PreviousAsync() => Safe(s => s.TrySkipPreviousAsync().AsTask());

    private async Task Safe(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> action)
    {
        try
        {
            if (_session is { } s) await action(s);
        }
        catch (Exception ex) { Log.Write("MediaService команда: " + ex.Message); }
    }

    public ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackChanged;
            _session.TimelinePropertiesChanged -= OnTimelineChanged;
            _session = null;
        }
        SetArtwork(null, "");
        return ValueTask.CompletedTask;
    }
}
