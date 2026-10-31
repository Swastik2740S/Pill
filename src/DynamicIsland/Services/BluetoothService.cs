using System.Windows.Threading;
using DynamicIsland.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace DynamicIsland.Services;

/// <summary>
/// Watches paired Bluetooth devices (classic and LE) and reports connect / disconnect.
/// DeviceWatcher is push based, so this costs nothing while nothing changes.
/// </summary>
public sealed class BluetoothService : IDisposable
{
    private const string IsConnectedKey = "System.Devices.Aep.IsConnected";

    private readonly Dispatcher _dispatcher;
    private readonly List<DeviceWatcher> _watchers = new();
    private readonly Dictionary<string, DeviceEntry> _devices = new();
    private readonly Dictionary<string, DateTime> _recentEvents = new();

    public BluetoothService(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>Raised on the UI thread with (device name, connected).</summary>
    public event Action<string, bool>? ConnectionChanged;

    public void Initialize()
    {
        string[] selectors;
        try
        {
            selectors = new[]
            {
                BluetoothDevice.GetDeviceSelectorFromPairingState(true),
                BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
            };
        }
        catch (Exception ex)
        {
            Log.Warn("Bluetooth unavailable", ex);
            return;
        }

        foreach (string selector in selectors)
        {
            try
            {
                var state = new WatcherState();
                var watcher = DeviceInformation.CreateWatcher(selector, new[] { IsConnectedKey }, DeviceInformationKind.AssociationEndpoint);
                watcher.Added += (_, info) =>
                {
                    string id = info.Id;
                    string name = info.Name;
                    bool connected = ReadConnected(info.Properties);
                    _dispatcher.InvokeAsync(() => OnAdded(state, id, name, connected));
                };
                watcher.Updated += (_, update) =>
                {
                    string id = update.Id;
                    bool? connected = update.Properties.TryGetValue(IsConnectedKey, out object? value) && value is bool b ? b : null;
                    _dispatcher.InvokeAsync(() => OnUpdated(id, connected));
                };
                watcher.Removed += (_, update) =>
                {
                    string id = update.Id;
                    _dispatcher.InvokeAsync(() => _devices.Remove(id));
                };
                watcher.EnumerationCompleted += (_, _) => _dispatcher.InvokeAsync(() => state.Ready = true);
                _watchers.Add(watcher);
                watcher.Start();
            }
            catch (Exception ex)
            {
                Log.Warn("Could not start Bluetooth watcher", ex);
            }
        }
    }

    private void OnAdded(WatcherState state, string id, string name, bool connected)
    {
        _devices[id] = new DeviceEntry(name, connected);
        // Ignore the initial enumeration burst; only announce devices that appear later.
        if (state.Ready && connected)
        {
            Raise(name, true);
        }
    }

    private void OnUpdated(string id, bool? connected)
    {
        if (connected is not bool now || !_devices.TryGetValue(id, out var entry) || entry.Connected == now)
        {
            return;
        }
        _devices[id] = entry with { Connected = now };
        Raise(entry.Name, now);
    }

    private void Raise(string name, bool connected)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        // Many headsets expose both a classic and an LE endpoint; announce once.
        string key = $"{name}|{connected}";
        var now = DateTime.UtcNow;
        if (_recentEvents.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(4))
        {
            return;
        }
        _recentEvents[key] = now;
        ConnectionChanged?.Invoke(name, connected);
    }

    private static bool ReadConnected(IReadOnlyDictionary<string, object> properties) =>
        properties.TryGetValue(IsConnectedKey, out object? value) && value is bool b && b;

    public void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            try
            {
                if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
                {
                    watcher.Stop();
                }
            }
            catch
            {
                // Ignore during shutdown.
            }
        }
        _watchers.Clear();
    }

    private sealed class WatcherState
    {
        public bool Ready { get; set; }
    }

    private sealed record DeviceEntry(string Name, bool Connected);
}
