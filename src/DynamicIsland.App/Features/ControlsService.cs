using System.Net.Http;
using System.Text.Json;
using DynamicIsland.Configuration;
using DynamicIsland.Ui;
using SkiaSharp;
using Windows.Devices.Radios;

namespace DynamicIsland.Features;

/// <summary>Состояние переключателей вкладки «Управление» и слежение за VPN.</summary>
internal sealed class ControlsService : IDisposable
{
    private readonly Settings _settings;
    private readonly PillHub _hub;
    private readonly Timer _vpnTimer;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private int _refreshing;
    private bool _vpnWasUp;
    private bool _vpnFirst = true;

    public bool? Wifi { get; private set; }
    public bool? Bluetooth { get; private set; }
    public bool Dark { get; private set; }
    public bool VpnUp { get; private set; }
    public string VpnInfo { get; private set; } = "";
    public List<VpnProfile> Vpn { get; private set; } = [];
    public List<BtDevice> BtDevices { get; private set; } = [];

    public event Action? Changed;

    public ControlsService(Settings settings, PillHub hub)
    {
        _settings = settings;
        _hub = hub;
        _vpnTimer = new Timer(_ => CheckVpn(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void StartMonitoring()
    {
        // Перечисление сетевых адаптеров стоит ~15 мс, поэтому по таймеру его гоняем редко,
        // а о смене сети узнаём от системы.
        _vpnTimer.Change(1500, 60_000);
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (_, _) => _vpnTimer.Change(2000, 60_000);
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += (_, _) => _vpnTimer.Change(2000, 60_000);
    }

    public void Refresh()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                Wifi = await SystemControl.GetRadioAsync(RadioKind.WiFi);
                Bluetooth = await SystemControl.GetRadioAsync(RadioKind.Bluetooth);
                Dark = SystemControl.IsDark();
                VpnUp = SystemControl.IsVpnUp();
                Changed?.Invoke();

                BtDevices = await SystemControl.PairedBluetoothAsync();
                Vpn = await SystemControl.ListVpnAsync();
                Changed?.Invoke();
            }
            catch (Exception ex) { Log.Write("Обновление управления: " + ex.Message); }
            finally { Interlocked.Exchange(ref _refreshing, 0); }
        });
    }

    public void ToggleWifi() => _ = Task.Run(async () =>
    {
        await SystemControl.ToggleRadioAsync(RadioKind.WiFi);
        Wifi = await SystemControl.GetRadioAsync(RadioKind.WiFi);
        Changed?.Invoke();
    });

    public void ToggleBluetooth() => _ = Task.Run(async () =>
    {
        await SystemControl.ToggleRadioAsync(RadioKind.Bluetooth);
        Bluetooth = await SystemControl.GetRadioAsync(RadioKind.Bluetooth);
        Changed?.Invoke();
    });

    public void ToggleTheme()
    {
        SystemControl.ToggleTheme();
        Dark = SystemControl.IsDark();
        Changed?.Invoke();
    }

    public void ToggleVpn()
    {
        var profile = Vpn.FirstOrDefault(p => p.Connected) ?? Vpn.FirstOrDefault();
        if (profile is null) return;
        SystemControl.ToggleVpn(profile);
        _ = Task.Run(async () =>
        {
            await Task.Delay(2500);
            CheckVpn();
            Vpn = await SystemControl.ListVpnAsync();
            Changed?.Invoke();
        });
    }

    private void CheckVpn()
    {
        var up = SystemControl.IsVpnUp();
        if (up == VpnUp && !_vpnFirst) return;

        VpnUp = up;
        var changed = up != _vpnWasUp || _vpnFirst;
        var first = _vpnFirst;
        _vpnWasUp = up;
        _vpnFirst = false;

        if (!changed) return;

        if (!up)
        {
            VpnInfo = "";
            _hub.SetChip("vpn", null);
            if (!first && _settings.EventVpn)
                _hub.Post(new Card { Glyph = G.Shield, Title = "VPN отключён", Subtitle = "Соединение идёт напрямую", Tint = new SKColor(255, 159, 10), Seconds = 3f });
            Changed?.Invoke();
            return;
        }

        _ = Task.Run(async () =>
        {
            var (country, code, ip) = await LookupAsync();
            VpnInfo = country.Length > 0 ? $"{country} · {ip}" : "";
            if (!_settings.EventVpn) { Changed?.Invoke(); return; }

            _hub.SetChip("vpn", new Chip("vpn", G.Shield, code, new SKColor(52, 199, 89), Priority: 40));
            if (!first)
                _hub.Post(new Card
                {
                    Glyph = G.Shield,
                    Title = "VPN подключён",
                    Subtitle = country.Length > 0 ? $"{country} · {ip}" : "Защищённое соединение",
                    Tint = new SKColor(52, 199, 89),
                    Seconds = 4f,
                });
            Changed?.Invoke();
        });
    }

    /// <summary>Страна и IP по внешнему адресу. Запрос уходит только когда VPN поднялся.</summary>
    private async Task<(string country, string code, string ip)> LookupAsync()
    {
        try
        {
            using var response = await _http.GetAsync("https://ipwho.is/?lang=ru&fields=ip,country,country_code,success");
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False) return ("", "VPN", "");
            return (root.GetProperty("country").GetString() ?? "",
                    root.GetProperty("country_code").GetString() ?? "VPN",
                    root.GetProperty("ip").GetString() ?? "");
        }
        catch { return ("", "VPN", ""); }
    }

    public void Dispose()
    {
        _vpnTimer.Dispose();
        _http.Dispose();
    }
}
