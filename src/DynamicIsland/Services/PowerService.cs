using System.Windows.Threading;
using DynamicIsland.Core;
using Windows.System.Power;

namespace DynamicIsland.Services;

/// <summary>Battery level and charger state, from WinRT power events (no polling).</summary>
public sealed class PowerService : ObservableObject, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private bool _subscribed;
    private bool _hasBattery;
    private bool _isCharging;
    private int _percent = 100;

    public PowerService(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>Charger plugged in; argument is the battery percentage.</summary>
    public event Action<int>? ChargingStarted;

    /// <summary>Battery crossed 20% or 10% while discharging.</summary>
    public event Action<int>? LowBattery;

    public bool HasBattery { get => _hasBattery; private set => Set(ref _hasBattery, value); }
    public bool IsCharging { get => _isCharging; private set => Set(ref _isCharging, value); }

    public int Percent
    {
        get => _percent;
        private set
        {
            if (Set(ref _percent, value))
            {
                OnPropertyChanged(nameof(Level));
            }
        }
    }

    public double Level => _percent / 100.0;

    public void Initialize()
    {
        Read(initial: true);
        try
        {
            PowerManager.BatteryStatusChanged += OnPowerChanged;
            PowerManager.PowerSupplyStatusChanged += OnPowerChanged;
            PowerManager.RemainingChargePercentChanged += OnPowerChanged;
            _subscribed = true;
        }
        catch (Exception ex)
        {
            Log.Warn("Power events unavailable", ex);
        }
    }

    private void OnPowerChanged(object? sender, object e) => _dispatcher.InvokeAsync(() => Read(initial: false));

    private void Read(bool initial)
    {
        BatteryStatus battery;
        PowerSupplyStatus supply;
        int percent;
        try
        {
            battery = PowerManager.BatteryStatus;
            supply = PowerManager.PowerSupplyStatus;
            percent = Math.Clamp(PowerManager.RemainingChargePercent, 0, 100);
        }
        catch (Exception ex)
        {
            Log.Warn("Reading power state failed", ex);
            return;
        }

        bool hasBattery = battery != BatteryStatus.NotPresent;
        bool charging = hasBattery && (battery == BatteryStatus.Charging || supply == PowerSupplyStatus.Adequate);

        if (!initial && hasBattery)
        {
            if (charging && !_isCharging)
            {
                ChargingStarted?.Invoke(percent);
            }
            else if (!charging && ((_percent > 20 && percent <= 20) || (_percent > 10 && percent <= 10)))
            {
                LowBattery?.Invoke(percent);
            }
        }

        HasBattery = hasBattery;
        IsCharging = charging;
        Percent = percent;
    }

    public void Dispose()
    {
        if (!_subscribed)
        {
            return;
        }
        PowerManager.BatteryStatusChanged -= OnPowerChanged;
        PowerManager.PowerSupplyStatusChanged -= OnPowerChanged;
        PowerManager.RemainingChargePercentChanged -= OnPowerChanged;
        _subscribed = false;
    }
}
