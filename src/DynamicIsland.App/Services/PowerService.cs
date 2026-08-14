using Windows.System.Power;

namespace DynamicIsland.Services;

internal sealed record PowerSnapshot(int Percent, bool IsCharging, bool HasBattery)
{
    public static readonly PowerSnapshot Unknown = new(100, false, false);
}

/// <summary>
/// Заряд батареи через WinRT PowerManager — на событиях.
///
/// Для сравнения: оригинальный DynamicWin дёргал WMI-запрос яркости
/// (<c>new ManagementClass("WmiMonitorBrightness").GetInstances()</c>) на каждом
/// кадре в UI-потоке. Системные данные не должны появляться в рендер-цикле вообще.
/// </summary>
internal sealed class PowerService : IDisposable
{
    private volatile PowerSnapshot _snapshot = PowerSnapshot.Unknown;

    public PowerSnapshot Snapshot => _snapshot;
    public event Action? Changed;

    public void Start()
    {
        try
        {
            PowerManager.RemainingChargePercentChanged += OnChanged;
            PowerManager.BatteryStatusChanged += OnChanged;
            PowerManager.PowerSupplyStatusChanged += OnChanged;
            Refresh();
        }
        catch (Exception ex) { Log.Write("PowerService недоступен: " + ex.Message); }
    }

    private void OnChanged(object? sender, object e) => Refresh();

    private void Refresh()
    {
        try
        {
            var status = PowerManager.BatteryStatus;
            _snapshot = new PowerSnapshot(
                Percent: PowerManager.RemainingChargePercent,
                IsCharging: status == BatteryStatus.Charging,
                HasBattery: status != BatteryStatus.NotPresent);

            Changed?.Invoke();
        }
        catch (Exception ex) { Log.Write("PowerService.Refresh: " + ex.Message); }
    }

    public void Dispose()
    {
        try
        {
            PowerManager.RemainingChargePercentChanged -= OnChanged;
            PowerManager.BatteryStatusChanged -= OnChanged;
            PowerManager.PowerSupplyStatusChanged -= OnChanged;
        }
        catch { /* при выгрузке подписки могут быть уже сняты */ }
    }
}
