using DynamicIsland.Core;
using Windows.Devices.Radios;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace DynamicIsland.Services;

/// <summary>
/// Checks, without asking for any permission, which system controls this PC actually allows,
/// and logs the result. Later steps enable a control only when its probe passed, so the
/// island never shows a switch that can't work.
/// </summary>
public static class SystemCapabilities
{
    public static bool HasWifiRadio { get; private set; }
    public static bool HasBluetoothRadio { get; private set; }
    public static bool HotspotReadable { get; private set; }

    public static async Task ProbeAsync()
    {
        var results = new List<string>();

        try
        {
            var radios = await Radio.GetRadiosAsync();
            HasWifiRadio = radios.Any(r => r.Kind == RadioKind.WiFi);
            HasBluetoothRadio = radios.Any(r => r.Kind == RadioKind.Bluetooth);
            results.Add("radios " + (radios.Count == 0 ? "none" : string.Join("/", radios.Select(r => $"{r.Kind}={r.State}"))));
        }
        catch (Exception ex)
        {
            results.Add($"radios unavailable ({ex.GetType().Name})");
        }

        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null)
            {
                results.Add("hotspot not checked (offline)");
            }
            else
            {
                var tethering = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
                HotspotReadable = true;
                results.Add($"hotspot {tethering.TetheringOperationalState} ({tethering.ClientCount}/{tethering.MaxClientCount} clients)");
            }
        }
        catch (Exception ex)
        {
            results.Add($"hotspot unavailable ({ex.GetType().Name})");
        }

        try
        {
            var report = global::Windows.Devices.Power.Battery.AggregateBattery.GetReport();
            results.Add($"battery report {report.Status}");
        }
        catch (Exception ex)
        {
            results.Add($"battery report unavailable ({ex.GetType().Name})");
        }

        results.Add("nearby Wi-Fi list off (location access not granted, by choice)");
        Log.Info("Capabilities: " + string.Join("; ", results));
    }
}
