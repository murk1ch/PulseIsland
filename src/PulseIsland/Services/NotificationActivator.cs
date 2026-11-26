using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using PulseIsland.Core;

namespace PulseIsland.Services;

/// <summary>
/// Opens the specific chat or conversation when a user clicks a notification in PulseIsland.
/// Supports Telegram Desktop (including waking from tray, UIA Search + Esc/Ctrl+F fallback, and tg:// links),
/// Discord Quick Switcher (Ctrl+K), and AUMID/window activation for other apps.
/// </summary>
public static partial class NotificationActivator
{
    private static readonly Guid ClsidApplicationActivationManager = new("45BA127D-10A8-46EA-8AB7-56EA9078943C");

    [ComImport]
    [Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments,
            uint options,
            out uint processId);
    }

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const byte VK_CONTROL = 0x11;
    private const byte VK_RETURN = 0x0D;
    private const byte VK_DOWN = 0x28;
    private const byte VK_A = 0x41;
    private const byte VK_K = 0x4B;
    private const byte VK_V = 0x56;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [GeneratedRegex(@"@([A-Za-z0-9_]{4,32})")]
    private static partial Regex TelegramUsernameRegex();

    public static void Activate(IslandNotification notification)
    {
        if (notification is null) return;

        bool isTelegram = notification.AppName.Contains("Telegram", StringComparison.OrdinalIgnoreCase) ||
                          notification.AppUserModelId.Contains("Telegram", StringComparison.OrdinalIgnoreCase);
        if (isTelegram)
        {
            _ = ActivateTelegramChatAsync(notification);
            return;
        }

        bool isDiscord = notification.AppName.Contains("Discord", StringComparison.OrdinalIgnoreCase) ||
                         notification.AppUserModelId.Contains("Discord", StringComparison.OrdinalIgnoreCase);
        if (isDiscord)
        {
            _ = ActivateDiscordChatAsync(notification);
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                if (TryFocusRunningProcessByName(notification.AppName))
                    return;

                if (!string.IsNullOrWhiteSpace(notification.AppUserModelId))
                    ActivateByAumid(notification.AppUserModelId);
            }
            catch (Exception ex)
            {
                LogService.Error($"NotificationActivator failed for {notification.AppName}.", ex);
            }
        });
    }

    private static async Task ActivateTelegramChatAsync(IslandNotification notification)
    {
        try
        {
            string searchTarget = ExtractTelegramChatTarget(notification);

            var usernameMatch = TelegramUsernameRegex().Match(searchTarget);
            if (usernameMatch.Success)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = $"tg://resolve?domain={usernameMatch.Groups[1].Value}",
                    UseShellExecute = true
                });
                return;
            }

            IntPtr tgHwnd = await WakeAndFindWindowAsync("Telegram", notification.AppUserModelId).ConfigureAwait(false);
            if (tgHwnd == IntPtr.Zero)
                return;

            if (!await BringWindowAndVerifyForegroundAsync(tgHwnd).ConfigureAwait(false))
                return;

            if (string.IsNullOrWhiteSpace(searchTarget) ||
                searchTarget.Contains("Новое уведомление", StringComparison.OrdinalIgnoreCase) ||
                searchTarget.Contains("New notification", StringComparison.OrdinalIgnoreCase) ||
                searchTarget.Equals("Telegram", StringComparison.OrdinalIgnoreCase) ||
                searchTarget.Equals("Telegram Desktop", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Re-check foreground strictly before typing into any window
            if (GetForegroundWindow() != tgHwnd)
                return;

            bool usedUiaEdit = TryFocusTelegramSearchEdit(tgHwnd, searchTarget);
            if (!usedUiaEdit)
            {
                // Qt Desktop fallback: click Telegram's top-left Global Search box (175, 50 DIP) so Ctrl+A / paste
                // focuses chat search safely in both folder-rail and compact-rail layouts without risking Esc-to-tray
                ClickTelegramGlobalSearchBox(tgHwnd);
                await Task.Delay(120).ConfigureAwait(false);
                if (GetForegroundWindow() != tgHwnd) return;

                await PasteTextSafeAsync(searchTarget).ConfigureAwait(false);
            }

            // Wait for Telegram chat search filter, select top matching chat with Down, then open with Enter
            await Task.Delay(340).ConfigureAwait(false);
            if (GetForegroundWindow() != tgHwnd) return;

            SendKey(VK_DOWN);
            await Task.Delay(75).ConfigureAwait(false);
            SendKey(VK_RETURN);
        }
        catch (Exception ex)
        {
            LogService.Error("Failed to open Telegram chat from notification.", ex);
        }
    }

    private static async Task ActivateDiscordChatAsync(IslandNotification notification)
    {
        try
        {
            string target = ExtractDiscordChatTarget(notification);
            IntPtr discordHwnd = await WakeAndFindWindowAsync("Discord", notification.AppUserModelId).ConfigureAwait(false);
            if (discordHwnd == IntPtr.Zero)
                return;

            if (!await BringWindowAndVerifyForegroundAsync(discordHwnd).ConfigureAwait(false))
                return;

            if (string.IsNullOrWhiteSpace(target) ||
                target.Equals("Discord", StringComparison.OrdinalIgnoreCase) ||
                target.Contains("Новое уведомление", StringComparison.OrdinalIgnoreCase) ||
                target.Contains("New notification", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await Task.Delay(140).ConfigureAwait(false);
            if (GetForegroundWindow() != discordHwnd) return;

            // Ctrl+K opens Discord Quick Switcher across all DMs, servers & channels
            SendCombo(VK_CONTROL, VK_K);
            await Task.Delay(160).ConfigureAwait(false);
            if (GetForegroundWindow() != discordHwnd) return;

            await PasteTextSafeAsync(target).ConfigureAwait(false);
            await Task.Delay(280).ConfigureAwait(false);
            if (GetForegroundWindow() != discordHwnd) return;

            SendKey(VK_RETURN);
        }
        catch (Exception ex)
        {
            LogService.Error("Failed to open Discord chat from notification.", ex);
        }
    }

    private static async Task<IntPtr> WakeAndFindWindowAsync(string processName, string aumid)
    {
        IntPtr hwnd = FindMainWindow(processName);
        if (hwnd != IntPtr.Zero)
            return hwnd;

        // If minimized to tray (MainWindowHandle == 0), re-launching running exe wakes the single instance immediately
        try
        {
            var running = Process.GetProcessesByName(processName).FirstOrDefault();
            string? exePath = running?.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(exePath))
            {
                Process.Start(new ProcessStartInfo { FileName = exePath, UseShellExecute = true });
            }
            else if (!string.IsNullOrWhiteSpace(aumid))
            {
                ActivateByAumid(aumid);
            }
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(aumid))
                ActivateByAumid(aumid);
        }

        for (int i = 0; i < 14; i++)
        {
            await Task.Delay(120).ConfigureAwait(false);
            hwnd = FindMainWindow(processName);
            if (hwnd != IntPtr.Zero)
                return hwnd;
        }
        return IntPtr.Zero;
    }

    public static string ExtractTelegramChatTarget(IslandNotification notification)
    {
        string raw = !string.IsNullOrWhiteSpace(notification.SenderHint)
            ? notification.SenderHint.Trim()
            : notification.Title.Trim();

        int arrowIdx = raw.IndexOf("→", StringComparison.Ordinal);
        if (arrowIdx >= 0 && arrowIdx + 1 < raw.Length)
            return raw[(arrowIdx + 1)..].Trim();

        arrowIdx = raw.IndexOf("->", StringComparison.Ordinal);
        if (arrowIdx >= 0 && arrowIdx + 2 < raw.Length)
            return raw[(arrowIdx + 2)..].Trim();

        int colonIdx = raw.IndexOf(':');
        if (colonIdx > 1 && !raw.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            raw = raw[..colonIdx].Trim();

        return raw;
    }

    public static string ExtractDiscordChatTarget(IslandNotification notification)
    {
        string raw = !string.IsNullOrWhiteSpace(notification.SenderHint)
            ? notification.SenderHint.Trim()
            : notification.Title.Trim();

        // Discord formats group/channel toasts like "User (#channel, Server)" or "#channel, Server"
        int openParen = raw.IndexOf("(#", StringComparison.Ordinal);
        if (openParen >= 0)
        {
            int comma = raw.IndexOf(',', openParen);
            int end = comma > openParen ? comma : raw.IndexOf(')', openParen);
            if (end > openParen + 1)
                return raw[(openParen + 1)..end].Trim();
        }

        int commaIdx = raw.IndexOf(',');
        if (commaIdx > 1)
            return raw[..commaIdx].Trim();

        return raw;
    }

    private static bool TryFocusTelegramSearchEdit(IntPtr tgHwnd, string query)
    {
        try
        {
            var root = AutomationElement.FromHandle(tgHwnd);
            if (root is null) return false;

            GetWindowRect(tgHwnd, out RECT winRect);
            var edits = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));

            AutomationElement? searchBox = null;
            double bestScore = double.MaxValue;

            foreach (AutomationElement edit in edits)
            {
                try
                {
                    Rect r = edit.Current.BoundingRectangle;
                    if (r.IsEmpty || edit.Current.IsOffscreen) continue;

                    double relX = r.Left - winRect.Left;
                    double relY = r.Top - winRect.Top;
                    if (relY < 180 && relX < 460)
                    {
                        double score = relY + relX * 0.25;
                        if (score < bestScore)
                        {
                            bestScore = score;
                            searchBox = edit;
                        }
                    }
                }
                catch { }
            }

            if (searchBox is null)
                return false;

            searchBox.SetFocus();
            if (searchBox.TryGetCurrentPattern(ValuePattern.Pattern, out object vpObj) &&
                vpObj is ValuePattern vp && !vp.Current.IsReadOnly)
            {
                vp.SetValue(query);
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static void ClickTelegramGlobalSearchBox(IntPtr tgHwnd)
    {
        if (!GetWindowRect(tgHwnd, out RECT rect))
            return;

        uint dpi = GetDpiForWindow(tgHwnd);
        double scale = dpi >= 96 ? dpi / 96.0 : 1.0;

        int clickX = rect.Left + (int)Math.Round(175 * scale);
        int clickY = rect.Top + (int)Math.Round(50 * scale);
        if (clickX >= rect.Right - 40 || clickY >= rect.Bottom - 40)
            return;

        SetCursorPos(clickX, clickY);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
    }

    private static async Task PasteTextSafeAsync(string text)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        string? previousText = null;
        await dispatcher.InvokeAsync(() =>
        {
            try
            {
                if (System.Windows.Clipboard.ContainsText())
                    previousText = System.Windows.Clipboard.GetText();
                System.Windows.Clipboard.SetText(text);
            }
            catch { }

            SendCombo(VK_CONTROL, VK_A);
            SendCombo(VK_CONTROL, VK_V);
        });

        await Task.Delay(120).ConfigureAwait(false);

        if (previousText is not null)
        {
            _ = dispatcher.InvokeAsync(() =>
            {
                try { System.Windows.Clipboard.SetText(previousText); } catch { }
            });
        }
    }

    private static IntPtr FindMainWindow(string processName)
    {
        foreach (var proc in Process.GetProcessesByName(processName))
        {
            if (proc.MainWindowHandle != IntPtr.Zero && IsWindowVisible(proc.MainWindowHandle))
                return proc.MainWindowHandle;
        }
        return IntPtr.Zero;
    }

    private static bool TryFocusRunningProcessByName(string appName)
    {
        string[] candidates = appName switch
        {
            var a when a.Contains("Discord", StringComparison.OrdinalIgnoreCase) => ["Discord"],
            var a when a.Contains("Steam", StringComparison.OrdinalIgnoreCase) => ["steam"],
            var a when a.Contains("Chrome", StringComparison.OrdinalIgnoreCase) => ["chrome"],
            var a when a.Contains("Firefox", StringComparison.OrdinalIgnoreCase) => ["firefox"],
            var a when a.Contains("Edge", StringComparison.OrdinalIgnoreCase) => ["msedge"],
            var a when a.Contains("Antigravity", StringComparison.OrdinalIgnoreCase) => ["Antigravity"],
            _ => [appName]
        };

        foreach (string name in candidates)
        {
            IntPtr hwnd = FindMainWindow(name);
            if (hwnd != IntPtr.Zero)
            {
                ForceForegroundWindow(hwnd);
                return true;
            }
        }
        return false;
    }

    private static async Task<bool> BringWindowAndVerifyForegroundAsync(IntPtr hwnd)
    {
        ForceForegroundWindow(hwnd);
        for (int i = 0; i < 8; i++)
        {
            if (GetForegroundWindow() == hwnd)
                return true;
            await Task.Delay(70).ConfigureAwait(false);
            ForceForegroundWindow(hwnd);
        }
        return GetForegroundWindow() == hwnd;
    }

    private static void ForceForegroundWindow(IntPtr hwnd)
    {
        IntPtr fg = GetForegroundWindow();
        uint fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out _) : 0;
        uint curThread = GetCurrentThreadId();
        bool attached = false;

        try
        {
            if (fgThread != 0 && fgThread != curThread)
                attached = AttachThreadInput(curThread, fgThread, true);

            ShowWindow(hwnd, IsIconic(hwnd) ? SW_RESTORE : SW_SHOW);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
                AttachThreadInput(curThread, fgThread, false);
        }
    }

    private static void ActivateByAumid(string aumid)
    {
        Type? comType = Type.GetTypeFromCLSID(ClsidApplicationActivationManager);
        if (comType is null) return;

        object? obj = null;
        try
        {
            obj = Activator.CreateInstance(comType);
            if (obj is IApplicationActivationManager mgr)
                mgr.ActivateApplication(aumid, null, 0, out _);
        }
        finally
        {
            if (obj is not null && Marshal.IsComObject(obj))
                Marshal.ReleaseComObject(obj);
        }
    }

    private static void SendKey(byte vk)
    {
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private static void SendCombo(byte mod, byte vk)
    {
        keybd_event(mod, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(mod, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }
}
