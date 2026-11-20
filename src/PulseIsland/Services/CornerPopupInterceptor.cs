using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using PulseIsland.Core;

namespace PulseIsland.Services;

/// <summary>
/// Event-driven Win32 SetWinEventHook(EVENT_OBJECT_SHOW) interceptor for custom bottom-right
/// desktop toast popups (e.g., Steam, Discord, or custom Qt/Electron popups that bypass Windows Action Center).
/// Zero background polling timer: fires instantaneously when a matching popup window is shown.
/// </summary>
public sealed class CornerPopupInterceptor : IDisposable
{
    private delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int maxCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const int OBJID_WINDOW = 0;
    private const int CHILDID_SELF = 0;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int SW_HIDE = 0;

    private static readonly HashSet<string> KnownPopupProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Telegram",
        "Discord",
        "steam",
        "steamwebhelper",
        "Viber",
        "WhatsApp",
        "Slack",
        "Skype",
        "ShellExperienceHost"
    };

    private static readonly HashSet<string> IgnoredProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "PulseIsland",
        "explorer",
        "SearchHost",
        "StartMenuExperienceHost",
        "Taskmgr"
    };

    private readonly IslandNotificationService _notificationService;
    private readonly Func<bool> _isEnabled;
    private readonly Func<double> _getAutoDismissSeconds;
    private readonly Dictionary<IntPtr, DateTime> _suppressedHandles = new();
    private readonly Dictionary<string, DateTime> _recentContentDedupe = new();
    private readonly object _stateLock = new();

    private WinEventDelegate? _winEventDelegate;
    private IntPtr _winEventHook;
    private bool _disposed;

    public CornerPopupInterceptor(
        IslandNotificationService notificationService,
        Func<bool> isEnabled,
        Func<double> getAutoDismissSeconds)
    {
        _notificationService = notificationService;
        _isEnabled = isEnabled;
        _getAutoDismissSeconds = getAutoDismissSeconds;
    }

    /// <summary>
    /// Installs an out-of-context WinEventHook on the WPF UI thread's active message loop.
    /// </summary>
    public void Start(Dispatcher dispatcher)
    {
        if (_disposed || _winEventHook != IntPtr.Zero)
            return;

        dispatcher.InvokeAsync(() =>
        {
            if (_disposed || _winEventHook != IntPtr.Zero)
                return;

            _winEventDelegate = OnWinEventReceived;
            _winEventHook = SetWinEventHook(
                EVENT_OBJECT_SHOW,
                EVENT_OBJECT_SHOW,
                IntPtr.Zero,
                _winEventDelegate,
                0,
                0,
                WINEVENT_OUTOFCONTEXT);

            if (_winEventHook != IntPtr.Zero)
                LogService.Info("CornerPopupInterceptor: SetWinEventHook(EVENT_OBJECT_SHOW) active.");
        });
    }

    private void OnWinEventReceived(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (_disposed || idObject != OBJID_WINDOW || idChild != CHILDID_SELF || hwnd == IntPtr.Zero)
            return;

        if (!_isEnabled())
            return;

        try
        {
            if (hwnd == GetForegroundWindow() || !IsWindowVisible(hwnd) || IsIconic(hwnd))
                return;

            if (!GetWindowRect(hwnd, out RECT rect))
                return;

            int w = rect.Right - rect.Left;
            int h = rect.Bottom - rect.Top;
            if (w < 170 || w > 640 || h < 42 || h > 240)
                return;

            if (!IsInAnyBottomRightCorner(rect))
                return;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return;

            string procName;
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                procName = proc.ProcessName;
            }
            catch
            {
                return;
            }

            if (IgnoredProcesses.Contains(procName))
                return;

            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            bool hasPopupStyle = (exStyle & (WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0;
            if (!KnownPopupProcesses.Contains(procName) && !hasPopupStyle)
                return;

            var clsSb = new StringBuilder(128);
            GetClassName(hwnd, clsSb, clsSb.Capacity);
            string cls = clsSb.ToString();
            if (cls is "Shell_TrayWnd" or "NotifyIconOverflowWindow" or "Progman" or "Tooltips_class32")
                return;

            // Hide instantaneously before slide-in frame finishes
            ShowWindow(hwnd, SW_HIDE);

            DateTime now = DateTime.UtcNow;
            lock (_stateLock)
            {
                foreach (var kv in _suppressedHandles.Where(k => (now - k.Value).TotalSeconds > 12).ToList())
                    _suppressedHandles.Remove(kv.Key);

                if (_suppressedHandles.TryGetValue(hwnd, out var seenAt) && (now - seenAt).TotalSeconds < 6)
                    return;

                _suppressedHandles[hwnd] = now;
            }

            if (procName.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase))
                return;

            var txtSb = new StringBuilder(256);
            GetWindowText(hwnd, txtSb, txtSb.Capacity);
            string winText = txtSb.ToString().Trim();

            _ = Task.Run(() => ExtractAndPushPopup(hwnd, procName, winText, now));
        }
        catch
        {
            // Never throw inside a Win32 WinEventHook callback
        }
    }

    private void ExtractAndPushPopup(IntPtr hWnd, string procName, string winText, DateTime now)
    {
        try
        {
            var (rawTitle, rawBody) = ExtractPopupTextViaUia(hWnd, procName, winText);
            string friendlyApp = FormatFriendlyAppName(procName);
            string fallbackTitle = string.IsNullOrWhiteSpace(rawTitle)
                ? LocalizationService.Pick($"New notification ({friendlyApp})", $"Новое уведомление ({friendlyApp})")
                : rawTitle;
            var (title, body, contentTag, senderHint) =
                WindowsNotificationListener.FormatNotificationContent(friendlyApp, fallbackTitle, rawBody);

            string dedupKey = $"{friendlyApp}|{title}|{body}";
            lock (_stateLock)
            {
                foreach (var kv in _recentContentDedupe.Where(k => (now - k.Value).TotalSeconds > 8).ToList())
                    _recentContentDedupe.Remove(kv.Key);

                if (_recentContentDedupe.ContainsKey(dedupKey))
                    return;

                _recentContentDedupe[dedupKey] = now;
            }

            _notificationService.Push(new IslandNotification(
                Id: Guid.NewGuid().ToString("N")[..8],
                AppName: friendlyApp,
                Title: title,
                Body: body,
                Kind: GuessKind(friendlyApp, title, body),
                Timestamp: DateTime.Now,
                AutoDismissSeconds: _getAutoDismissSeconds(),
                ContentTag: contentTag,
                SenderHint: senderHint));
        }
        catch (Exception ex)
        {
            LogService.Error("CornerPopupInterceptor extract error.", ex);
        }
    }

    private static bool IsInAnyBottomRightCorner(RECT rect)
    {
        foreach (var scr in System.Windows.Forms.Screen.AllScreens)
        {
            var wa = scr.Bounds;
            if (rect.Right >= wa.Right - 200 && rect.Right <= wa.Right + 20 &&
                rect.Left >= wa.Right - 680 &&
                rect.Top >= wa.Bottom - 540 && rect.Bottom <= wa.Bottom + 40)
            {
                return true;
            }
        }
        return false;
    }

    private static (string title, string body) ExtractPopupTextViaUia(IntPtr hWnd, string procName, string fallbackWindowText)
    {
        var texts = new List<string>();
        try
        {
            var root = AutomationElement.FromHandle(hWnd);
            if (root is not null)
            {
                var cond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text);
                var found = root.FindAll(TreeScope.Descendants, cond);
                foreach (AutomationElement el in found)
                {
                    string name = el.Current.Name?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (name is "×" or "X" or "x" or "Закрыть" or "Close") continue;
                    if (!texts.Contains(name, StringComparer.Ordinal))
                        texts.Add(name);
                    if (texts.Count >= 4) break;
                }
            }
        }
        catch { }

        if (texts.Count == 0 && !string.IsNullOrWhiteSpace(fallbackWindowText) &&
            !fallbackWindowText.Equals(procName, StringComparison.OrdinalIgnoreCase))
        {
            texts.Add(fallbackWindowText);
        }

        if (texts.Count == 0)
            return (string.Empty, string.Empty);
        if (texts.Count == 1)
            return (texts[0], string.Empty);

        return (texts[0], string.Join(" • ", texts.Skip(1)));
    }

    private static string FormatFriendlyAppName(string procName)
    {
        if (procName.Equals("Telegram", StringComparison.OrdinalIgnoreCase)) return "Telegram";
        if (procName.Equals("Discord", StringComparison.OrdinalIgnoreCase)) return "Discord";
        if (procName.StartsWith("steam", StringComparison.OrdinalIgnoreCase)) return "Steam";
        return procName;
    }

    private static NotificationKind GuessKind(string app, string title, string body)
    {
        string text = $"{app} {title} {body}".ToLowerInvariant();
        if (text.Contains("ошибк") || text.Contains("error")) return NotificationKind.Error;
        if (text.Contains("предупрежд") || text.Contains("warning")) return NotificationKind.Warning;
        return NotificationKind.Info;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_winEventHook != IntPtr.Zero)
        {
            try { UnhookWinEvent(_winEventHook); } catch { }
            _winEventHook = IntPtr.Zero;
        }
        _winEventDelegate = null;
    }
}
