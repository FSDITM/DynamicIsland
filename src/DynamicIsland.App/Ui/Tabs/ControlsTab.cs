using DynamicIsland.Features;
using SkiaSharp;

namespace DynamicIsland.Ui.Tabs;

/// <summary>Wi-Fi, Bluetooth, тема, VPN, громкость, микрофон и питание.</summary>
internal sealed class ControlsTab(ControlsService controls, AudioService audio) : ITabView
{
    private bool _draggingVolume;
    private DateTime _confirmUntil;
    private string _confirmWhat = "";
    private float _volume = -1;

    public void Activated()
    {
        controls.Refresh();
        _volume = audio.Volume ?? -1;
    }

    /// <summary>Переключатель-строка: значок, название и состояние текстом. Без плиток.</summary>
    private void Switch(UiKit ui, SKRect r, string glyph, string label, string state, bool on, Action click)
    {
        var hover = ui.IsHover(r);
        ui.Row(r, hover);
        ui.Glyph(glyph, r.Left + 12, r.MidY + 2.5f, 15, on ? ui.Accent : ui.Pal.Muted);
        ui.Text(label, r.Left + 32, r.MidY + 4.5f, 12.5f, ui.Pal.Primary, bold: true);
        ui.Circle(r.Right - 12 - ui.Measure(state, 11) - 8, r.MidY, 3f, on ? ui.Accent : ui.Pal.Line);
        ui.Text(state, r.Right - 10, r.MidY + 4, 11, on ? ui.Pal.Primary : ui.Pal.Muted, SKTextAlign.Right);
        ui.Hit(r, () => { Log.Write("Переключатель: " + label); click(); });
    }

    public void Draw(UiKit ui)
    {
        var pal = ui.Pal;
        ui.Text("Управление", 0, 14, 13, pal.Primary, bold: true);
        if (controls.VpnUp && controls.VpnInfo.Length > 0)
            ui.Text("VPN: " + controls.VpnInfo, ui.Width, 14, 11, new SKColor(52, 199, 89), SKTextAlign.Right);

        // Переключатели: два столбца по две строки.
        const float gap = 14f;
        var cw = (ui.Width - gap) / 2f;
        SKRect Cell(int col, int row) => new(col * (cw + gap), 24 + row * 32, col * (cw + gap) + cw, 24 + row * 32 + 31);

        Switch(ui, Cell(0, 0), G.Wifi, "Wi-Fi",
            controls.Wifi is null ? "нет модуля" : controls.Wifi.Value ? "включён" : "выключен",
            controls.Wifi == true, controls.ToggleWifi);

        Switch(ui, Cell(1, 0), G.Bluetooth, "Bluetooth",
            controls.Bluetooth is null ? "нет модуля" : controls.Bluetooth.Value ? "включён" : "выключен",
            controls.Bluetooth == true, controls.ToggleBluetooth);

        Switch(ui, Cell(0, 1), G.Moon, "Тёмная тема",
            controls.Dark ? "включена" : "выключена", controls.Dark, controls.ToggleTheme);

        var vpnState = controls.Vpn.Count == 0 && !controls.VpnUp ? "нет подключений"
            : controls.VpnUp ? "подключён" : "отключён";
        Switch(ui, Cell(1, 1), G.Shield, "VPN", vpnState, controls.VpnUp, controls.ToggleVpn);

        // Громкость: тянется мышью.
        var rowY = 100f;
        var muted = audio.Muted == true;
        ui.IconButton(new SKRect(0, rowY, 28, rowY + 28), muted ? G.Mute : G.Volume, audio.ToggleMute,
            color: muted ? pal.Muted : pal.Primary);

        var track = new SKRect(40, rowY + 12, ui.Width - 130, rowY + 18);
        var hit = new SKRect(track.Left - 6, rowY, track.Right + 6, rowY + 30);

        if (_volume < 0) _volume = audio.Volume ?? 0;
        if (_draggingVolume)
        {
            if (!ui.MouseDown) _draggingVolume = false;
            else
            {
                _volume = Math.Clamp((ui.Cursor.X - track.Left) / track.Width, 0f, 1f);
                audio.Volume = _volume;
            }
        }

        var thin = new SKRect(track.Left, track.MidY - 1.5f, track.Right, track.MidY + 1.5f);
        ui.RoundRect(thin, 1.5f, pal.Hover);
        ui.RoundRect(new SKRect(thin.Left, thin.Top, thin.Left + thin.Width * _volume, thin.Bottom), 1.5f, muted ? pal.Muted : ui.Accent);
        ui.Circle(track.Left + track.Width * _volume, track.MidY, _draggingVolume ? 6.5f : 5f, muted ? pal.Muted : ui.Accent);
        ui.Hit(hit, null, null, () => { _draggingVolume = true; });
        ui.Text($"{(int)Math.Round(_volume * 100)}%", track.Right + 14, rowY + 19, 11.5f, pal.Muted);

        // Микрофон.
        var micOff = audio.MicMuted == true;
        var mic = new SKRect(ui.Width - 74, rowY, ui.Width, rowY + 28);
        ui.Button(mic, micOff ? "Выкл." : "Микр.", () => audio.MicMuted = !(audio.MicMuted ?? false),
            danger: micOff, glyph: G.Mic);

        // Bluetooth-устройства.
        var listY = 142f;
        ui.Text("Bluetooth-устройства", 0, listY + 4, 11.5f, pal.Muted, bold: true);
        var y = listY + 12f;
        if (controls.BtDevices.Count == 0)
        {
            ui.Text(controls.Bluetooth == false ? "Bluetooth выключен" : "Нет сопряжённых устройств", 0, y + 16, 11.5f, pal.Muted);
        }
        foreach (var d in controls.BtDevices.Take(3))
        {
            var row = new SKRect(0, y, ui.Width * 0.58f, y + 24);
            ui.Row(row, false);
            ui.Circle(12, row.MidY, 3.5f, d.Connected ? new SKColor(52, 199, 89) : pal.Muted);
            ui.Text(ui.Fit(d.Name, 11.5f, row.Width - 80), 24, row.MidY + 4, 11.5f, pal.Primary);
            ui.Text(d.Connected ? "подключено" : "не подключено", row.Right - 8, row.MidY + 4, 10, pal.Muted, SKTextAlign.Right);
            y += 28f;
        }

        // Питание: опасные действия подтверждаются вторым нажатием.
        var by = ui.Height - 30f;
        var px = ui.Width * 0.58f + 14f;
        var pw = (ui.Width - px - 8f) / 2f;
        void Power(int col, int row, string label, string glyph, string key, Action act, bool danger)
        {
            var r = new SKRect(px + col * (pw + 8f), by - row * 32, px + col * (pw + 8f) + pw, by - row * 32 + 26);
            var confirming = _confirmWhat == key && DateTime.Now < _confirmUntil;
            ui.Button(r, confirming ? "Ещё раз" : label, () =>
            {
                if (!danger) { act(); return; }
                if (_confirmWhat == key && DateTime.Now < _confirmUntil) { _confirmWhat = ""; act(); }
                else { _confirmWhat = key; _confirmUntil = DateTime.Now.AddSeconds(3); }
            }, danger: danger && confirming, glyph: glyph);
        }

        Power(0, 1, "Блок", G.Lock, "lock", SystemControl.Lock, false);
        Power(1, 1, "Сон", G.Moon, "sleep", SystemControl.Sleep, false);
        Power(0, 0, "Рестарт", G.Restart, "restart", SystemControl.Restart, true);
        Power(1, 0, "Выкл.", G.Power, "shutdown", SystemControl.ShutDown, true);
    }
}
