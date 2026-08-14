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
    string AppId)
{
    public static readonly MediaSnapshot Empty = new(false, false, "", "", "");
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
            }

            _session = _manager?.GetCurrentSession();

            if (_session is not null)
            {
                _session.MediaPropertiesChanged += OnPropertiesChanged;
                _session.PlaybackInfoChanged += OnPlaybackChanged;
            }

            _ = RefreshAsync();
        }
        catch (Exception ex) { Log.Write("AttachSession: " + ex.Message); }
    }

    private void OnPropertiesChanged(GlobalSystemMediaTransportControlsSession s, object e) => _ = RefreshAsync();
    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession s, object e) => _ = RefreshAsync();

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

            _snapshot = new MediaSnapshot(
                HasSession: true,
                IsPlaying: playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                Title: title,
                Artist: artist,
                AppId: session.SourceAppUserModelId ?? "");

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
            _session = null;
        }
        SetArtwork(null, "");
        return ValueTask.CompletedTask;
    }
}
