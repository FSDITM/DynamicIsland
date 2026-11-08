using DynamicIsland.Configuration;
using DynamicIsland.Ui;
using SkiaSharp;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace DynamicIsland.Features;

/// <summary>Одно системное уведомление, как его увидел островок.</summary>
internal sealed record ToastInfo(uint Id, string App, string Title, string Body, DateTime Time);

/// <summary>
/// Читает уведомления Windows (то, что приходит в центр уведомлений) и показывает их карточкой.
/// Доступ даёт сам пользователь: «Параметры → Конфиденциальность → Уведомления».
/// </summary>
internal sealed class NotificationService : IDisposable
{
    private readonly Settings _settings;
    private readonly PillHub _hub;
    private readonly HashSet<uint> _seen = [];
    private readonly Timer _poll;
    private UserNotificationListener? _listener;
    private bool _first = true;
    private int _busy;

    public UserNotificationListenerAccessStatus Access { get; private set; } = UserNotificationListenerAccessStatus.Unspecified;
    public string Problem { get; private set; } = "";

    /// <summary>Пришло новое уведомление. Нужно слушателям (например, ответу в Telegram).</summary>
    public event Action<ToastInfo>? Arrived;

    public NotificationService(Settings settings, PillHub hub)
    {
        _settings = settings;
        _hub = hub;
        _poll = new Timer(_ => _ = PollAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public async Task StartAsync()
    {
        try
        {
            _listener = UserNotificationListener.Current;
            Access = await _listener.RequestAccessAsync();
            Log.Write("Доступ к уведомлениям: " + Access);
            if (Access != UserNotificationListenerAccessStatus.Allowed)
            {
                Problem = "Разрешите доступ к уведомлениям в параметрах Windows";
                return;
            }

            _poll.Change(500, 1500);
        }
        catch (Exception ex)
        {
            Problem = ex.Message;
            Log.Write("Уведомления недоступны: " + ex.Message);
        }
    }

    private async Task PollAsync()
    {
        if (_listener is null || Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var list = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            var current = new HashSet<uint>();

            foreach (var n in list.OrderBy(n => n.CreationTime))
            {
                current.Add(n.Id);
                if (!_seen.Add(n.Id)) continue;
                if (_first) continue;          // накопленное до запуска не показываем

                var info = Describe(n);
                if (info is null) continue;
                Show(info);
                Arrived?.Invoke(info);
            }

            _seen.IntersectWith(current);
            _first = false;
        }
        catch (Exception ex) { Log.Write("Чтение уведомлений: " + ex.Message); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    public static ToastInfo? Describe(UserNotification n)
    {
        try
        {
            var app = n.AppInfo?.DisplayInfo?.DisplayName ?? "";
            var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
            var texts = binding?.GetTextElements().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? [];
            if (texts.Count == 0) return null;

            return new ToastInfo(n.Id, app, texts[0], string.Join("\n", texts.Skip(1)), n.CreationTime.LocalDateTime);
        }
        catch { return null; }
    }

    private void Show(ToastInfo info)
    {
        if (!_settings.ShowNotifications) return;
        if (_settings.NotificationBlocklist.Any(b => info.App.Contains(b, StringComparison.OrdinalIgnoreCase))) return;

        var telegram = IsTelegram(info.App);

        // Аватар и данные для ответа лежат в базе уведомлений Windows; читаем её только для карточки.
        SKImage? avatar = null;
        if (ToastPayload.Read(info.Id) is { } xml)
            avatar = ToastPayload.Avatar(xml);
        var body = info.Body.Replace('\n', ' ');
        _hub.Post(new Card
        {
            Glyph = telegram ? G.Share : G.Notes,
            Title = info.Title,
            Subtitle = body.Length > 0 ? body : info.App,
            Tint = telegram ? new SKColor(42, 171, 238) : null,
            Image = avatar,
            RoundImage = avatar is not null,
            Seconds = 7f,
            AutoClose = true,
        });
    }

    public static bool IsTelegram(string app) =>
        app.Contains("Telegram", StringComparison.OrdinalIgnoreCase);

    public void Dispose() => _poll.Dispose();
}
