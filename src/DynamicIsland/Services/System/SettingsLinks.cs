using System.Diagnostics;
using DynamicIsland.Core;

namespace DynamicIsland.Services;

/// <summary>
/// The Windows Settings pages the island may open, as a fixed allowlist. Nothing else can be
/// launched through here: no shell commands, no URLs built from user or web input.
/// </summary>
public static class SettingsLinks
{
    private static readonly Dictionary<string, string> Pages = new(StringComparer.Ordinal)
    {
        ["wifi"] = "ms-settings:network-wifi",
        ["networks"] = "ms-availablenetworks:",
        ["network"] = "ms-settings:network-status",
        ["bluetooth"] = "ms-settings:bluetooth",
        ["airplane"] = "ms-settings:network-airplanemode",
        ["hotspot"] = "ms-settings:network-mobilehotspot",
        ["vpn"] = "ms-settings:network-vpn",
        ["sound"] = "ms-settings:sound",
        ["sound-devices"] = "ms-settings:sound-devices",
        ["app-volume"] = "ms-settings:apps-volume",
        ["display"] = "ms-settings:display",
        ["nightlight"] = "ms-settings:nightlight",
        ["wallpaper"] = "ms-settings:personalization-background",
        ["colors"] = "ms-settings:colors",
        ["power"] = "ms-settings:powersleep",
        ["batterysaver"] = "ms-settings:batterysaver",
        ["notifications"] = "ms-settings:notifications",
        ["focus"] = "ms-settings:focus",
        ["startup"] = "ms-settings:startupapps",
        ["privacy-camera"] = "ms-settings:privacy-webcam",
        ["privacy-mic"] = "ms-settings:privacy-microphone",
        ["privacy-location"] = "ms-settings:privacy-location",
        ["privacy-notifications"] = "ms-settings:privacy-notifications",
        ["text-size"] = "ms-settings:easeofaccess-display",
        ["contrast"] = "ms-settings:easeofaccess-highcontrast",
        ["visual-effects"] = "ms-settings:easeofaccess-visualeffects",
        ["about"] = "ms-settings:about",
        ["storage"] = "ms-settings:storagesense",
    };

    public static bool Has(string key) => Pages.ContainsKey(key);

    public static void Open(string key)
    {
        if (!Pages.TryGetValue(key, out string? uri))
        {
            Log.Warn($"Refused to open unknown settings page '{key}'");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open {uri}", ex);
        }
    }
}
