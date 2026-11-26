using System.Diagnostics;
using System.Runtime.InteropServices;
using PulseIsland.Services;
using static PulseIsland.Audio.WasapiComInterop;

namespace PulseIsland.Audio;

public readonly record struct AudioEndpointState(
    int MasterVolumePercent,
    bool IsSpeakerMuted,
    bool IsMicMuted,
    bool HasSpeakerDevice,
    bool HasMicDevice,
    bool IsMicActivelyCapturing = false,
    string ActiveMicAppName = "",
    string CurrentRenderShortName = "",
    string CurrentRenderFullName = "",
    int RenderDeviceCount = 0);

/// <summary>
/// Zero-dependency Core Audio monitor tracking default speaker volume/mute, active microphone capture sessions,
/// real render device names, and 1-click default audio output switching via IPolicyConfig.
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

    [ComVisible(true)]
    private sealed class EndpointChangeClient : IMMNotificationClient
    {
        private readonly AudioEndpointMonitorService _owner;
        public EndpointChangeClient(AudioEndpointMonitorService owner) => _owner = owner;
        public void OnDeviceStateChanged(string pwstrDeviceId, uint dwNewState) => _owner.ScheduleDeviceRefresh();
        public void OnDeviceAdded(string pwstrDeviceId) => _owner.ScheduleDeviceRefresh();
        public void OnDeviceRemoved(string pwstrDeviceId) => _owner.ScheduleDeviceRefresh();
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
        public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, string pwstrDefaultDeviceId)
        {
            if (role is ERole.eMultimedia or ERole.eCommunications)
                _owner.ScheduleDeviceRefresh();
        }
    }

    private readonly object _syncLock = new();
    private readonly object _stateLock = new();
    private AudioEndpointState _currentState = new(50, false, false, false, false);
    private IMMDeviceEnumerator? _enumerator;
    private EndpointChangeClient? _endpointChangeClient;
    private IAudioEndpointVolume? _renderVolume;
    private IAudioEndpointVolume? _captureVolume;
    private VolumeCallback? _renderCallback;
    private VolumeCallback? _captureCallback;
    private Timer? _micSessionPollTimer;
    private int _refreshScheduled;
    private volatile bool _isMicActivelyCapturing;
    private volatile string _activeMicAppName = string.Empty;
    private volatile string _currentRenderShortName = string.Empty;
    private volatile string _currentRenderFullName = string.Empty;
    private volatile int _renderDeviceCount;
    private volatile bool _disposed;

    public AudioEndpointState CurrentState
    {
        get { lock (_stateLock) return _currentState; }
        private set { lock (_stateLock) _currentState = value; }
    }

    public event Action<AudioEndpointState, bool>? StateChanged;

    public void Start()
    {
        if (_disposed) return;
        // Initialize Core Audio COM on MTA thread pool so UI STA never marshals/deadlocks
        _ = Task.Run(() =>
        {
            if (!_disposed)
                RefreshDevices();
        });
        _micSessionPollTimer ??= new Timer(_ => PollActiveCaptureSessionsSafe(), null, 900, 1800);
    }

    private void ScheduleDeviceRefresh()
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _refreshScheduled, 1) != 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(180).ConfigureAwait(false);
                Interlocked.Exchange(ref _refreshScheduled, 0);
                if (!_disposed)
                    RefreshDevices();
            }
            catch
            {
                Interlocked.Exchange(ref _refreshScheduled, 0);
            }
        });
    }

    public void RefreshDevices()
    {
        if (_disposed) return;

        // Swap out and unregister old COM callbacks OUTSIDE _syncLock to guarantee zero AB-BA deadlock
        // with AudioSes.dll's synchronous VolumeCallback.OnNotify RPC thread.
        DetachEndpointsSafe();

        IMMDeviceEnumerator? localEnum = null;
        IAudioEndpointVolume? newRenderVol = null;
        VolumeCallback? newRenderCb = null;
        IAudioEndpointVolume? newCaptureVol = null;
        VolumeCallback? newCaptureCb = null;

        try
        {
            lock (_syncLock)
            {
                if (_disposed) return;
                if (_enumerator is null)
                {
                    _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                    _endpointChangeClient = new EndpointChangeClient(this);
                    try { _enumerator.RegisterEndpointNotificationCallback(_endpointChangeClient); } catch { }
                }
            }

            localEnum = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

            if (localEnum.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var renderDev) == 0 && renderDev is not null)
            {
                try
                {
                    var (fullName, shortName) = WasapiAudioCaptureService.ReadDeviceNames(renderDev, 0);
                    _currentRenderFullName = fullName;
                    _currentRenderShortName = shortName;

                    Guid iid = IID_IAudioEndpointVolume;
                    if (renderDev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var volObj) == 0 && volObj is IAudioEndpointVolume vol)
                    {
                        newRenderVol = vol;
                        newRenderCb = new VolumeCallback(() =>
                        {
                            if (!_disposed)
                                _ = Task.Run(() => { if (!_disposed) UpdateSnapshot(raiseHudToast: true); });
                        });
                        newRenderVol.RegisterControlChangeNotify(newRenderCb);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(renderDev);
                }
            }
            else
            {
                _currentRenderFullName = string.Empty;
                _currentRenderShortName = string.Empty;
            }

            if (localEnum.EnumAudioEndpoints(EDataFlow.eRender, 0x00000001 /* ACTIVE */, out var col) == 0 && col is not null)
            {
                try
                {
                    if (col.GetCount(out uint cnt) == 0)
                        _renderDeviceCount = (int)cnt;
                }
                finally
                {
                    Marshal.ReleaseComObject(col);
                }
            }

            IMMDevice? captureDev = null;
            if (localEnum.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eCommunications, out captureDev) != 0 || captureDev is null)
            {
                localEnum.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eMultimedia, out captureDev);
            }

            if (captureDev is not null)
            {
                try
                {
                    Guid iid = IID_IAudioEndpointVolume;
                    if (captureDev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var volObj) == 0 && volObj is IAudioEndpointVolume vol)
                    {
                        newCaptureVol = vol;
                        newCaptureCb = new VolumeCallback(() =>
                        {
                            if (!_disposed)
                                _ = Task.Run(() => { if (!_disposed) UpdateSnapshot(raiseHudToast: true); });
                        });
                        newCaptureVol.RegisterControlChangeNotify(newCaptureCb);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(captureDev);
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"AudioEndpointMonitor refresh failed: {ex.Message}");
        }
        finally
        {
            if (localEnum is not null)
            {
                try { Marshal.ReleaseComObject(localEnum); } catch { }
            }
        }

        if (_disposed)
        {
            ReleaseVolumeEndpoint(newRenderVol, newRenderCb);
            ReleaseVolumeEndpoint(newCaptureVol, newCaptureCb);
            return;
        }

        IAudioEndpointVolume? prevRender = null;
        VolumeCallback? prevRenderCb = null;
        IAudioEndpointVolume? prevCapture = null;
        VolumeCallback? prevCaptureCb = null;
        lock (_syncLock)
        {
            prevRender = _renderVolume;
            prevRenderCb = _renderCallback;
            prevCapture = _captureVolume;
            prevCaptureCb = _captureCallback;

            _renderVolume = newRenderVol;
            _renderCallback = newRenderCb;
            _captureVolume = newCaptureVol;
            _captureCallback = newCaptureCb;
        }
        ReleaseVolumeEndpoint(prevRender, prevRenderCb);
        ReleaseVolumeEndpoint(prevCapture, prevCaptureCb);

        UpdateSnapshot(raiseHudToast: false);
    }

    public (bool Switched, string FullName, string ShortName) CycleDefaultRenderDevice()
    {
        try
        {
            var devices = WasapiAudioCaptureService.GetRenderDevicesDetailed();
            if (devices.Count == 0)
                return (false, string.Empty, string.Empty);

            if (devices.Count == 1)
                return (false, devices[0].FullName, devices[0].ShortName);

            int currentIdx = -1;
            for (int i = 0; i < devices.Count; i++)
            {
                if (devices[i].IsDefault)
                {
                    currentIdx = i;
                    break;
                }
            }

            var next = devices[(currentIdx + 1) % devices.Count];
            object? policyCom = null;
            try
            {
                policyCom = new PolicyConfigClientComObject();
                var policy = (IPolicyConfig)policyCom;
                int hr1 = policy.SetDefaultEndpoint(next.Id, ERole.eConsole);
                int hr2 = policy.SetDefaultEndpoint(next.Id, ERole.eMultimedia);
                int hr3 = policy.SetDefaultEndpoint(next.Id, ERole.eCommunications);
                if (hr1 != 0 && hr2 != 0 && hr3 != 0)
                {
                    LogService.Warn($"IPolicyConfig.SetDefaultEndpoint returned hr=0x{hr2:X8}");
                    return (false, next.FullName, next.ShortName);
                }
            }
            finally
            {
                if (policyCom is not null)
                {
                    try { Marshal.ReleaseComObject(policyCom); } catch { }
                }
            }

            _currentRenderFullName = next.FullName;
            _currentRenderShortName = next.ShortName;
            _renderDeviceCount = devices.Count;
            RefreshDevices();
            return (true, next.FullName, next.ShortName);
        }
        catch (Exception ex)
        {
            LogService.Warn($"CycleDefaultRenderDevice error: {ex.Message}");
            return (false, string.Empty, string.Empty);
        }
    }

    public void ToggleSpeakerMute()
    {
        try
        {
            bool toggled = false;
            lock (_syncLock)
            {
                if (_renderVolume is not null && _renderVolume.GetMute(out bool muted) == 0)
                {
                    Guid ctx = Guid.Empty;
                    _renderVolume.SetMute(!muted, ref ctx);
                    toggled = true;
                }
            }
            if (!toggled)
            {
                RefreshDevices();
                lock (_syncLock)
                {
                    if (_renderVolume is not null && _renderVolume.GetMute(out bool muted) == 0)
                    {
                        Guid ctx = Guid.Empty;
                        _renderVolume.SetMute(!muted, ref ctx);
                        toggled = true;
                    }
                }
            }
            if (toggled)
                UpdateSnapshot(raiseHudToast: true);
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
            bool toggled = false;
            lock (_syncLock)
            {
                if (_captureVolume is not null && _captureVolume.GetMute(out bool muted) == 0)
                {
                    Guid ctx = Guid.Empty;
                    _captureVolume.SetMute(!muted, ref ctx);
                    toggled = true;
                }
            }
            if (!toggled)
            {
                RefreshDevices();
                lock (_syncLock)
                {
                    if (_captureVolume is not null && _captureVolume.GetMute(out bool muted) == 0)
                    {
                        Guid ctx = Guid.Empty;
                        _captureVolume.SetMute(!muted, ref ctx);
                        toggled = true;
                    }
                }
            }
            if (toggled)
                UpdateSnapshot(raiseHudToast: true);
        }
        catch (Exception ex)
        {
            LogService.Warn($"ToggleMicMute error: {ex.Message}");
        }
    }

    private void PollActiveCaptureSessionsSafe()
    {
        if (_disposed) return;

        bool active = false;
        string appName = string.Empty;
        IMMDeviceEnumerator? localEnum = null;
        try
        {
            localEnum = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            string? checkedId = null;

            if (localEnum.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eCommunications, out var commDev) == 0 && commDev is not null)
            {
                try
                {
                    commDev.GetId(out checkedId);
                    InspectCaptureDeviceSessions(commDev, ref active, ref appName);
                }
                finally
                {
                    Marshal.ReleaseComObject(commDev);
                }
            }

            if (!active && localEnum.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eMultimedia, out var mediaDev) == 0 && mediaDev is not null)
            {
                try
                {
                    mediaDev.GetId(out string? mediaId);
                    if (!string.Equals(mediaId, checkedId, StringComparison.OrdinalIgnoreCase))
                        InspectCaptureDeviceSessions(mediaDev, ref active, ref appName);
                }
                finally
                {
                    Marshal.ReleaseComObject(mediaDev);
                }
            }
        }
        catch
        {
            // Ignore transient Core Audio session enumeration errors
        }
        finally
        {
            if (localEnum is not null)
            {
                try { Marshal.ReleaseComObject(localEnum); } catch { }
            }
        }

        if (_disposed) return;
        if (active != _isMicActivelyCapturing || !string.Equals(appName, _activeMicAppName, StringComparison.Ordinal))
        {
            _isMicActivelyCapturing = active;
            _activeMicAppName = appName;
            UpdateSnapshot(raiseHudToast: false);
        }
    }

    private static void InspectCaptureDeviceSessions(IMMDevice captureDevice, ref bool isActive, ref string activeAppName)
    {
        IAudioSessionManager2? mgr = null;
        IAudioSessionEnumerator? sessionEnum = null;
        try
        {
            Guid iidMgr = IID_IAudioSessionManager2;
            if (captureDevice.Activate(ref iidMgr, CLSCTX_ALL, IntPtr.Zero, out object mgrObj) != 0 || mgrObj is not IAudioSessionManager2 m)
                return;
            mgr = m;

            if (mgr.GetSessionEnumerator(out sessionEnum) != 0 || sessionEnum is null)
                return;

            if (sessionEnum.GetCount(out int count) != 0 || count <= 0)
                return;

            for (int i = 0; i < count; i++)
            {
                IAudioSessionControl? ctrl = null;
                try
                {
                    if (sessionEnum.GetSession(i, out ctrl) != 0 || ctrl is null)
                        continue;

                    if (ctrl.GetState(out int st) != 0 || st != 1 /* AudioSessionStateActive */)
                        continue;

                    if (ctrl is IAudioSessionControl2 ctrl2)
                    {
                        if (ctrl2.IsSystemSoundsSession() == 0)
                            continue;

                        ctrl2.GetProcessId(out uint pid);
                        if (pid == 0)
                            continue;

                        isActive = true;
                        activeAppName = FormatFriendlyMicAppName(pid);
                        if (!string.IsNullOrEmpty(activeAppName))
                            return;
                    }
                }
                catch { }
                finally
                {
                    if (ctrl is not null)
                    {
                        try { Marshal.ReleaseComObject(ctrl); } catch { }
                    }
                }
            }
        }
        finally
        {
            if (sessionEnum is not null)
            {
                try { Marshal.ReleaseComObject(sessionEnum); } catch { }
            }
            if (mgr is not null)
            {
                try { Marshal.ReleaseComObject(mgr); } catch { }
            }
        }
    }

    internal static string FormatFriendlyMicAppName(uint pid)
    {
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            return FormatFriendlyProcessLabel(proc.ProcessName);
        }
        catch
        {
            return LocalizationService.Pick("Microphone", "Микрофон");
        }
    }

    internal static string FormatFriendlyProcessLabel(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
            return LocalizationService.Pick("Microphone", "Микрофон");

        string lower = rawName.Trim().ToLowerInvariant();
        if (lower.EndsWith(".exe", StringComparison.Ordinal))
            lower = lower[..^4];

        return lower switch
        {
            "discord" => "Discord",
            "telegram" => "Telegram",
            "chrome" => "Chrome",
            "msedge" => "Edge",
            "firefox" => "Firefox",
            "opera" => "Opera",
            "brave" => "Brave",
            "browser" => LocalizationService.Pick("Yandex", "Яндекс"),
            "zoom" => "Zoom",
            "teams" or "ms-teams" => "Teams",
            "skype" => "Skype",
            "obs64" or "obs32" or "obs" => "OBS",
            "nvidia share" or "nvcontainer" => "NVIDIA",
            "pulseisland" => LocalizationService.Pick("Dictation", "Диктовка"),
            "ts3client_win64" or "teamspeak" => "TeamSpeak",
            "steam" or "steamwebhelper" => "Steam",
            _ => rawName.Length <= 14
                ? char.ToUpperInvariant(rawName[0]) + rawName[1..]
                : char.ToUpperInvariant(rawName[0]) + rawName[1..12] + "…"
        };
    }

    private void UpdateSnapshot(bool raiseHudToast)
    {
        int volPercent = 0;
        bool speakerMuted = false;
        bool hasSpeaker;
        bool micMuted = false;
        bool hasMic;

        lock (_syncLock)
        {
            hasSpeaker = _renderVolume is not null;
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

            hasMic = _captureVolume is not null;
            if (_captureVolume is not null)
            {
                try
                {
                    if (_captureVolume.GetMute(out bool m) == 0)
                        micMuted = m;
                }
                catch { hasMic = false; }
            }
        }

        var snapshot = new AudioEndpointState(
            volPercent,
            speakerMuted,
            micMuted,
            hasSpeaker,
            hasMic,
            _isMicActivelyCapturing,
            _activeMicAppName,
            _currentRenderShortName,
            _currentRenderFullName,
            _renderDeviceCount);

        CurrentState = snapshot;
        StateChanged?.Invoke(snapshot, raiseHudToast);
    }

    private void DetachEndpointsSafe()
    {
        IAudioEndpointVolume? renderVol;
        VolumeCallback? renderCb;
        IAudioEndpointVolume? captureVol;
        VolumeCallback? captureCb;

        lock (_syncLock)
        {
            renderVol = _renderVolume;
            renderCb = _renderCallback;
            captureVol = _captureVolume;
            captureCb = _captureCallback;
            _renderVolume = null;
            _renderCallback = null;
            _captureVolume = null;
            _captureCallback = null;
        }

        ReleaseVolumeEndpoint(renderVol, renderCb);
        ReleaseVolumeEndpoint(captureVol, captureCb);
    }

    private static void ReleaseVolumeEndpoint(IAudioEndpointVolume? vol, VolumeCallback? cb)
    {
        if (vol is null) return;
        try
        {
            if (cb is not null)
                vol.UnregisterControlChangeNotify(cb);
            Marshal.ReleaseComObject(vol);
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _micSessionPollTimer?.Dispose(); } catch { }
        _micSessionPollTimer = null;

        DetachEndpointsSafe();

        IMMDeviceEnumerator? enumerator;
        EndpointChangeClient? client;
        lock (_syncLock)
        {
            enumerator = _enumerator;
            client = _endpointChangeClient;
            _enumerator = null;
            _endpointChangeClient = null;
        }

        if (enumerator is not null)
        {
            try
            {
                if (client is not null)
                    enumerator.UnregisterEndpointNotificationCallback(client);
            }
            catch { }
            try { Marshal.ReleaseComObject(enumerator); } catch { }
        }
    }
}
