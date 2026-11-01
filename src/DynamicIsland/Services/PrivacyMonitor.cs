using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using DynamicIsland.Core;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace DynamicIsland.Services;

/// <summary>
/// Knows, at any moment, whether an app is using the camera or the microphone, and which.
///
///   Camera       Media Foundation's sensor activity monitor (IMFSensorActivityMonitor), the
///                documented API Windows provides for privacy indicators: it calls back whenever
///                a process starts or stops streaming from any camera. Event driven.
///   Microphone   Core Audio: recording sessions that are active on any microphone right now,
///                checked every 1.5 s on a background thread (a handful of cheap calls).
///                Muted microphones and virtual or loopback inputs (Virtual Audio Cable, VB-Cable,
///                Voicemeeter, Stereo Mix) never count: nothing real is being heard through them.
///   Names        The app's process name, made readable ("WindowsCamera" shows as "Windows
///                Camera"). Other processes are never opened or read.
/// </summary>
public sealed class PrivacyMonitor : ObservableObject, IDisposable
{
    private const int MicPollMs = 1500;

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly ManualResetEvent _stop = new(false);
    private readonly int _ownPid = Environment.ProcessId;
    private Thread? _thread;

    // Camera through Media Foundation (kept alive for the life of the monitor).
    private IMFSensorActivityMonitor? _sensorMonitor;
    private SensorCallback? _sensorCallback;
    private bool _mfStarted;
    private bool _loggedDevices;
    private bool _loggedAudioError;
    private readonly HashSet<string> _loggedSkips = new(StringComparer.OrdinalIgnoreCase);

    // True while at least one physical microphone is present and not muted. The sensor monitor
    // cannot say which input an app records from, so its mic reports only count while this holds.
    private bool _realMicLive;

    private static readonly PropertyKey EnumeratorNameKey = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);
    private static readonly string[] VirtualMarkers =
    {
        "virtual", "vb-audio", "cable output", "voicemeeter", "stereo mix", "loopback",
        "what u hear", "wave out", "steam streaming",
    };

    // Per source, merged on the UI thread.
    private IReadOnlyList<string> _sensorCamera = Array.Empty<string>();
    private IReadOnlyList<string> _sensorMic = Array.Empty<string>();
    private IReadOnlyList<string> _audioMic = Array.Empty<string>();

    private IReadOnlyList<string> _cameraApps = Array.Empty<string>();
    private IReadOnlyList<string> _micApps = Array.Empty<string>();

    public bool CameraInUse => _cameraApps.Count > 0;
    public bool MicInUse => _micApps.Count > 0;
    public bool AnyInUse => CameraInUse || MicInUse;

    public string CameraStatus => CameraInUse ? $"In use by {string.Join(", ", _cameraApps)}" : "Not in use";
    public string MicStatus => MicInUse ? $"In use by {string.Join(", ", _micApps)}" : "Not in use";

    /// <summary>Hover text for the indicator beside the island.</summary>
    public string Tooltip =>
        string.Join("\n", new[]
        {
            CameraInUse ? $"Camera: {string.Join(", ", _cameraApps)}" : null,
            MicInUse ? $"Microphone: {string.Join(", ", _micApps)}" : null,
        }.Where(line => line is not null));

    public void Start()
    {
        StartSensorMonitor();
        _thread = new Thread(Watch) { IsBackground = true, Name = "PrivacyMonitor" };
        _thread.Start();
    }

    public void Dispose()
    {
        _stop.Set();
        _thread?.Join(800);
        try
        {
            _sensorMonitor?.Stop();
        }
        catch
        {
            // Shutting down.
        }
        if (_mfStarted)
        {
            MFShutdown();
        }
    }

    // ------------------------------------------------------------ camera: sensor activity monitor

    private void StartSensorMonitor()
    {
        try
        {
            _mfStarted = MFStartup(0x00020070, 1 /* MFSTARTUP_LITE */) >= 0;
            _sensorCallback = new SensorCallback(this);
            int hr = MFCreateSensorActivityMonitor(_sensorCallback, out _sensorMonitor);
            if (hr < 0 || _sensorMonitor is null)
            {
                Log.Warn($"Privacy indicator: camera monitor unavailable (0x{hr:X8}); using the consent store only");
                return;
            }
            hr = _sensorMonitor.Start();
            Log.Info(hr >= 0
                ? "Privacy indicator: watching cameras through the sensor activity monitor"
                : $"Privacy indicator: camera monitor did not start (0x{hr:X8})");
        }
        catch (Exception ex)
        {
            Log.Warn("Privacy indicator: camera monitor unavailable; using the consent store only", ex);
        }
    }

    /// <summary>Called by Media Foundation on its own thread whenever any sensor's activity changes.</summary>
    private void OnSensorReport(IMFSensorActivitiesReport report)
    {
        var camera = new List<string>();
        var mic = new List<string>();
        var seen = new List<string>();
        try
        {
            report.GetCount(out uint devices);
            for (uint i = 0; i < devices; i++)
            {
                if (report.GetActivityReport(i, out var device) < 0 || device is null)
                {
                    continue;
                }
                try
                {
                    string name = ReadString((b, n) => device.GetFriendlyName(b, n, out _));
                    string link = ReadString((b, n) => device.GetSymbolicLink(b, n, out _)).ToLowerInvariant();
                    // KSCATEGORY_AUDIO in the device path means a microphone; anything else is a camera.
                    bool isMic = link.Contains("6994ad04-93ef-11d0-a3cc-00a0c9223196")
                        || name.Contains("microphone", StringComparison.OrdinalIgnoreCase);
                    seen.Add($"{(isMic ? "mic" : "camera")} '{name}'");

                    device.GetProcessCount(out uint processes);
                    for (uint p = 0; p < processes; p++)
                    {
                        if (device.GetProcessActivity(p, out var activity) < 0 || activity is null)
                        {
                            continue;
                        }
                        try
                        {
                            activity.GetStreamingState(out int streaming);
                            activity.GetProcessId(out uint pid);
                            if (streaming != 0 && pid != _ownPid)
                            {
                                AddOnce(isMic ? mic : camera, ProcessLabel((int)pid));
                            }
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(activity);
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Privacy indicator: reading a camera report failed", ex);
            return;
        }

        if (!_loggedDevices && seen.Count > 0)
        {
            _loggedDevices = true;
            Log.Info($"Privacy indicator: sensors seen: {string.Join(", ", seen)}");
        }
        _dispatcher.InvokeAsync(() =>
        {
            _sensorCamera = camera;
            _sensorMic = mic;
            Merge();
        });
    }

    private static string ReadString(Func<StringBuilder, uint, int> read)
    {
        var buffer = new StringBuilder(512);
        return read(buffer, (uint)buffer.Capacity) >= 0 ? buffer.ToString() : "";
    }

    // ------------------------------------------------------------ microphone (background thread)

    private void Watch()
    {
        MMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = new MMDeviceEnumerator();
        }
        catch (Exception ex)
        {
            Log.Warn("Privacy indicator: Core Audio unavailable for the microphone check", ex);
        }

        while (true)
        {
            var (audio, realMicLive) = ActiveRecorders(enumerator);
            _dispatcher.InvokeAsync(() =>
            {
                _audioMic = audio;
                _realMicLive = realMicLive;
                Merge();
            });

            if (_stop.WaitOne(MicPollMs))
            {
                enumerator?.Dispose();
                return;
            }
        }
    }

    /// <summary>
    /// Apps recording from a real, unmuted microphone right now (active capture sessions), and
    /// whether any real microphone is unmuted at all.
    /// </summary>
    private (List<string> Apps, bool RealMicLive) ActiveRecorders(MMDeviceEnumerator? enumerator)
    {
        var apps = new List<string>();
        bool realMicLive = false;
        if (enumerator is null)
        {
            return (apps, false);
        }
        try
        {
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device)
                {
                    string skip = SkipReason(device);
                    if (skip.Length > 0)
                    {
                        if (_loggedSkips.Add($"{device.FriendlyName}|{skip}"))
                        {
                            Log.Info($"Privacy indicator: ignoring input '{device.FriendlyName}' ({skip})");
                        }
                        continue;
                    }
                    realMicLive = true;

                    var manager = device.AudioSessionManager;
                    manager.RefreshSessions();
                    var sessions = manager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        using var session = sessions[i];
                        if (session.State != AudioSessionState.AudioSessionStateActive || session.IsSystemSoundsSession)
                        {
                            continue;
                        }
                        int pid = (int)session.GetProcessID;
                        if (pid != 0 && pid != _ownPid)
                        {
                            AddOnce(apps, ProcessLabel(pid));
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (!_loggedAudioError)
            {
                _loggedAudioError = true;
                Log.Warn("Privacy indicator: checking microphone sessions failed", ex);
            }
        }
        return (apps, realMicLive);
    }

    /// <summary>Why an input does not count as a live microphone ("" when it does).</summary>
    private static string SkipReason(MMDevice device)
    {
        if (IsVirtual(device))
        {
            return "virtual input";
        }
        try
        {
            var volume = device.AudioEndpointVolume;
            if (volume.Mute)
            {
                return "muted";
            }
            if (volume.MasterVolumeLevelScalar <= 0.001f)
            {
                return "volume at zero";
            }
        }
        catch
        {
            // No volume control: treat it as live.
        }
        return "";
    }

    /// <summary>Software inputs (installed under ROOT, or named like the common virtual cables).</summary>
    private static bool IsVirtual(MMDevice device)
    {
        try
        {
            var props = device.Properties;
            if (props.Contains(EnumeratorNameKey)
                && props[EnumeratorNameKey].Value is string enumeratorName
                && enumeratorName.Equals("ROOT", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        catch
        {
            // Fall back to the names.
        }
        string names = "";
        try
        {
            names = $"{device.FriendlyName} {device.DeviceFriendlyName}";
        }
        catch
        {
        }
        return VirtualMarkers.Any(m => names.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------ names

    // Process names that read badly as they are.
    private static readonly Dictionary<string, string> KnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WindowsCamera"] = "Windows Camera",
        ["msedge"] = "Microsoft Edge",
        ["msedgewebview2"] = "Microsoft Edge",
        ["chrome"] = "Google Chrome",
        ["firefox"] = "Firefox",
        ["brave"] = "Brave",
        ["opera"] = "Opera",
        ["ms-teams"] = "Microsoft Teams",
        ["Teams"] = "Microsoft Teams",
        ["Zoom"] = "Zoom",
        ["Discord"] = "Discord",
        ["obs64"] = "OBS Studio",
        ["SoundRecorder"] = "Sound Recorder",
        ["VoiceRecorder"] = "Voice Recorder",
        ["WhatsApp"] = "WhatsApp",
        ["Telegram"] = "Telegram",
        ["slack"] = "Slack",
        ["python"] = "Python",
        ["pythonw"] = "Python",
    };

    /// <summary>A readable name from the process name alone (no other process is opened or read).</summary>
    private static string ProcessLabel(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            string name = process.ProcessName;
            return KnownNames.TryGetValue(name, out string? known) ? known : Pretty(name);
        }
        catch
        {
            return "An app";
        }
    }

    /// <summary>"WindowsCamera" → "Windows Camera", "spotify" → "Spotify".</summary>
    private static string Pretty(string process)
    {
        if (process.Length == 0)
        {
            return "An app";
        }
        var text = new StringBuilder(process.Length + 4);
        for (int i = 0; i < process.Length; i++)
        {
            char c = process[i];
            if (i > 0 && char.IsUpper(c) && char.IsLower(process[i - 1]))
            {
                text.Append(' ');
            }
            text.Append(i == 0 ? char.ToUpperInvariant(c) : c);
        }
        return text.ToString();
    }

    private static void AddOnce(List<string> list, string name)
    {
        if (!list.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(name);
        }
    }

    // ------------------------------------------------------------ merge (UI thread)

    private void Merge()
    {
        var camera = _sensorCamera.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // With every real microphone muted (or only virtual inputs present) nothing can be heard,
        // so the sensor monitor is not allowed to light the mic mark either.
        var mic = (_realMicLive ? _audioMic.Concat(_sensorMic) : _audioMic)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (camera.SequenceEqual(_cameraApps) && mic.SequenceEqual(_micApps))
        {
            return;
        }
        bool wasCamera = CameraInUse;
        bool wasMic = MicInUse;
        _cameraApps = camera;
        _micApps = mic;

        if (CameraInUse != wasCamera)
        {
            Log.Info(CameraInUse ? $"Privacy indicator: camera in use ({string.Join(", ", camera)})" : "Privacy indicator: camera no longer in use");
        }
        if (MicInUse != wasMic)
        {
            Log.Info(MicInUse ? $"Privacy indicator: microphone in use ({string.Join(", ", mic)})" : "Privacy indicator: microphone no longer in use");
        }

        OnPropertyChanged(nameof(CameraInUse));
        OnPropertyChanged(nameof(MicInUse));
        OnPropertyChanged(nameof(CameraStatus));
        OnPropertyChanged(nameof(MicStatus));
        OnPropertyChanged(nameof(Tooltip));
        OnPropertyChanged(nameof(AnyInUse));
    }

    // ------------------------------------------------------------ interop

    /// <summary>Receives Media Foundation's activity reports and hands them to the monitor.</summary>
    // Public so COM can see it and its interface when Media Foundation calls back.
    [ComVisible(true)]
    public sealed class SensorCallback : IMFSensorActivitiesReportCallback
    {
        private readonly PrivacyMonitor _owner;

        internal SensorCallback(PrivacyMonitor owner) => _owner = owner;

        public int OnActivitiesReport(IMFSensorActivitiesReport report)
        {
            _owner.OnSensorReport(report);
            return 0;
        }
    }

    [ComImport]
    [Guid("DE5072EE-DBE3-46DC-8A87-B6F631194751")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFSensorActivitiesReportCallback
    {
        [PreserveSig] int OnActivitiesReport(IMFSensorActivitiesReport report);
    }

    [ComImport]
    [Guid("D0CEF145-B3F4-4340-A2E5-7A5080CA05CB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFSensorActivityMonitor
    {
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
    }

    [ComImport]
    [Guid("683F7A5E-4A19-43CD-B1A9-DBF4AB3F7777")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFSensorActivitiesReport
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetActivityReport(uint index, out IMFSensorActivityReport report);
        [PreserveSig] int GetActivityReportByDeviceName([MarshalAs(UnmanagedType.LPWStr)] string symbolicName, out IMFSensorActivityReport report);
    }

    [ComImport]
    [Guid("3E8C4BE1-A8C2-4528-90DE-2851BDE5FEAD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFSensorActivityReport
    {
        [PreserveSig] int GetFriendlyName([MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, uint size, out uint written);
        [PreserveSig] int GetSymbolicLink([MarshalAs(UnmanagedType.LPWStr)] StringBuilder link, uint size, out uint written);
        [PreserveSig] int GetProcessCount(out uint count);
        [PreserveSig] int GetProcessActivity(uint index, out IMFSensorProcessActivity activity);
    }

    [ComImport]
    [Guid("39DC7F4A-B141-4719-813C-A7F46162A2B8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFSensorProcessActivity
    {
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int GetStreamingState(out int streaming);
        [PreserveSig] int GetStreamingMode(out int mode);
        [PreserveSig] int GetReportTime(out long fileTime);
    }

    [DllImport("mfsensorgroup.dll")]
    private static extern int MFCreateSensorActivityMonitor(IMFSensorActivitiesReportCallback callback, out IMFSensorActivityMonitor monitor);

    [DllImport("mfplat.dll")]
    private static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll")]
    private static extern int MFShutdown();
}
