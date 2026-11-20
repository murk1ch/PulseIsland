using System.Diagnostics;
using System.Runtime.InteropServices;
using static PulseIsland.Audio.WasapiComInterop;

namespace PulseIsland.Audio;

/// <summary>
/// Inspects per-process WASAPI render session peak meters (IAudioSessionManager2 + IAudioMeterInformation)
/// so the visualizer reacts strictly to the active music player and ignores voice/chat apps like Discord.
/// </summary>
public sealed class AudioSessionMusicFilter : IDisposable
{
    private static readonly string[] VoiceProcessMarkers =
    [
        "discord", "teamspeak", "ts3client", "zoom", "skype",
        "telegram", "viber", "mumble", "ventrilo", "webex", "facetime"
    ];

    private static readonly string[] KnownMusicProcessMarkers =
    [
        "spotify", "yandex", "applemusic", "music.ui", "aimp", "foobar2000",
        "vlc", "wmplayer", "winamp", "itunes", "musicbee", "tidal", "deezer", "qobuz"
    ];

    private readonly Dictionary<uint, string> _pidNameCache = new();
    private CancellationTokenSource? _cts;
    private Thread? _pollThread;
    private volatile string _activeSourceAppId = string.Empty;
    private volatile float _musicPeak;
    private volatile float _voicePeak;
    private volatile float _musicGain = 1.0f;
    private volatile bool _hasLiveSessionData;
    private volatile bool _disposed;

    public string ActiveSourceAppId
    {
        get => _activeSourceAppId;
        set => _activeSourceAppId = value ?? string.Empty;
    }

    /// <summary>
    /// Normalized [0..1] scale factor indicating how strongly the current system audio comes from music vs voice/Discord.
    /// </summary>
    public float MusicGain => _musicGain;
    public float MusicPeak => _musicPeak;
    public float VoicePeak => _voicePeak;
    public bool HasLiveSessionData => _hasLiveSessionData;

    public void Start()
    {
        if (_disposed || _pollThread is not null)
            return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _pollThread = new Thread(() => PollLoop(token))
        {
            IsBackground = true,
            Name = "PulseIsland.MusicSessionFilter"
        };
        _pollThread.Start();
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _pollThread?.Join(250); } catch { }
        _pollThread = null;
        _cts?.Dispose();
        _cts = null;
    }

    private void PollLoop(CancellationToken token)
    {
        int cacheClearCounter = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                SampleSessions();
                if (++cacheClearCounter >= 100)
                {
                    cacheClearCounter = 0;
                    _pidNameCache.Clear();
                }
            }
            catch
            {
                _hasLiveSessionData = false;
                _musicGain = 1.0f;
            }

            Thread.Sleep(50);
        }
    }

    private void SampleSessions()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioSessionManager2? sessionManager = null;
        IAudioSessionEnumerator? sessionEnum = null;

        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out device) != 0 || device is null)
                return;

            Guid iidMgr = IID_IAudioSessionManager2;
            if (device.Activate(ref iidMgr, CLSCTX_ALL, IntPtr.Zero, out object mgrObj) != 0 || mgrObj is not IAudioSessionManager2 mgr)
                return;
            sessionManager = mgr;

            if (sessionManager.GetSessionEnumerator(out sessionEnum) != 0 || sessionEnum is null)
                return;

            if (sessionEnum.GetCount(out int count) != 0)
                return;

            float musicPeak = 0f;
            float voicePeak = 0f;
            float otherPeak = 0f;
            string sourceApp = _activeSourceAppId.ToLowerInvariant();

            for (int i = 0; i < count; i++)
            {
                IAudioSessionControl? ctrl = null;
                try
                {
                    if (sessionEnum.GetSession(i, out ctrl) != 0 || ctrl is null)
                        continue;

                    if (ctrl.GetState(out int state) != 0 || state != 1) // 1 = AudioSessionStateActive
                        continue;

                    if (ctrl is IAudioSessionControl2 ctrl2)
                    {
                        if (ctrl2.IsSystemSoundsSession() == 0)
                            continue;

                        ctrl2.GetProcessId(out uint pid);
                        if (pid == 0)
                            continue;

                        float peak = 0f;
                        if (ctrl is IAudioMeterInformation meter)
                            meter.GetPeakValue(out peak);

                        string procName = ResolveProcessName(pid);
                        if (IsVoiceApp(procName))
                        {
                            if (peak > voicePeak) voicePeak = peak;
                        }
                        else if (IsMusicApp(procName, sourceApp))
                        {
                            if (peak > musicPeak) musicPeak = peak;
                        }
                        else
                        {
                            if (peak > otherPeak) otherPeak = peak;
                        }
                    }
                }
                catch { }
                finally
                {
                    if (ctrl is not null)
                        Marshal.ReleaseComObject(ctrl);
                }
            }

            _musicPeak = musicPeak;
            _voicePeak = voicePeak;
            _hasLiveSessionData = true;

            // Compute suppression gain:
            // If Discord/voice is speaking while the music session itself is silent, drop gain to 0.
            // If both are active, scale shared loopback FFT down proportionally when voice dominates.
            if (musicPeak > 0.002f)
            {
                _musicGain = voicePeak > 0.005f
                    ? Math.Clamp(musicPeak / (musicPeak + voicePeak * 1.4f), 0.12f, 1.0f)
                    : 1.0f;
            }
            else if (voicePeak > 0.003f || otherPeak > 0.003f)
            {
                _musicGain = 0f;
            }
            else
            {
                _musicGain = 1.0f;
            }
        }
        finally
        {
            if (sessionEnum is not null) Marshal.ReleaseComObject(sessionEnum);
            if (sessionManager is not null) Marshal.ReleaseComObject(sessionManager);
            if (device is not null) Marshal.ReleaseComObject(device);
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
        }
    }

    private string ResolveProcessName(uint pid)
    {
        if (_pidNameCache.TryGetValue(pid, out string? cached))
            return cached;

        try
        {
            using var proc = Process.GetProcessById((int)pid);
            string name = proc.ProcessName.ToLowerInvariant();
            _pidNameCache[pid] = name;
            return name;
        }
        catch
        {
            _pidNameCache[pid] = string.Empty;
            return string.Empty;
        }
    }

    private static bool IsVoiceApp(string procName)
    {
        if (string.IsNullOrEmpty(procName))
            return false;

        foreach (string marker in VoiceProcessMarkers)
        {
            if (procName.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool IsMusicApp(string procName, string sourceAppLower)
    {
        if (string.IsNullOrEmpty(procName))
            return false;

        if (!string.IsNullOrWhiteSpace(sourceAppLower))
        {
            string cleanProc = procName.Replace(".exe", string.Empty, StringComparison.OrdinalIgnoreCase);
            if (sourceAppLower.Contains(cleanProc, StringComparison.OrdinalIgnoreCase))
                return true;

            // Match friendly Chrome/Edge/Yandex AppUserModelIds
            if ((sourceAppLower.Contains("chrome") && cleanProc == "chrome") ||
                (sourceAppLower.Contains("msedge") && cleanProc == "msedge") ||
                (sourceAppLower.Contains("firefox") && cleanProc == "firefox") ||
                (sourceAppLower.Contains("opera") && cleanProc == "opera") ||
                (sourceAppLower.Contains("brave") && cleanProc == "brave"))
                return true;
        }

        foreach (string marker in KnownMusicProcessMarkers)
        {
            if (procName.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
