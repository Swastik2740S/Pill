using System.IO;
using System.Runtime.InteropServices;
using DynamicIsland.Core;
using Microsoft.Win32;
using Windows.Networking.Connectivity;

namespace DynamicIsland.Services;

/// <summary>
/// Real facts about this PC for the Settings pages, read from documented sources (registry
/// keys Windows itself publishes, Win32 and WinRT). Values that never change are read once.
/// Nothing here is estimated: if a value can't be read, it says so.
/// </summary>
public static class DeviceInfo
{
    private static string? _windows;
    private static string? _processor;
    private static string? _graphics;

    public static string DeviceName => Environment.MachineName;

    public static string AppVersion => typeof(DeviceInfo).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    public static string Windows => _windows ??= ReadWindows();

    public static string Processor => _processor ??= ReadProcessor();

    public static string LogicalProcessors => $"{Environment.ProcessorCount} logical processors";

    public static string Graphics => _graphics ??= ReadGraphics();

    public static string Memory
    {
        get
        {
            string installed = GetPhysicallyInstalledSystemMemory(out ulong kb) && kb > 0
                ? $"{kb / 1024.0 / 1024.0:0.#} GB installed"
                : "Installed size not reported";
            var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            return GlobalMemoryStatusEx(ref status) ? $"{installed}, {status.MemoryLoad}% in use" : installed;
        }
    }

    public static IReadOnlyList<(string Name, string Detail)> Drives()
    {
        var drives = new List<(string, string)>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                {
                    continue;
                }
                string label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local disk" : drive.VolumeLabel;
                double used = 1 - (double)drive.TotalFreeSpace / drive.TotalSize;
                drives.Add(($"{drive.Name.TrimEnd('\\')} {label}",
                    $"{Size(drive.TotalFreeSpace)} free of {Size(drive.TotalSize)} ({used:P0} used)"));
            }
            catch
            {
                // Drive went away; skip it.
            }
        }
        return drives;
    }

    public static string Storage
    {
        get
        {
            var drives = Drives();
            return drives.Count == 0 ? "No fixed drives reported" : string.Join("\n", drives.Select(d => $"{d.Name}: {d.Detail}"));
        }
    }

    public static string Uptime
    {
        get
        {
            var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
            return up.TotalDays >= 1 ? $"{(int)up.TotalDays} d {up.Hours} h"
                : up.TotalHours >= 1 ? $"{(int)up.TotalHours} h {up.Minutes} min"
                : $"{up.Minutes} min";
        }
    }

    /// <summary>"Wi-Fi · HomeNet", "Ethernet", "Not connected".</summary>
    public static string Network
    {
        get
        {
            try
            {
                var profile = NetworkInformation.GetInternetConnectionProfile();
                if (profile is null)
                {
                    return "Not connected";
                }
                string kind = profile.IsWlanConnectionProfile ? "Wi-Fi"
                    : profile.IsWwanConnectionProfile ? "Mobile network"
                    : "Ethernet";
                string name = string.IsNullOrWhiteSpace(profile.ProfileName) ? kind : $"{kind} · {profile.ProfileName}";
                return profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess
                    ? name
                    : $"{name} (no internet)";
            }
            catch (Exception ex)
            {
                Log.Warn("Reading network status failed", ex);
                return "Unknown";
            }
        }
    }

    /// <summary>Primary display mode and Windows scale.</summary>
    public static string Display
    {
        get
        {
            var mode = new DevMode { Size = (short)Marshal.SizeOf<DevMode>() };
            string resolution = EnumDisplaySettings(null, -1 /* current */, ref mode)
                ? $"{mode.PelsWidth} × {mode.PelsHeight} at {mode.DisplayFrequency} Hz"
                : "Mode not reported";
            uint dpi = GetDpiForSystem();
            return dpi > 0 ? $"{resolution}, scale {dpi * 100 / 96}%" : resolution;
        }
    }

    public static string Monitors
    {
        get
        {
            int count = GetSystemMetrics(80 /* SM_CMONITORS */);
            return count == 1 ? "1 display" : $"{count} displays";
        }
    }

    // ------------------------------------------------------------ power

    public static string BatteryHealth
    {
        get
        {
            try
            {
                var report = global::Windows.Devices.Power.Battery.AggregateBattery.GetReport();
                if (report.DesignCapacityInMilliwattHours is int design && design > 0
                    && report.FullChargeCapacityInMilliwattHours is int full && full > 0)
                {
                    return $"{full * 100.0 / design:0}% of original capacity ({full / 1000.0:0.#} of {design / 1000.0:0.#} Wh)";
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Battery report unavailable", ex);
            }
            return "Not reported by this battery";
        }
    }

    public static string PowerDraw
    {
        get
        {
            try
            {
                var report = global::Windows.Devices.Power.Battery.AggregateBattery.GetReport();
                if (report.ChargeRateInMilliwatts is int rate)
                {
                    return rate > 0 ? $"Charging at {rate / 1000.0:0.#} W"
                        : rate < 0 ? $"Using {-rate / 1000.0:0.#} W from the battery"
                        : "Not charging or discharging";
                }
            }
            catch
            {
                // Reported as not available below.
            }
            return "Not reported";
        }
    }

    public static string BatterySaver =>
        global::Windows.System.Power.PowerManager.EnergySaverStatus switch
        {
            global::Windows.System.Power.EnergySaverStatus.On => "On",
            global::Windows.System.Power.EnergySaverStatus.Off => "Off",
            _ => "Not available on this power source",
        };

    // ------------------------------------------------------------ accessibility

    public static string TextScale
    {
        get
        {
            try
            {
                double scale = new global::Windows.UI.ViewManagement.UISettings().TextScaleFactor;
                return $"{scale * 100:0}%";
            }
            catch
            {
                return "Not reported";
            }
        }
    }

    public static string Animations
    {
        get
        {
            try
            {
                return new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled ? "On" : "Off";
            }
            catch
            {
                return "Not reported";
            }
        }
    }

    public static string HighContrast => System.Windows.SystemParameters.HighContrast ? "On" : "Off";

    // ------------------------------------------------------------ readers

    private static string ReadWindows()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string product = key?.GetValue("ProductName") as string ?? "Windows";
            string version = key?.GetValue("DisplayVersion") as string ?? "";
            string build = key?.GetValue("CurrentBuild") as string ?? Environment.OSVersion.Version.Build.ToString();
            string revision = key?.GetValue("UBR") is int ubr ? $".{ubr}" : "";
            // The registry still says "Windows 10" on Windows 11; the build number decides.
            if (int.TryParse(build, out int number) && number >= 22000)
            {
                product = product.Replace("Windows 10", "Windows 11");
            }
            return $"{product}{(version.Length > 0 ? " " + version : "")}, build {build}{revision}";
        }
        catch
        {
            return Environment.OSVersion.VersionString;
        }
    }

    private static string ReadProcessor()
    {
        try
        {
            return (Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                "ProcessorNameString", null) as string)?.Trim() ?? "Not reported";
        }
        catch
        {
            return "Not reported";
        }
    }

    /// <summary>Display adapters from the display device class, skipping virtual ones.</summary>
    private static string ReadGraphics()
    {
        var names = new List<string>();
        try
        {
            using var adapters = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            foreach (string sub in adapters?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                if (sub.Length != 4 || !sub.All(char.IsDigit))
                {
                    continue;
                }
                using var adapter = adapters!.OpenSubKey(sub);
                if (adapter?.GetValue("DriverDesc") is string name
                    && !name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("Remote Display", StringComparison.OrdinalIgnoreCase)
                    && !names.Contains(name))
                {
                    names.Add(name);
                }
            }
        }
        catch
        {
            // Reported as not available below.
        }
        return names.Count == 0 ? "Not reported" : string.Join("\n", names);
    }

    private static string Size(long bytes) =>
        bytes >= 1L << 40 ? $"{bytes / (double)(1L << 40):0.##} TB" : $"{bytes / (double)(1L << 30):0} GB";

    // ------------------------------------------------------------ interop

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public short SpecVersion;
        public short DriverVersion;
        public short Size;
        public short DriverExtra;
        public int Fields;
        public int PositionX;
        public int PositionY;
        public int DisplayOrientation;
        public int DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TTOption;
        public short Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public short LogPixels;
        public int BitsPerPel;
        public int PelsWidth;
        public int PelsHeight;
        public int DisplayFlags;
        public int DisplayFrequency;
        public int IcmMethod;
        public int IcmIntent;
        public int MediaType;
        public int DitherType;
        public int Reserved1;
        public int Reserved2;
        public int PanningWidth;
        public int PanningHeight;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalKilobytes);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNumber, ref DevMode mode);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
