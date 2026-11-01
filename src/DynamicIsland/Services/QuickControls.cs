using System.Collections.ObjectModel;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using DynamicIsland.Core;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using Windows.Devices.Radios;
using Windows.Networking.Connectivity;

namespace DynamicIsland.Services;

/// <summary>One audio output or input device in the Controls card's pickers.</summary>
public sealed class AudioEndpoint : ObservableObject
{
    private bool _isDefault;

    public AudioEndpoint(string id, string name)
    {
        Id = id;
        Name = name;
    }

    public string Id { get; }
    public string Name { get; }
    public bool IsDefault { get => _isDefault; set => Set(ref _isDefault, value); }
}

/// <summary>
/// The Controls card: switches that act directly, without opening Windows Settings.
///
///   Wi-Fi and Bluetooth   Windows.Devices.Radios (documented; Windows may ask once)
///   Output and input      default device switching through IPolicyConfig (undocumented but
///                         stable since Windows 7, the same call the Windows sound flyout makes)
///   Microphone mute       Core Audio
///   Brightness            WMI monitor brightness (laptop screens)
///   Dark mode             shown from the Personalize values (read only); the tile opens
///                         Windows' colour settings, so Windows itself makes the change
///   Do not disturb        the island's own: messages still arrive as bubbles, but don't pop up
///
/// Anything Windows refuses falls back to opening the matching Windows Settings page.
/// </summary>
public sealed class QuickControls : ObservableObject
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly object _brightnessGate = new();
    private Radio? _wifi;
    private Radio? _bluetooth;
    private int _pendingBrightness = -1;
    private bool _brightnessWorker;

    private bool _hasWifi;
    private bool _wifiOn;
    private string _wifiStatus = "";
    private bool _hasBluetooth;
    private bool _bluetoothOn;
    private string _bluetoothStatus = "";
    private bool _doNotDisturb;
    private bool _darkMode;
    private bool _hasBrightness;
    private double _brightness;
    private bool _hasMic;
    private bool _micMuted;
    private string _outputName = "";
    private string _inputName = "";
    private bool _outputsOpen;
    private bool _inputsOpen;
    private string _message = "";

    public bool HasWifi { get => _hasWifi; private set => Set(ref _hasWifi, value); }
    public bool WifiOn { get => _wifiOn; private set => Set(ref _wifiOn, value); }
    public string WifiStatus
    {
        get => _wifiStatus;
        private set
        {
            if (Set(ref _wifiStatus, value))
            {
                OnPropertyChanged(nameof(WifiCaption));
            }
        }
    }

    /// <summary>Under the round toggle: the network's name while connected, otherwise just "Wi-Fi".</summary>
    public string WifiCaption => _wifiOn && _wifiStatus is not ("On" or "Off" or "Not available" or "") ? _wifiStatus : "Wi-Fi";
    public bool HasBluetooth { get => _hasBluetooth; private set => Set(ref _hasBluetooth, value); }
    public bool BluetoothOn { get => _bluetoothOn; private set => Set(ref _bluetoothOn, value); }
    public string BluetoothStatus { get => _bluetoothStatus; private set => Set(ref _bluetoothStatus, value); }

    /// <summary>The island's own Do Not Disturb: new messages wait as bubbles instead of popping up.</summary>
    public bool DoNotDisturb
    {
        get => _doNotDisturb;
        set
        {
            if (Set(ref _doNotDisturb, value))
            {
                OnPropertyChanged(nameof(DoNotDisturbStatus));
                Log.Info($"Quick controls: island do not disturb {(value ? "on" : "off")}");
            }
        }
    }

    public string DoNotDisturbStatus => _doNotDisturb ? "On" : "Off";

    public bool DarkMode
    {
        get => _darkMode;
        private set
        {
            if (Set(ref _darkMode, value))
            {
                OnPropertyChanged(nameof(DarkModeStatus));
            }
        }
    }

    public string DarkModeStatus => _darkMode ? "On" : "Off";

    public bool HasBrightness { get => _hasBrightness; private set => Set(ref _hasBrightness, value); }
    public double Brightness { get => _brightness; private set => Set(ref _brightness, value); }

    public bool HasMic { get => _hasMic; private set => Set(ref _hasMic, value); }

    public bool MicMuted
    {
        get => _micMuted;
        private set
        {
            if (Set(ref _micMuted, value))
            {
                OnPropertyChanged(nameof(InputStatus));
            }
        }
    }

    public ObservableCollection<AudioEndpoint> Outputs { get; } = new();
    public ObservableCollection<AudioEndpoint> Inputs { get; } = new();

    public string OutputName { get => _outputName; private set => Set(ref _outputName, value); }

    public string InputName
    {
        get => _inputName;
        private set
        {
            if (Set(ref _inputName, value))
            {
                OnPropertyChanged(nameof(InputStatus));
            }
        }
    }

    public string InputStatus => !_hasMic ? "No microphone" : _micMuted ? $"Muted · {_inputName}" : _inputName;

    public bool OutputsOpen { get => _outputsOpen; set => Set(ref _outputsOpen, value); }
    public bool InputsOpen { get => _inputsOpen; set => Set(ref _inputsOpen, value); }

    /// <summary>A short note when Windows refused something (shown under the controls).</summary>
    public string Message
    {
        get => _message;
        private set
        {
            if (Set(ref _message, value))
            {
                OnPropertyChanged(nameof(HasMessage));
            }
        }
    }

    public bool HasMessage => _message.Length > 0;

    // ------------------------------------------------------------ lifecycle

    public async Task InitializeAsync()
    {
        try
        {
            var radios = await Radio.GetRadiosAsync();
            _wifi = radios.FirstOrDefault(r => r.Kind == RadioKind.WiFi);
            _bluetooth = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            if (_wifi is not null)
            {
                _wifi.StateChanged += (_, _) => _dispatcher.InvokeAsync(RefreshRadios);
            }
            if (_bluetooth is not null)
            {
                _bluetooth.StateChanged += (_, _) => _dispatcher.InvokeAsync(RefreshRadios);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Quick controls: radios unavailable", ex);
        }
        NetworkInformation.NetworkStatusChanged += _ => _dispatcher.InvokeAsync(RefreshRadios);
        Refresh();
    }

    /// <summary>Re-read everything (the card calls this when it opens).</summary>
    public void Refresh()
    {
        Message = "";
        RefreshRadios();
        RefreshAudio();
        DarkMode = ReadDarkMode();
        Task.Run(() =>
        {
            int? level = ReadBrightness();
            _dispatcher.InvokeAsync(() =>
            {
                HasBrightness = level is not null;
                if (level is { } value)
                {
                    Brightness = value / 100.0;
                }
            });
        });
    }

    // ------------------------------------------------------------ Wi-Fi and Bluetooth

    public async void ToggleWifi() => await SetRadioAsync(_wifi, !WifiOn, "Wi-Fi", "wifi");

    public async void ToggleBluetooth() => await SetRadioAsync(_bluetooth, !BluetoothOn, "Bluetooth", "bluetooth");

    private async Task SetRadioAsync(Radio? radio, bool on, string label, string page)
    {
        Message = "";
        if (radio is null)
        {
            SettingsLinks.Open(page);
            return;
        }
        try
        {
            var access = await Radio.RequestAccessAsync();
            var result = access == RadioAccessStatus.Allowed
                ? await radio.SetStateAsync(on ? RadioState.On : RadioState.Off)
                : access;
            if (result != RadioAccessStatus.Allowed)
            {
                Log.Warn($"Quick controls: Windows refused to switch {label} ({result})");
                Message = $"Windows didn't let the island switch {label}, so its settings opened instead.";
                SettingsLinks.Open(page);
            }
            else
            {
                Log.Info($"Quick controls: {label} {(on ? "on" : "off")}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Quick controls: switching {label} failed", ex);
            Message = $"Couldn't switch {label}, so its settings opened instead.";
            SettingsLinks.Open(page);
        }
        RefreshRadios();
    }

    private void RefreshRadios()
    {
        HasWifi = _wifi is not null;
        WifiOn = _wifi?.State == RadioState.On;
        WifiStatus = !HasWifi ? "Not available" : !WifiOn ? "Off" : WifiNetworkName();
        HasBluetooth = _bluetooth is not null;
        BluetoothOn = _bluetooth?.State == RadioState.On;
        BluetoothStatus = !HasBluetooth ? "Not available" : BluetoothOn ? "On" : "Off";
    }

    private static string WifiNetworkName()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is not null && profile.IsWlanConnectionProfile && !string.IsNullOrWhiteSpace(profile.ProfileName))
            {
                return profile.ProfileName;
            }
        }
        catch
        {
            // Fall through to the plain state.
        }
        return "On";
    }

    // ------------------------------------------------------------ sound devices

    public void RefreshAudio()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            Fill(Outputs, enumerator, DataFlow.Render);
            Fill(Inputs, enumerator, DataFlow.Capture);
            try
            {
                using var mic = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
                MicMuted = mic.AudioEndpointVolume.Mute;
                HasMic = true;
            }
            catch
            {
                HasMic = false; // no microphone connected
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Quick controls: listing audio devices failed", ex);
        }
        OutputName = Outputs.FirstOrDefault(d => d.IsDefault)?.Name ?? "No output device";
        InputName = Inputs.FirstOrDefault(d => d.IsDefault)?.Name ?? "No microphone";
        OnPropertyChanged(nameof(InputStatus));
    }

    private static void Fill(ObservableCollection<AudioEndpoint> list, MMDeviceEnumerator enumerator, DataFlow flow)
    {
        string? defaultId = null;
        try
        {
            using var device = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
            defaultId = device.ID;
        }
        catch
        {
            // None of this kind connected.
        }

        list.Clear();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device)
            {
                list.Add(new AudioEndpoint(device.ID, device.FriendlyName) { IsDefault = device.ID == defaultId });
            }
        }
    }

    /// <summary>Make a device the default for everything (music, calls, system sounds).</summary>
    public void SetDefault(AudioEndpoint endpoint)
    {
        Message = "";
        object? client = null;
        try
        {
            client = new PolicyConfigClient();
            var policy = (IPolicyConfig)client;
            for (int role = 0; role <= 2; role++) // console, multimedia, communications
            {
                Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(endpoint.Id, role));
            }
            Log.Info($"Quick controls: default device is now '{endpoint.Name}'");
        }
        catch (Exception ex)
        {
            Log.Warn($"Quick controls: switching to '{endpoint.Name}' failed", ex);
            Message = "Couldn't switch the device, so Windows sound settings opened instead.";
            SettingsLinks.Open("sound-devices");
        }
        finally
        {
            if (client is not null && Marshal.IsComObject(client))
            {
                Marshal.ReleaseComObject(client);
            }
        }
        OutputsOpen = false;
        InputsOpen = false;
        RefreshAudio();
    }

    public void ToggleMicMute()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var mic = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            var volume = mic.AudioEndpointVolume;
            volume.Mute = !volume.Mute;
            MicMuted = volume.Mute;
            HasMic = true;
            Log.Info($"Quick controls: microphone {(MicMuted ? "muted" : "on")}");
        }
        catch (Exception ex)
        {
            Log.Warn("Quick controls: microphone mute failed", ex);
            HasMic = false;
        }
        OnPropertyChanged(nameof(InputStatus));
    }

    // ------------------------------------------------------------ brightness

    /// <summary>Set screen brightness (0..1). Writes happen off the UI thread, newest value wins.</summary>
    public void SetBrightness(double level)
    {
        level = Math.Clamp(level, 0, 1);
        Brightness = level;
        lock (_brightnessGate)
        {
            _pendingBrightness = (int)Math.Round(level * 100);
            if (_brightnessWorker)
            {
                return;
            }
            _brightnessWorker = true;
        }
        Task.Run(() =>
        {
            while (true)
            {
                int value;
                lock (_brightnessGate)
                {
                    value = _pendingBrightness;
                    _pendingBrightness = -1;
                    if (value < 0)
                    {
                        _brightnessWorker = false;
                        return;
                    }
                }
                WriteBrightness(value);
            }
        });
    }

    private static int? ReadBrightness()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    return Convert.ToInt32(item["CurrentBrightness"]);
                }
            }
        }
        catch
        {
            // External monitors and desktops don't offer this.
        }
        return null;
    }

    private static void WriteBrightness(int percent)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject item in searcher.Get())
            {
                using (item)
                {
                    item.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)Math.Clamp(percent, 0, 100) });
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Quick controls: setting brightness failed", ex);
        }
    }

    // ------------------------------------------------------------ dark mode

    /// <summary>
    /// Opens Windows' colour settings, where light or dark is one click. The island only reads
    /// the current mode; it never writes Windows' theme settings itself.
    /// </summary>
    public void OpenDarkModeSettings()
    {
        Log.Info("Quick controls: opened Windows colour settings for dark mode");
        SettingsLinks.Open("colors");
    }

    private static bool ReadDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------ interop

    [ComImport]
    [Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient
    {
    }

    [ComImport]
    [Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int useDefault, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int useDefault, IntPtr defaultPeriod, IntPtr minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }
}
