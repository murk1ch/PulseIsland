using System.Runtime.InteropServices;
using PulseIsland.Services;
using static PulseIsland.Audio.WasapiComInterop;

namespace PulseIsland.Audio;

public readonly record struct AudioEndpointState(
    int MasterVolumePercent,
    bool IsSpeakerMuted,
    bool IsMicMuted,
    bool HasSpeakerDevice,
    bool HasMicDevice);

/// <summary>
/// Zero-dependency Core Audio monitor tracking default speaker volume/mute and microphone mute state.
/// </summary>
public sealed class AudioEndpointMonitorService : IDisposable
{
    private sealed class VolumeCallback : IAudioEndpointVolumeCallback
    {
        private readonly Action _onChanged;
        public VolumeCallback(Action onChanged) => _onChanged = onChanged;
        public int OnNotify(IntPtr pNotifyData)
        {
            _onChanged();
            return 0;
        }
    }

    private IMMDeviceEnumerator? _enumerator;
    private IAudioEndpointVolume? _renderVolume;
    private IAudioEndpointVolume? _captureVolume;
    private VolumeCallback? _renderCallback;
    private VolumeCallback? _captureCallback;
    private bool _disposed;

    public AudioEndpointState CurrentState { get; private set; } = new(50, false, false, false, false);

    public event Action<AudioEndpointState, bool>? StateChanged;

    public void Start()
    {
        RefreshDevices();
    }

    public void RefreshDevices()
    {
        DetachEndpoints();

        try
        {
            _enumerator ??= (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

            if (_enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var renderDev) == 0 && renderDev is not null)
            {
                Guid iid = IID_IAudioEndpointVolume;
                if (renderDev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var volObj) == 0 && volObj is IAudioEndpointVolume vol)
                {
                    _renderVolume = vol;
                    _renderCallback = new VolumeCallback(() => { if (!_disposed) UpdateSnapshot(raiseHudToast: true); });
                    _renderVolume.RegisterControlChangeNotify(_renderCallback);
                }
                Marshal.ReleaseComObject(renderDev);
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"AudioEndpointMonitor render attach failed: {ex.Message}");
            _renderVolume = null;
        }

        try
        {
            if (_enumerator is not null)
            {
                IMMDevice? captureDev = null;
                if (_enumerator.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eCommunications, out captureDev) != 0 || captureDev is null)
                {
                    _enumerator.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eMultimedia, out captureDev);
                }

                if (captureDev is not null)
                {
                    Guid iid = IID_IAudioEndpointVolume;
                    if (captureDev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var volObj) == 0 && volObj is IAudioEndpointVolume vol)
                    {
                        _captureVolume = vol;
                        _captureCallback = new VolumeCallback(() => { if (!_disposed) UpdateSnapshot(raiseHudToast: true); });
                        _captureVolume.RegisterControlChangeNotify(_captureCallback);
                    }
                    Marshal.ReleaseComObject(captureDev);
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"AudioEndpointMonitor capture attach failed: {ex.Message}");
            _captureVolume = null;
        }

        UpdateSnapshot(raiseHudToast: false);
    }

    public void ToggleSpeakerMute()
    {
        try
        {
            if (_renderVolume is null)
                RefreshDevices();

            if (_renderVolume is not null && _renderVolume.GetMute(out bool muted) == 0)
            {
                Guid ctx = Guid.Empty;
                _renderVolume.SetMute(!muted, ref ctx);
                UpdateSnapshot(raiseHudToast: true);
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"ToggleSpeakerMute error: {ex.Message}");
        }
    }

    public void ToggleMicMute()
    {
        try
        {
            if (_captureVolume is null)
                RefreshDevices();

            if (_captureVolume is not null && _captureVolume.GetMute(out bool muted) == 0)
            {
                Guid ctx = Guid.Empty;
                _captureVolume.SetMute(!muted, ref ctx);
                UpdateSnapshot(raiseHudToast: true);
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"ToggleMicMute error: {ex.Message}");
        }
    }

    private void UpdateSnapshot(bool raiseHudToast)
    {
        int volPercent = 0;
        bool speakerMuted = false;
        bool hasSpeaker = _renderVolume is not null;
        if (_renderVolume is not null)
        {
            try
            {
                if (_renderVolume.GetMasterVolumeLevelScalar(out float scalar) == 0)
                    volPercent = (int)Math.Round(scalar * 100.0f);
                if (_renderVolume.GetMute(out bool m) == 0)
                    speakerMuted = m;
            }
            catch { hasSpeaker = false; }
        }

        bool micMuted = false;
        bool hasMic = _captureVolume is not null;
        if (_captureVolume is not null)
        {
            try
            {
                if (_captureVolume.GetMute(out bool m) == 0)
                    micMuted = m;
            }
            catch { hasMic = false; }
        }

        CurrentState = new AudioEndpointState(volPercent, speakerMuted, micMuted, hasSpeaker, hasMic);
        StateChanged?.Invoke(CurrentState, raiseHudToast);
    }

    private void DetachEndpoints()
    {
        if (_renderVolume is not null)
        {
            try
            {
                if (_renderCallback is not null)
                    _renderVolume.UnregisterControlChangeNotify(_renderCallback);
                Marshal.ReleaseComObject(_renderVolume);
            }
            catch { }
            _renderVolume = null;
            _renderCallback = null;
        }

        if (_captureVolume is not null)
        {
            try
            {
                if (_captureCallback is not null)
                    _captureVolume.UnregisterControlChangeNotify(_captureCallback);
                Marshal.ReleaseComObject(_captureVolume);
            }
            catch { }
            _captureVolume = null;
            _captureCallback = null;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        DetachEndpoints();
        if (_enumerator is not null)
        {
            try { Marshal.ReleaseComObject(_enumerator); } catch { }
            _enumerator = null;
        }
    }
}
