using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PulseIsland.Core;
using PulseIsland.Services;

#if WITH_WINRT
using Windows.Media.Control;
using Windows.Storage.Streams;
#endif

namespace PulseIsland.Media;

/// <summary>
/// Integrates with Windows System Media Transport Controls (SMTC) to retrieve track title, artist,
/// playback status, and album art thumbnail (for adaptive neon glow), plus media transport commands.
/// </summary>
public sealed class SmtcMediaService : IMediaSessionService
{
    private static readonly Color DefaultAccent = Color.FromRgb(0x00, 0xF0, 0xFF);
    private readonly Func<bool>? _isSystemSilent;

    private MediaTrackInfo _currentTrack = new(
        Title: string.Empty,
        Artist: string.Empty,
        IsPlaying: false,
        HasActiveSession: false,
        Thumbnail: null,
        AccentColor: DefaultAccent);

    public SmtcMediaService(Func<bool>? isSystemSilent = null)
    {
        _isSystemSilent = isSystemSilent;
    }

    public MediaTrackInfo CurrentTrack => _currentTrack;
    public event Action<MediaTrackInfo>? TrackChanged;

#if WITH_WINRT
    private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
    private GlobalSystemMediaTransportControlsSession? _currentSession;

    public async Task StartAsync()
    {
        try
        {
            _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _sessionManager.CurrentSessionChanged += OnCurrentSessionChanged;
            AttachToSession(_sessionManager.GetCurrentSession());
        }
        catch (Exception ex)
        {
            LogService.Error("Failed to initialize WinRT SMTC session manager.", ex);
        }
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        AttachToSession(sender.GetCurrentSession());
    }

    private void AttachToSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_currentSession is not null)
        {
            _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }

        _currentSession = session;

        if (_currentSession is null)
        {
            UpdateTrack(new MediaTrackInfo(string.Empty, string.Empty, false, false, null, DefaultAccent));
            return;
        }

        _currentSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
        _currentSession.PlaybackInfoChanged += OnPlaybackInfoChanged;
        _ = RefreshSessionAsync(_currentSession);
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        => _ = RefreshSessionAsync(sender);

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        => _ = RefreshSessionAsync(sender);

    private async Task RefreshSessionAsync(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            bool isPlaying = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            double durSec = timeline is not null ? Math.Max(0, (timeline.EndTime - timeline.StartTime).TotalSeconds) : 0;
            double posSec = timeline is not null ? Math.Max(0, (timeline.Position - timeline.StartTime).TotalSeconds) : 0;
            if (isPlaying && timeline is not null && timeline.LastUpdatedTime.Year > 2000)
            {
                posSec = Math.Clamp(posSec + (DateTimeOffset.Now - timeline.LastUpdatedTime).TotalSeconds, 0, Math.Max(posSec, durSec));
            }

            BitmapSource? thumbnail = null;
            if (props.Thumbnail is not null)
            {
                using IRandomAccessStreamWithContentType stream = await props.Thumbnail.OpenReadAsync();
                using var netStream = stream.AsStreamForRead();
                using var ms = new MemoryStream();
                await netStream.CopyToAsync(ms);
                ms.Position = 0;

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                thumbnail = bmp;
            }

            var accent = AlbumColorHelper.ExtractDominantColor(thumbnail, DefaultAccent);
            UpdateTrack(new MediaTrackInfo(
                Title: props.Title ?? string.Empty,
                Artist: props.Artist ?? string.Empty,
                IsPlaying: isPlaying,
                HasActiveSession: !string.IsNullOrWhiteSpace(props.Title),
                Thumbnail: thumbnail,
                AccentColor: accent,
                SourceAppId: session.SourceAppUserModelId ?? string.Empty,
                PositionSeconds: posSec,
                DurationSeconds: durSec,
                CanSeek: durSec > 1.0));
        }
        catch (Exception ex)
        {
            LogService.Warn($"SMTC refresh error: {ex.Message}");
        }
    }

    public async Task TogglePlayPauseAsync()
    {
        if (_currentSession is not null)
            await _currentSession.TryTogglePlayPauseAsync();
        else
            SendMediaKey(VK_MEDIA_PLAY_PAUSE);
    }

    public async Task NextTrackAsync()
    {
        if (_currentSession is not null)
            await _currentSession.TrySkipNextAsync();
        else
            SendMediaKey(VK_MEDIA_NEXT_TRACK);
    }

    public async Task PreviousTrackAsync()
    {
        if (_currentSession is not null)
            await _currentSession.TrySkipPreviousAsync();
        else
            SendMediaKey(VK_MEDIA_PREV_TRACK);
    }

    public async Task SeekToAsync(double positionSeconds)
    {
        if (_currentSession is not null)
        {
            long ticks = TimeSpan.FromSeconds(Math.Max(0, positionSeconds)).Ticks;
            await _currentSession.TryChangePlaybackPositionAsync(ticks);
            UpdateTrack(_currentTrack with { PositionSeconds = Math.Max(0, positionSeconds) });
        }
    }

    public void Dispose()
    {
        if (_currentSession is not null)
        {
            _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }
        if (_sessionManager is not null)
        {
            _sessionManager.CurrentSessionChanged -= OnCurrentSessionChanged;
        }
    }
#else
    private System.Threading.Timer? _pollTimer;
    private int _isPolling;
    private string _lastThumbKey = string.Empty;
    private BitmapSource? _cachedThumbnail;
    private Color _cachedAccent = DefaultAccent;

    public Task StartAsync()
    {
        _pollTimer = new System.Threading.Timer(_ => _ = PollMediaStateAsync(), null, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(2200));
        return Task.CompletedTask;
    }

    private async Task PollMediaStateAsync()
    {
        if (Interlocked.Exchange(ref _isPolling, 1) == 1)
            return;

        try
        {
            bool isSilentNow = _isSystemSilent?.Invoke() ?? true;

            // 1. Try querying real Windows SMTC (GlobalSystemMediaTransportControlsSessionManager) in-memory
            var smtc = await TryQueryWindowsSmtcBridgeAsync();
            if (smtc is not null && !string.IsNullOrWhiteSpace(smtc.Value.Title))
            {
                string thumbKey = $"{smtc.Value.Artist}|{smtc.Value.Title}";
                if (thumbKey != _lastThumbKey && !string.IsNullOrWhiteSpace(smtc.Value.ThumbBase64))
                {
                    _cachedThumbnail = DecodeThumbnail(smtc.Value.ThumbBase64);
                    _cachedAccent = AlbumColorHelper.ExtractDominantColor(_cachedThumbnail, DefaultAccent);
                    _lastThumbKey = thumbKey;
                }
                else if (string.IsNullOrWhiteSpace(smtc.Value.ThumbBase64) && thumbKey != _lastThumbKey)
                {
                    _cachedThumbnail = null;
                    _cachedAccent = DefaultAccent;
                    _lastThumbKey = thumbKey;
                }

                bool playing = smtc.Value.IsPlaying || !isSilentNow;
                UpdateTrack(new MediaTrackInfo(
                    Title: smtc.Value.Title,
                    Artist: smtc.Value.Artist,
                    IsPlaying: playing,
                    HasActiveSession: true,
                    Thumbnail: _cachedThumbnail,
                    AccentColor: _cachedAccent,
                    SourceAppId: smtc.Value.AppId,
                    PositionSeconds: smtc.Value.PositionSeconds,
                    DurationSeconds: smtc.Value.DurationSeconds,
                    CanSeek: smtc.Value.DurationSeconds > 1.0));
                return;
            }

            // 2. Fallback: Check known player window titles
            string[] playerNames = { "Spotify", "YandexMusic", "Яндекс Музыка", "AIMP", "foobar2000", "vlc", "chrome", "msedge", "yandex", "opera", "firefox" };
            foreach (var procName in playerNames)
            {
                var procs = Process.GetProcessesByName(procName);
                foreach (var p in procs)
                {
                    try
                    {
                        var title = p.MainWindowTitle;
                        if (string.IsNullOrWhiteSpace(title)) continue;

                        if (TryParseMediaTitle(procName, title, out var artist, out var track, out bool isExplicitPause))
                        {
                            bool playing = !isExplicitPause && !isSilentNow;
                            UpdateTrack(new MediaTrackInfo(
                                Title: track,
                                Artist: artist,
                                IsPlaying: playing,
                                HasActiveSession: true,
                                Thumbnail: null,
                                AccentColor: DefaultAccent,
                                SourceAppId: procName));
                            return;
                        }
                    }
                    finally
                    {
                        p.Dispose();
                    }
                }
            }

            // 3. If system audio is actively playing right now even without SMTC metadata, show active audio state
            if (!isSilentNow)
            {
                UpdateTrack(new MediaTrackInfo(
                    Title: string.IsNullOrWhiteSpace(_currentTrack.Title) ? LocalizationService.Pick("System Audio", "Системное аудио") : _currentTrack.Title,
                    Artist: string.IsNullOrWhiteSpace(_currentTrack.Artist) ? LocalizationService.Pick("Now Playing", "Воспроизведение") : _currentTrack.Artist,
                    IsPlaying: true,
                    HasActiveSession: true,
                    Thumbnail: _cachedThumbnail,
                    AccentColor: _cachedAccent,
                    SourceAppId: "SystemAudio"));
                return;
            }

            if (_currentTrack.HasActiveSession)
            {
                UpdateTrack(_currentTrack with { IsPlaying = false });
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"Media state poll error: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _isPolling, 0);
        }
    }

    private readonly record struct SmtcBridgeDto(
        string Title,
        string Artist,
        bool IsPlaying,
        string AppId,
        string ThumbBase64,
        double PositionSeconds,
        double DurationSeconds);

    private static async Task<SmtcBridgeDto?> TryQueryWindowsSmtcBridgeAsync()
    {
        try
        {
            const string script = @"
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows.Media.Control, ContentType=WindowsRuntime]
$null = [Windows.Storage.Streams.IRandomAccessStreamWithContentType, Windows.Storage.Streams, ContentType=WindowsRuntime]
$null = [Windows.Storage.Streams.IInputStream, Windows.Storage.Streams, ContentType=WindowsRuntime]

$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
function Await($WinRtTask, $ResultType) {
    $netTask = $asTaskGeneric.MakeGenericMethod($ResultType).Invoke($null, @($WinRtTask))
    $netTask.Wait(1500) | Out-Null
    return $netTask.Result
}

$mgr = Await ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]::RequestAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager])
$session = $mgr.GetCurrentSession()
if ($null -eq $session) {
    Write-Output '{}'
    exit 0
}

$props = Await ($session.TryGetMediaPropertiesAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties])
$info = $session.GetPlaybackInfo()
$playing = ($info.PlaybackStatus -eq [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus]::Playing)

$posSec = 0.0
$durSec = 0.0
try {
    $tl = $session.GetTimelineProperties()
    if ($null -ne $tl) {
        $durSec = [Math]::Max(0.0, ($tl.EndTime - $tl.StartTime).TotalSeconds)
        $posSec = [Math]::Max(0.0, ($tl.Position - $tl.StartTime).TotalSeconds)
        if ($playing -and $tl.LastUpdatedTime.Year -gt 2000) {
            $elapsed = ([DateTimeOffset]::Now - $tl.LastUpdatedTime).TotalSeconds
            $posSec = [Math]::Max(0.0, $posSec + $elapsed)
            if ($durSec -gt 0) { $posSec = [Math]::Min($durSec, $posSec) }
        }
    }
} catch {}

$thumbB64 = ''
if ($null -ne $props.Thumbnail) {
    try {
        $stream = Await ($props.Thumbnail.OpenReadAsync()) ([Windows.Storage.Streams.IRandomAccessStreamWithContentType])
        if ($null -ne $stream) {
            $asStream = [System.IO.WindowsRuntimeStreamExtensions].GetMethod('AsStreamForRead', [Type[]]@([Windows.Storage.Streams.IInputStream]))
            $netStream = $asStream.Invoke($null, @($stream))
            $ms = New-Object System.IO.MemoryStream
            $netStream.CopyTo($ms)
            if ($ms.Length -gt 0 -and $ms.Length -lt 2097152) {
                $thumbB64 = [Convert]::ToBase64String($ms.ToArray())
            }
            $ms.Dispose()
            $netStream.Dispose()
        }
    } catch {}
}

Write-Output (@{
    title = [string]$props.Title
    artist = [string]$props.Artist
    playing = [bool]$playing
    app = [string]$session.SourceAppUserModelId
    thumb = $thumbB64
    pos = [Math]::Round($posSec, 2)
    dur = [Math]::Round($durSec, 2)
} | ConvertTo-Json -Compress)
";
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc is null) return null;

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(2500));
            string json = await proc.StandardOutput.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);

            if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}")
                return null;

            using var doc = JsonDocument.Parse(json.Trim());
            var root = doc.RootElement;
            if (!root.TryGetProperty("title", out var titleEl))
                return null;

            return new SmtcBridgeDto(
                Title: titleEl.GetString() ?? string.Empty,
                Artist: root.TryGetProperty("artist", out var artEl) ? (artEl.GetString() ?? string.Empty) : string.Empty,
                IsPlaying: root.TryGetProperty("playing", out var plEl) && plEl.GetBoolean(),
                AppId: root.TryGetProperty("app", out var appEl) ? (appEl.GetString() ?? string.Empty) : string.Empty,
                ThumbBase64: root.TryGetProperty("thumb", out var thEl) ? (thEl.GetString() ?? string.Empty) : string.Empty,
                PositionSeconds: root.TryGetProperty("pos", out var posEl) ? posEl.GetDouble() : 0.0,
                DurationSeconds: root.TryGetProperty("dur", out var durEl) ? durEl.GetDouble() : 0.0);
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource? DecodeThumbnail(string base64)
    {
        try
        {
            byte[] bytes = Convert.FromBase64String(base64);
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryParseMediaTitle(
        string procName,
        string windowTitle,
        out string artist,
        out string track,
        out bool isExplicitPause)
    {
        artist = string.Empty;
        track = string.Empty;
        isExplicitPause = false;

        if (procName.Equals("Spotify", StringComparison.OrdinalIgnoreCase))
        {
            if (windowTitle is "Spotify Free" or "Spotify Premium" or "Spotify")
            {
                isExplicitPause = true;
                return false;
            }

            var parts = windowTitle.Split(" - ", 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2)
            {
                artist = parts[0];
                track = parts[1];
                return true;
            }
        }

        if (windowTitle.Contains("YouTube", StringComparison.OrdinalIgnoreCase) ||
            windowTitle.Contains("Яндекс Музыка", StringComparison.OrdinalIgnoreCase))
        {
            track = windowTitle
                .Replace(" - YouTube", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace(" — Яндекс Музыка", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim();
            artist = procName;
            return !string.IsNullOrWhiteSpace(track);
        }

        if (procName is "AIMP" or "foobar2000" or "vlc")
        {
            if (windowTitle is "VLC media player" or "AIMP" or "foobar2000")
            {
                isExplicitPause = true;
                return false;
            }

            var cleaned = windowTitle.Replace(" - VLC media player", string.Empty).Trim();
            var parts = cleaned.Split(" - ", 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2)
            {
                artist = parts[0];
                track = parts[1];
            }
            else
            {
                track = cleaned;
            }
            return !string.IsNullOrWhiteSpace(track);
        }

        return false;
    }

    public async Task TogglePlayPauseAsync()
    {
        if (_currentTrack.HasActiveSession)
        {
            UpdateTrack(_currentTrack with { IsPlaying = !_currentTrack.IsPlaying });
        }

        if (!await ExecuteSmtcSessionActionAsync("TryTogglePlayPauseAsync()"))
        {
            SendMediaKey(VK_MEDIA_PLAY_PAUSE);
        }
    }

    public async Task NextTrackAsync()
    {
        if (!await ExecuteSmtcSessionActionAsync("TrySkipNextAsync()"))
        {
            SendMediaKey(VK_MEDIA_NEXT_TRACK);
        }
    }

    public async Task PreviousTrackAsync()
    {
        if (!await ExecuteSmtcSessionActionAsync("TrySkipPreviousAsync()"))
        {
            SendMediaKey(VK_MEDIA_PREV_TRACK);
        }
    }

    public async Task SeekToAsync(double positionSeconds)
    {
        long targetTicks = TimeSpan.FromSeconds(Math.Max(0, positionSeconds)).Ticks;
        if (_currentTrack.HasActiveSession)
            UpdateTrack(_currentTrack with { PositionSeconds = Math.Max(0, positionSeconds) });

        await ExecuteSmtcSessionActionAsync($"TryChangePlaybackPositionAsync({targetTicks})");
    }

    private static async Task<bool> ExecuteSmtcSessionActionAsync(string sessionMethodCall)
    {
        try
        {
            string script = $@"
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows.Media.Control, ContentType=WindowsRuntime]
$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {{ $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' }})[0]
$mgrTask = $asTaskGeneric.MakeGenericMethod([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]).Invoke($null, @([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]::RequestAsync()))
$mgrTask.Wait(1200) | Out-Null
$session = $mgrTask.Result.GetCurrentSession()
if ($null -eq $session) {{ Write-Output 'NO_SESSION'; exit 0 }}
$opTask = $asTaskGeneric.MakeGenericMethod([bool]).Invoke($null, @($session.{sessionMethodCall}))
$opTask.Wait(1200) | Out-Null
Write-Output 'OK'
";
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null)
                return false;

            string output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return output.Contains("OK", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _pollTimer?.Dispose();
    }
#endif

    private void UpdateTrack(MediaTrackInfo next)
    {
        if (Equals(_currentTrack, next))
            return;

        _currentTrack = next;
        TrackChanged?.Invoke(next);
    }

    private const byte VK_MEDIA_NEXT_TRACK = 0xB0;
    private const byte VK_MEDIA_PREV_TRACK = 0xB1;
    private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private static void SendMediaKey(byte vk)
    {
        keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
        keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
    }
}
