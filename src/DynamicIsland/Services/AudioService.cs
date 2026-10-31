using System.Windows.Threading;
using DynamicIsland.Core;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace DynamicIsland.Services;

/// <summary>
/// System output volume (push notifications from Core Audio, no polling) and the live
/// peak meter used by the waveform bars. Follows the default output device when it changes.
/// Volume and mute are observable for the music card's slider, and settable from it.
/// </summary>
public sealed class AudioService : ObservableObject, IMMNotificationClient, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private bool _registered;
    private float _lastVolume = -1;
    private bool _lastMuted;

    private double _volume;
    private bool _isMuted;
    private bool _hasDevice;

    public AudioService(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>Master volume of the default output, 0..1.</summary>
    public double Volume { get => _volume; private set => Set(ref _volume, value); }
    public bool IsMuted { get => _isMuted; private set => Set(ref _isMuted, value); }
    public bool HasDevice { get => _hasDevice; private set => Set(ref _hasDevice, value); }

    private string _deviceName = "";
    /// <summary>Friendly name of the default output ("Speakers (Realtek(R) Audio)").</summary>
    public string DeviceName { get => _deviceName; private set => Set(ref _deviceName, value); }

    /// <summary>Set the master volume (0..1). Moving it up also unmutes, like the Windows flyout.</summary>
    public void SetVolume(double level)
    {
        if (_device is null)
        {
            return;
        }
        try
        {
            float value = (float)Math.Clamp(level, 0, 1);
            var endpoint = _device.AudioEndpointVolume;
            endpoint.MasterVolumeLevelScalar = value;
            if (endpoint.Mute && value > 0.001f)
            {
                endpoint.Mute = false;
            }
            Volume = value;
        }
        catch (Exception ex)
        {
            Log.Warn("Setting volume failed", ex);
        }
    }

    public void ToggleMute()
    {
        if (_device is null)
        {
            return;
        }
        try
        {
            var endpoint = _device.AudioEndpointVolume;
            endpoint.Mute = !endpoint.Mute;
            IsMuted = endpoint.Mute;
        }
        catch (Exception ex)
        {
            Log.Warn("Toggling mute failed", ex);
        }
    }

    /// <summary>Raised on the UI thread with (volume 0..1, muted).</summary>
    public event Action<float, bool>? VolumeChanged;

    public void Initialize()
    {
        try
        {
            _enumerator = new MMDeviceEnumerator();
            _enumerator.RegisterEndpointNotificationCallback(this);
            _registered = true;
            AttachDefaultDevice();
        }
        catch (Exception ex)
        {
            Log.Error("Core Audio unavailable", ex);
        }
    }

    /// <summary>Current output peak (0..1). Cheap COM call, safe to call at 30 Hz.</summary>
    public float GetPeak()
    {
        try
        {
            return _device?.AudioMeterInformation.MasterPeakValue ?? 0f;
        }
        catch
        {
            return 0f;
        }
    }

    private void AttachDefaultDevice()
    {
        DetachDevice();
        if (_enumerator is null)
        {
            return;
        }

        try
        {
            _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var volume = _device.AudioEndpointVolume;
            _lastVolume = volume.MasterVolumeLevelScalar;
            _lastMuted = volume.Mute;
            Volume = _lastVolume;
            IsMuted = _lastMuted;
            HasDevice = true;
            DeviceName = _device.FriendlyName;
            volume.OnVolumeNotification += OnVolumeNotification;
            Log.Info($"Audio: following '{_device.FriendlyName}'");
        }
        catch (Exception ex)
        {
            // No output device (e.g. headphones unplugged on a desktop). Not an error.
            Log.Warn("No default audio endpoint", ex);
            _device = null;
            HasDevice = false;
            DeviceName = "";
        }
    }

    private void DetachDevice()
    {
        if (_device is null)
        {
            return;
        }
        try
        {
            _device.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification;
        }
        catch
        {
            // Device already gone.
        }
        _device.Dispose();
        _device = null;
    }

    // Called on a Core Audio worker thread.
    private void OnVolumeNotification(AudioVolumeNotificationData data)
    {
        float volume = data.MasterVolume;
        bool muted = data.Muted;
        _dispatcher.InvokeAsync(() =>
        {
            // Notifications also fire for per-channel changes; only react to what the user sees.
            if (Math.Abs(volume - _lastVolume) < 0.004f && muted == _lastMuted)
            {
                return;
            }
            _lastVolume = volume;
            _lastMuted = muted;
            Volume = volume;
            IsMuted = muted;
            VolumeChanged?.Invoke(volume, muted);
        });
    }

    // ------------------------------------------------ IMMNotificationClient (worker thread)

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && role == Role.Multimedia)
        {
            _dispatcher.InvokeAsync(AttachDefaultDevice);
        }
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }

    public void OnDeviceAdded(string pwstrDeviceId) { }

    public void OnDeviceRemoved(string deviceId) { }

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        DetachDevice();
        if (_registered && _enumerator is not null)
        {
            try
            {
                _enumerator.UnregisterEndpointNotificationCallback(this);
            }
            catch
            {
                // Ignore during shutdown.
            }
        }
        _enumerator?.Dispose();
        _enumerator = null;
    }
}
