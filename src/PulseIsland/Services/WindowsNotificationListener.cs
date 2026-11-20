using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using PulseIsland.Core;

namespace PulseIsland.Services;

/// <summary>
/// Event-driven WinRT notification interceptor combining Windows Notification Facility (WNF)
/// instant triggers (WNF_SHEL_TOAST_PUBLISHED) with a single persistent hidden UserNotificationListener worker.
/// Extracts AppUserModelId, sender identity, first words of messages, and normalized media tags
/// (Voice Message, Video Message, Photo, Video, Sticker, GIF, File).
/// </summary>
public sealed class WindowsNotificationListener : IDisposable
{
    private const ulong WnfShelToastPublished = 0x0D83063EA3BF1075UL;

    private delegate int WnfCallback(
        ulong stateName,
        uint changeStamp,
        IntPtr typeId,
        IntPtr callbackContext,
        IntPtr buffer,
        uint bufferSize);

    [DllImport("ntdll.dll")]
    private static extern int RtlSubscribeWnfStateChangeNotification(
        out IntPtr subscription,
        ulong stateName,
        uint changeStamp,
        WnfCallback callback,
        IntPtr callbackContext,
        IntPtr typeId,
        uint serializationGroup,
        uint unknown);

    [DllImport("ntdll.dll")]
    private static extern int RtlUnsubscribeWnfStateChangeNotification(IntPtr subscription);

    private readonly IslandNotificationService _service;
    private readonly Func<double> _getAutoDismissSeconds;
    private readonly Func<bool> _isSuppressionEnabled;
    private readonly HashSet<string> _seenKeys = new();
    private readonly SemaphoreSlim _pollSignal = new(0, 8);

    private WnfCallback? _wnfDelegate;
    private IntPtr _wnfSubscription;
    private Process? _workerProcess;
    private StreamWriter? _workerStdin;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    private static readonly HashSet<string> SuppressedApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "PulseIsland"
    };

    public WindowsNotificationListener(
        IslandNotificationService service,
        Func<double>? getAutoDismissSeconds = null,
        Func<bool>? isSuppressionEnabled = null)
    {
        _service = service;
        _getAutoDismissSeconds = getAutoDismissSeconds ?? (() => 5.0);
        _isSuppressionEnabled = isSuppressionEnabled ?? (() => true);
    }

    public void Start(Dispatcher dispatcher)
    {
        if (_disposed || _cts is not null)
            return;

        _ = dispatcher;
        _cts = new CancellationTokenSource();
        SubscribeWnf();
        _ = Task.Run(() => RunWorkerLoopAsync(_cts.Token), _cts.Token);
    }

    private void SubscribeWnf()
    {
        try
        {
            _wnfDelegate = (_, _, _, _, _, _) =>
            {
                if (!_disposed && _pollSignal.CurrentCount < 4)
                {
                    try { _pollSignal.Release(); } catch { }
                }
                return 0;
            };

            int hr = RtlSubscribeWnfStateChangeNotification(
                out _wnfSubscription,
                WnfShelToastPublished,
                0,
                _wnfDelegate,
                IntPtr.Zero,
                IntPtr.Zero,
                0,
                0);

            if (hr == 0)
                LogService.Info("WindowsNotificationListener: WNF_SHEL_TOAST_PUBLISHED event hook active.");
        }
        catch (Exception ex)
        {
            LogService.Error("WindowsNotificationListener: WNF subscription fallback to timer.", ex);
        }
    }

    private async Task RunWorkerLoopAsync(CancellationToken ct)
    {
        int consecutiveFailures = 0;
        while (!ct.IsCancellationRequested && !_disposed)
        {
            try
            {
                await RunSingleWorkerSessionAsync(ct).ConfigureAwait(false);
                consecutiveFailures = 0;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                LogService.Error("WindowsNotificationListener worker error.", ex);
                if (consecutiveFailures >= 5)
                {
                    LogService.Info("WindowsNotificationListener paused after repeated failures.");
                    break;
                }
            }

            if (!ct.IsCancellationRequested && !_disposed)
                await Task.Delay(3000, ct).ConfigureAwait(false);
        }
    }

    private async Task RunSingleWorkerSessionAsync(CancellationToken ct)
    {
        string encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(WorkerScript));
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encodedScript}",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardInputEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc is null) return;
        _workerProcess = proc;
        _workerStdin = proc.StandardInput;

        using var reg = ct.Register(() =>
        {
            try
            {
                if (!proc.HasExited)
                    proc.Kill(entireProcessTree: true);
            }
            catch { }
        });

        // Stdin wake driver: wakes immediately on WNF toast signal, plus 2.5s safety heartbeat
        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested && !proc.HasExited)
                {
                    await _pollSignal.WaitAsync(2500, ct).ConfigureAwait(false);
                    if (ct.IsCancellationRequested || proc.HasExited) break;
                    await Task.Delay(40, ct).ConfigureAwait(false); // tiny coalesce for batch bursts
                    while (_pollSignal.CurrentCount > 0) _pollSignal.Wait(0);
                    await proc.StandardInput.WriteLineAsync("POLL").ConfigureAwait(false);
                    await proc.StandardInput.FlushAsync().ConfigureAwait(false);
                }
            }
            catch { }
        }, ct);

        while (!ct.IsCancellationRequested && !proc.HasExited)
        {
            string? line = await proc.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            ProcessJsonPacket(line.Trim());
        }

        _workerStdin = null;
        _workerProcess = null;
    }

    private void ProcessJsonPacket(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            bool isInit = root.TryGetProperty("init", out var initEl) && initEl.GetBoolean();
            if (!root.TryGetProperty("items", out var itemsEl))
                return;

            var itemElements = new List<JsonElement>();
            if (itemsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in itemsEl.EnumerateArray())
                    itemElements.Add(item);
            }
            else if (itemsEl.ValueKind == JsonValueKind.Object)
            {
                itemElements.Add(itemsEl);
            }
            else
            {
                return;
            }

            double dismissSec = Math.Clamp(_getAutoDismissSeconds(), 2.0, 30.0);
            var parsed = new List<IslandNotification>();

            foreach (var el in itemElements)
            {
                string rawId = el.TryGetProperty("id", out var idEl) ? idEl.ToString() : Guid.NewGuid().ToString("N")[..8];
                string defaultSystemApp = LocalizationService.Pick("System", "Система");
                string app = el.TryGetProperty("app", out var appEl) ? (appEl.GetString() ?? defaultSystemApp).Trim() : defaultSystemApp;
                string aumid = el.TryGetProperty("aumid", out var aumidEl) ? (aumidEl.GetString() ?? "").Trim() : "";
                string rawTitle = el.TryGetProperty("title", out var titleEl) ? (titleEl.GetString() ?? "").Trim() : "";
                string rawBody = el.TryGetProperty("body", out var bodyEl) ? (bodyEl.GetString() ?? "").Trim() : "";
                long ticks = el.TryGetProperty("ticks", out var ticksEl) && ticksEl.TryGetInt64(out long t) ? t : DateTime.Now.Ticks;

                if (string.IsNullOrWhiteSpace(app)) app = defaultSystemApp;
                if (SuppressedApps.Contains(app) || aumid.Contains("PulseIsland", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.IsNullOrWhiteSpace(rawTitle) && string.IsNullOrWhiteSpace(rawBody))
                    continue;

                string friendlyApp = FriendlyAppName(app, aumid);
                var ts = ticks > 0 ? new DateTime(ticks, DateTimeKind.Local) : DateTime.Now;
                string dedupKey = $"{rawId}|{friendlyApp}|{rawTitle}";

                if (isInit)
                {
                    _seenKeys.Add(dedupKey);
                }
                else if (!_seenKeys.Add(dedupKey))
                {
                    continue;
                }

                if (_seenKeys.Count > 500)
                    _seenKeys.Clear();

                var (title, body, contentTag, senderHint) = FormatNotificationContent(friendlyApp, rawTitle, rawBody);

                parsed.Add(new IslandNotification(
                    Id: rawId,
                    AppName: friendlyApp,
                    Title: title,
                    Body: body,
                    Kind: GuessKind(friendlyApp, title, body),
                    Timestamp: ts,
                    AutoDismissSeconds: dismissSec,
                    AppUserModelId: aumid,
                    ContentTag: contentTag,
                    SenderHint: senderHint));
            }

            if (isInit)
            {
                if (parsed.Count > 0)
                    _service.SeedHistory(parsed);
                return;
            }

            if (parsed.Count > 0)
            {
                if (_isSuppressionEnabled())
                    WindowsToastSuppressor.SuppressVisibleToastWindows();

                for (int i = parsed.Count - 1; i >= 0; i--)
                    _service.Push(parsed[i]);
            }
        }
        catch (Exception ex)
        {
            LogService.Error("Failed to parse notification JSON packet.", ex);
        }
    }

    /// <summary>
    /// Normalizes messenger payloads (especially Telegram Desktop) into a clear sender title,
    /// concise first-words body preview, and standardized media ContentTag
    /// ("Voice Message", "Video Message", "Photo", "Video", "Sticker", "GIF", "File").
    /// </summary>
    public static (string Title, string Body, string ContentTag, string SenderHint) FormatNotificationContent(
        string friendlyApp,
        string rawTitle,
        string rawBody)
    {
        string title = CollapseWhitespace(rawTitle);
        string body = CollapseWhitespace(rawBody);

        if (string.IsNullOrWhiteSpace(title))
            title = friendlyApp;

        string senderHint = title;
        string contentTag = DetectMediaTag(body);

        if (string.IsNullOrEmpty(contentTag) && !string.IsNullOrEmpty(body))
        {
            // Check "Sender: Voice message" or "Sender: Фотография" inside group notifications
            int colonIdx = body.IndexOf(':');
            if (colonIdx > 0 && colonIdx < 48)
            {
                string possibleAuthor = body[..colonIdx].Trim();
                string afterColon = body[(colonIdx + 1)..].Trim();
                string nestedTag = DetectMediaTag(afterColon);
                if (!string.IsNullOrEmpty(nestedTag))
                {
                    contentTag = nestedTag;
                    body = $"{possibleAuthor}: {nestedTag}";
                }
            }
        }

        if (!string.IsNullOrEmpty(contentTag))
        {
            // Cleanly surface caption if body has "Photo (caption)" or "Фотография, описание"
            string normalizedBody = FormatMediaBodyWithCaption(body, contentTag);
            body = normalizedBody;
        }
        else
        {
            body = TruncatePreview(body, 96);
        }

        return (TruncatePreview(title, 64), body, contentTag, senderHint);
    }

    private static string DetectMediaTag(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        string lower = text.Trim().ToLowerInvariant();

        // Check video message BEFORE generic video / voice!
        if (MatchesMediaPrefixOrExact(lower, "video message", "видеосообщение", "кружок", "video note"))
            return "Video Message";

        if (MatchesMediaPrefixOrExact(lower, "voice message", "голосовое сообщение", "голосовое", "audio message", "аудиосообщение"))
            return "Voice Message";

        if (MatchesMediaPrefixOrExact(lower, "photo", "фотография", "фото", "image", "изображение", "picture", "картинка"))
            return "Photo";

        if (MatchesMediaPrefixOrExact(lower, "video", "видеозапись", "видео", "ролик"))
            return "Video";

        if (MatchesMediaPrefixOrExact(lower, "sticker", "стикер"))
            return "Sticker";

        if (MatchesMediaPrefixOrExact(lower, "gif", "анимация", "animation"))
            return "GIF";

        if (MatchesMediaPrefixOrExact(lower, "document", "file", "файл", "документ"))
            return "File";

        return string.Empty;
    }

    private static bool MatchesMediaPrefixOrExact(string lower, params string[] keywords)
    {
        foreach (string kw in keywords)
        {
            if (lower == kw ||
                lower.StartsWith(kw + " ", StringComparison.Ordinal) ||
                lower.StartsWith(kw + ",", StringComparison.Ordinal) ||
                lower.StartsWith(kw + ":", StringComparison.Ordinal) ||
                lower.StartsWith(kw + " (", StringComparison.Ordinal) ||
                lower.StartsWith("🎤 " + kw, StringComparison.Ordinal) ||
                lower.StartsWith("📷 " + kw, StringComparison.Ordinal) ||
                lower.StartsWith("📹 " + kw, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static string FormatMediaBodyWithCaption(string body, string contentTag)
    {
        // Strip leading emoji/raw tag only to check if extra caption/author exists
        string trimmed = body.Trim();
        if (trimmed.Equals(contentTag, StringComparison.OrdinalIgnoreCase))
            return contentTag;

        // If body is just translated single keyword like "Фотография" or "Голосовое сообщение", return clean English tag
        string lower = trimmed.ToLowerInvariant();
        if (lower is "голосовое сообщение" or "voice message" or "голосовое" or "audio message" or "аудиосообщение"
            or "видеосообщение" or "video message" or "кружок" or "video note"
            or "фотография" or "фото" or "photo" or "image" or "изображение"
            or "видеозапись" or "видео" or "video"
            or "стикер" or "sticker" or "gif" or "анимация" or "файл" or "документ" or "file" or "document")
        {
            return contentTag;
        }

        return TruncatePreview(trimmed, 96);
    }

    private static string CollapseWhitespace(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var sb = new StringBuilder(input.Length);
        bool prevSpace = false;
        foreach (char c in input.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                if (!prevSpace)
                {
                    sb.Append(' ');
                    prevSpace = true;
                }
            }
            else
            {
                sb.Append(c);
                prevSpace = false;
            }
        }
        return sb.ToString();
    }

    private static string TruncatePreview(string text, int maxLen)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLen)
            return text;

        int cut = text.LastIndexOf(' ', maxLen - 1);
        if (cut < maxLen / 2)
            cut = maxLen - 1;
        return text[..cut].TrimEnd() + "...";
    }

    private static string FriendlyAppName(string rawApp, string aumid)
    {
        string combined = $"{rawApp} {aumid}";
        if (combined.Contains("Telegram", StringComparison.OrdinalIgnoreCase)) return "Telegram";
        if (combined.Contains("Discord", StringComparison.OrdinalIgnoreCase)) return "Discord";
        if (combined.Contains("Slack", StringComparison.OrdinalIgnoreCase)) return "Slack";
        if (combined.Contains("Teams", StringComparison.OrdinalIgnoreCase)) return "Teams";
        if (combined.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase)) return "WhatsApp";
        if (combined.Contains("Firefox", StringComparison.OrdinalIgnoreCase)) return "Firefox";
        if (combined.Contains("Chrome", StringComparison.OrdinalIgnoreCase)) return "Chrome";
        if (combined.Contains("Edge", StringComparison.OrdinalIgnoreCase)) return "Edge";
        if (combined.Contains("Defender", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Безопасность", StringComparison.OrdinalIgnoreCase))
            return LocalizationService.Pick("Windows Security", "Безопасность Windows");

        return rawApp;
    }

    private static NotificationKind GuessKind(string app, string title, string body)
    {
        string combined = $"{app} {title} {body}".ToLowerInvariant();
        if (combined.Contains("ошибк") || combined.Contains("error") || combined.Contains("fail") ||
            combined.Contains("отключен") || combined.Contains("угроз"))
            return NotificationKind.Error;
        if (combined.Contains("предупрежд") || combined.Contains("warning") || combined.Contains("внимание"))
            return NotificationKind.Warning;
        if (combined.Contains("успешно") || combined.Contains("success") || combined.Contains("готово") || combined.Contains("completed"))
            return NotificationKind.Success;
        if (combined.Contains("безопасн") || combined.Contains("defender") || combined.Contains("windows") || combined.Contains("обновлен"))
            return NotificationKind.System;
        return NotificationKind.Info;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_wnfSubscription != IntPtr.Zero)
        {
            try { RtlUnsubscribeWnfStateChangeNotification(_wnfSubscription); } catch { }
            _wnfSubscription = IntPtr.Zero;
        }

        try { _cts?.Cancel(); } catch { }
        try { _workerStdin?.Dispose(); } catch { }
        try
        {
            if (_workerProcess is not null && !_workerProcess.HasExited)
                _workerProcess.Kill(entireProcessTree: true);
        }
        catch { }
        _pollSignal.Dispose();
        _cts?.Dispose();
    }

    private const string WorkerScript = @"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::InputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.Runtime.WindowsRuntime

$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
})[0]

function AwaitWinRT($winRtTask, $resultType) {
    $netTask = $asTaskGeneric.MakeGenericMethod($resultType).Invoke($null, @($winRtTask))
    $netTask.Wait(-1) | Out-Null
    $netTask.Result
}

$null = [Windows.UI.Notifications.Management.UserNotificationListener, Windows.UI.Notifications, ContentType=WindowsRuntime]
$null = [Windows.UI.Notifications.NotificationKinds, Windows.UI.Notifications, ContentType=WindowsRuntime]
$null = [Windows.UI.Notifications.KnownNotificationBindings, Windows.UI.Notifications, ContentType=WindowsRuntime]

$listener = [Windows.UI.Notifications.Management.UserNotificationListener]::Current
$status = $listener.GetAccessStatus()
if ($status -ne [Windows.UI.Notifications.Management.UserNotificationListenerAccessStatus]::Allowed) {
    $reqOp = $listener.RequestAccessAsync()
    $status = AwaitWinRT $reqOp ([Windows.UI.Notifications.Management.UserNotificationListenerAccessStatus])
}
if ($status -ne [Windows.UI.Notifications.Management.UserNotificationListenerAccessStatus]::Allowed) {
    exit 0
}

$listType = [System.Collections.Generic.IReadOnlyList[Windows.UI.Notifications.UserNotification]]
$seen = New-Object 'System.Collections.Generic.HashSet[uint32]'
$isFirst = $true

function ReadSnapshot() {
    $op = $listener.GetNotificationsAsync([Windows.UI.Notifications.NotificationKinds]::Toast)
    $list = AwaitWinRT $op $listType
    $arr = @()
    $sorted = $list | Sort-Object { $_.CreationTime } -Descending
    foreach ($n in $sorted) {
        try {
            $appName = ''
            $aumid = ''
            if ($null -ne $n.AppInfo) {
                $aumid = [string]$n.AppInfo.AppUserModelId
                if ($null -ne $n.AppInfo.DisplayInfo) {
                    $appName = [string]$n.AppInfo.DisplayInfo.DisplayName
                }
            }
            $title = ''
            $body = ''
            if ($null -ne $n.Notification -and $null -ne $n.Notification.Visual) {
                $binding = $n.Notification.Visual.GetBinding([Windows.UI.Notifications.KnownNotificationBindings]::ToastGeneric)
                if ($null -ne $binding) {
                    $texts = @($binding.GetTextElements() | ForEach-Object { [string]$_.Text })
                    if ($texts.Count -ge 1) { $title = $texts[0] }
                    if ($texts.Count -ge 2) { $body = ($texts[1..($texts.Count - 1)] -join ' ') }
                }
            }
            if ([string]::IsNullOrWhiteSpace($title) -and [string]::IsNullOrWhiteSpace($body)) { continue }
            $arr += [pscustomobject]@{
                id    = [uint32]$n.Id
                app   = $appName
                aumid = $aumid
                title = $title
                body  = $body
                ticks = [int64]$n.CreationTime.LocalDateTime.Ticks
            }
        } catch {}
    }
    return ,$arr
}

while ($true) {
    try {
        $all = ReadSnapshot
        if ($isFirst) {
            foreach ($item in $all) { [void]$seen.Add([uint32]$item.id) }
            $initBatch = @($all | Select-Object -First 25)
            Write-Output (@{ init = $true; items = $initBatch } | ConvertTo-Json -Compress -Depth 4)
            $isFirst = $false
        } else {
            $newItems = @()
            foreach ($item in $all) {
                if ($seen.Add([uint32]$item.id)) {
                    $newItems += $item
                }
            }
            if ($newItems.Count -gt 0) {
                Write-Output (@{ init = $false; items = $newItems } | ConvertTo-Json -Compress -Depth 4)
            }
            if ($seen.Count -gt 500) {
                $seen.Clear()
                foreach ($item in $all) { [void]$seen.Add([uint32]$item.id) }
            }
        }
    } catch {}

    # Block cleanly until C# sends 'POLL' (triggered instantaneously by WNF or heartbeat)
    $cmd = [Console]::In.ReadLine()
    if ($null -eq $cmd) { break }
}
";
}
