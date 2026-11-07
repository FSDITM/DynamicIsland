using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Text.Json;
using DynamicIsland.Interop;
using Microsoft.Win32;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace DynamicIsland.Features;

internal sealed record BtDevice(string Name, bool Connected);

internal sealed record VpnProfile(string Name, string Kind, bool Connected);

/// <summary>Wi-Fi, Bluetooth, тема, питание, VPN. Всё штатными средствами Windows.</summary>
internal static class SystemControl
{
    // ---------- Радиомодули ----------

    private static async Task<List<Radio>> RadiosAsync(RadioKind kind)
    {
        try
        {
            var access = await Radio.RequestAccessAsync();
            if (access != RadioAccessStatus.Allowed) return [];
            var all = await Radio.GetRadiosAsync();
            return all.Where(r => r.Kind == kind).ToList();
        }
        catch (Exception ex)
        {
            Log.Write("Радиомодули недоступны: " + ex.Message);
            return [];
        }
    }

    /// <summary>true — включён, false — выключен, null — такого модуля нет.</summary>
    public static async Task<bool?> GetRadioAsync(RadioKind kind)
    {
        var list = await RadiosAsync(kind);
        if (list.Count == 0) return null;
        return list.Any(r => r.State == RadioState.On);
    }

    public static async Task SetRadioAsync(RadioKind kind, bool on)
    {
        foreach (var r in await RadiosAsync(kind))
        {
            try { await r.SetStateAsync(on ? RadioState.On : RadioState.Off); }
            catch (Exception ex) { Log.Write("Не переключился модуль: " + ex.Message); }
        }
    }

    public static async Task ToggleRadioAsync(RadioKind kind)
    {
        var state = await GetRadioAsync(kind);
        if (state is null) return;
        await SetRadioAsync(kind, !state.Value);
    }

    public static async Task<List<BtDevice>> PairedBluetoothAsync()
    {
        var result = new List<BtDevice>();
        try
        {
            const string connected = "System.Devices.Aep.IsConnected";
            var selector = Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromPairingState(true);
            var devices = await DeviceInformation.FindAllAsync(selector, [connected]);
            foreach (var d in devices)
            {
                var on = d.Properties.TryGetValue(connected, out var v) && v is true;
                if (!string.IsNullOrWhiteSpace(d.Name)) result.Add(new BtDevice(d.Name, on));
            }
        }
        catch (Exception ex) { Log.Write("Bluetooth-устройства не получены: " + ex.Message); }
        return result.OrderByDescending(d => d.Connected).ThenBy(d => d.Name).ToList();
    }

    // ---------- Тема ----------

    public static bool IsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    public static void ToggleTheme()
    {
        try
        {
            var toLight = IsDark() ? 1 : 0;
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: true);
            if (key is null) return;
            key.SetValue("AppsUseLightTheme", toLight, RegistryValueKind.DWord);
            key.SetValue("SystemUsesLightTheme", toLight, RegistryValueKind.DWord);
            Native.SendMessageTimeoutW(0xFFFF, (uint)Win32.WM_SETTINGCHANGE, 0, "ImmersiveColorSet", 2, 200, out _);
        }
        catch (Exception ex) { Log.Write("Тема не переключилась: " + ex.Message); }
    }

    // ---------- Питание ----------

    public static void Lock() => Native.LockWorkStation();

    public static void Sleep() => Native.SetSuspendState(false, false, false);

    public static void Restart() => Run("shutdown", "/r /t 0");

    public static void ShutDown() => Run("shutdown", "/s /t 0");

    private static void Run(string file, string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file, args) { CreateNoWindow = true, UseShellExecute = false });
        }
        catch (Exception ex) { Log.Write($"{file} {args}: {ex.Message}"); }
    }

    // ---------- VPN ----------

    private static readonly string[] VpnNameParts =
        ["vpn", "wireguard", "wintun", "tap-windows", "tailscale", "warp", "mullvad", "nord", "proton",
         "openvpn", "happ", "v2ray", "xray", "sing-box", "singbox", "clash", "karing", "hiddify", "outline"];

    /// <summary>Поднято ли сейчас VPN-соединение (по сетевым адаптерам).</summary>
    public static bool IsVpnUp()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback) continue;

                var text = (nic.Name + " " + nic.Description).ToLowerInvariant();
                var named = VpnNameParts.Any(text.Contains);
                var tunnel = nic.NetworkInterfaceType is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel;
                if (named || (tunnel && !text.Contains("isatap") && !text.Contains("teredo") && !text.Contains("6to4")))
                {
                    // Адаптер без IPv4-адреса — «спящий», считаем выключенным.
                    if (nic.GetIPProperties().UnicastAddresses.Any(a =>
                            a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)) return true;
                }
            }
        }
        catch { /* сеть меняется на лету */ }
        return false;
    }

    /// <summary>Встроенные подключения Windows и найденные клиенты с командной строкой.</summary>
    public static async Task<List<VpnProfile>> ListVpnAsync()
    {
        var list = new List<VpnProfile>();

        try
        {
            var json = await RunCapture("powershell.exe",
                "-NoProfile -NonInteractive -Command \"Get-VpnConnection | Select-Object Name,ConnectionStatus | ConvertTo-Json -Compress\"");
            if (!string.IsNullOrWhiteSpace(json))
            {
                using var doc = JsonDocument.Parse(json);
                var items = doc.RootElement.ValueKind == JsonValueKind.Array
                    ? doc.RootElement.EnumerateArray().ToList()
                    : [doc.RootElement];
                foreach (var it in items)
                {
                    var name = it.GetProperty("Name").GetString() ?? "";
                    var status = it.TryGetProperty("ConnectionStatus", out var cs) ? cs.ToString() : "";
                    if (name.Length > 0)
                        list.Add(new VpnProfile(name, "windows", status is "Connected" or "2"));
                }
            }
        }
        catch (Exception ex) { Log.Write("VPN-подключения Windows не прочитаны: " + ex.Message); }

        var warp = @"C:\Program Files\Cloudflare\Cloudflare WARP\warp-cli.exe";
        if (File.Exists(warp)) list.Add(new VpnProfile("Cloudflare WARP", "warp", IsVpnUp()));

        var mullvad = @"C:\Program Files\Mullvad VPN\resources\mullvad.exe";
        if (File.Exists(mullvad)) list.Add(new VpnProfile("Mullvad", "mullvad", IsVpnUp()));

        return list;
    }

    public static void ToggleVpn(VpnProfile p)
    {
        switch (p.Kind)
        {
            case "windows":
                Run("rasdial", p.Connected ? $"\"{p.Name}\" /disconnect" : $"\"{p.Name}\"");
                break;
            case "warp":
                Run(@"C:\Program Files\Cloudflare\Cloudflare WARP\warp-cli.exe", p.Connected ? "disconnect" : "connect");
                break;
            case "mullvad":
                Run(@"C:\Program Files\Mullvad VPN\resources\mullvad.exe", p.Connected ? "disconnect" : "connect");
                break;
        }
    }

    private static async Task<string> RunCapture(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        });
        if (p is null) return "";
        var text = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return text.Trim();
    }

    public static async Task<string> CaptureAsync(string file, string args) => await RunCapture(file, args);
}
