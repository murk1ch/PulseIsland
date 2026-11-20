using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using PulseIsland.Core;

namespace PulseIsland.Services;

/// <summary>
/// Suppresses default Windows bottom-right toast popups via the native Focus Assist COM API
/// (IQuietHoursSettings -> Microsoft.QuietHoursProfile.AlarmsOnly) so notifications arrive
/// exclusively inside PulseIsland while still flowing through Action Center / UserNotificationListener.
/// Persists the pre-suppression Focus Assist profile to portable config so it can always be restored.
/// </summary>
public sealed class WindowsToastSuppressor : IDisposable
{
    private const string AlarmsOnlyProfile = "Microsoft.QuietHoursProfile.AlarmsOnly";
    private const string UnrestrictedProfile = "Microsoft.QuietHoursProfile.Unrestricted";
    private const string PushNotificationsRegKey = @"Software\Microsoft\Windows\CurrentVersion\PushNotifications";
    private const string ToastEnabledValue = "ToastEnabled";

    private static readonly Guid ClsidQuietHoursSettings = new("F53321FA-34F8-4B7F-B9A3-361877CB94CF");

    [ComImport]
    [Guid("6BFF4732-81EC-4FFB-AE67-B6C1BC29631F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IQuietHoursSettings
    {
        [PreserveSig] int get_UserSelectedProfile([MarshalAs(UnmanagedType.LPWStr)] out string profileId);
        [PreserveSig] int put_UserSelectedProfile([MarshalAs(UnmanagedType.LPWStr)] string profileId);
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const int SW_HIDE = 0;

    private readonly string _restoreFilePath;
    private bool _isCurrentlySuppressed;
    private bool _disposed;

    public WindowsToastSuppressor()
    {
        PortablePaths.EnsureDirectoriesExist();
        _restoreFilePath = Path.Combine(PortablePaths.ConfigDirectory, "quiethours_prev.txt");
    }

    public void SetSuppression(bool suppress)
    {
        if (suppress)
            EnableSuppression();
        else
            RestoreOriginal();
    }

    private void EnableSuppression()
    {
        try
        {
            // Ensure Windows still delivers notifications to Action Center / UserNotificationListener
            // (a prior version wrote ToastEnabled=0 which blocks delivery on some builds).
            EnsureToastDeliveryEnabled();

            string? currentProfile = GetQuietHoursProfile();
            if (!string.IsNullOrWhiteSpace(currentProfile) &&
                !string.Equals(currentProfile, AlarmsOnlyProfile, StringComparison.OrdinalIgnoreCase))
            {
                if (!File.Exists(_restoreFilePath))
                    File.WriteAllText(_restoreFilePath, currentProfile, Encoding.UTF8);
            }
            else if (!File.Exists(_restoreFilePath))
            {
                File.WriteAllText(_restoreFilePath, UnrestrictedProfile, Encoding.UTF8);
            }

            if (!_isCurrentlySuppressed || !string.Equals(currentProfile, AlarmsOnlyProfile, StringComparison.OrdinalIgnoreCase))
            {
                if (SetQuietHoursProfile(AlarmsOnlyProfile))
                {
                    _isCurrentlySuppressed = true;
                    LogService.Info("WindowsToastSuppressor: Focus Assist set to AlarmsOnly (toasts routed to island only).");
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Error("WindowsToastSuppressor: Failed to enable suppression.", ex);
        }
    }

    public void RestoreOriginal()
    {
        try
        {
            string targetProfile = UnrestrictedProfile;
            if (File.Exists(_restoreFilePath))
            {
                string saved = File.ReadAllText(_restoreFilePath, Encoding.UTF8).Trim();
                if (!string.IsNullOrWhiteSpace(saved) &&
                    !string.Equals(saved, AlarmsOnlyProfile, StringComparison.OrdinalIgnoreCase))
                {
                    targetProfile = saved;
                }
            }

            string? currentProfile = GetQuietHoursProfile();
            if (_isCurrentlySuppressed || string.Equals(currentProfile, AlarmsOnlyProfile, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(_restoreFilePath))
                {
                    SetQuietHoursProfile(targetProfile);
                    LogService.Info($"WindowsToastSuppressor: Focus Assist restored to {targetProfile}.");
                }
            }

            if (File.Exists(_restoreFilePath))
                File.Delete(_restoreFilePath);

            _isCurrentlySuppressed = false;
        }
        catch (Exception ex)
        {
            LogService.Error("WindowsToastSuppressor: Failed to restore Focus Assist.", ex);
        }
    }

    private static void EnsureToastDeliveryEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PushNotificationsRegKey, writable: true);
            if (key?.GetValue(ToastEnabledValue) is int val && val == 0)
            {
                key.SetValue(ToastEnabledValue, 1, RegistryValueKind.DWord);
                LogService.Info("WindowsToastSuppressor: Restored PushNotifications\\ToastEnabled=1 so WinRT listener receives notifications.");
            }
        }
        catch { }
    }

    private static string? GetQuietHoursProfile()
    {
        Type? comType = Type.GetTypeFromCLSID(ClsidQuietHoursSettings);
        if (comType is null) return null;

        object? instance = null;
        try
        {
            instance = Activator.CreateInstance(comType);
            if (instance is IQuietHoursSettings qh && qh.get_UserSelectedProfile(out string profile) == 0)
                return profile;
        }
        catch (Exception ex)
        {
            LogService.Error("WindowsToastSuppressor: get_UserSelectedProfile failed.", ex);
        }
        finally
        {
            if (instance is not null && Marshal.IsComObject(instance))
                Marshal.ReleaseComObject(instance);
        }
        return null;
    }

    private static bool SetQuietHoursProfile(string profileId)
    {
        Type? comType = Type.GetTypeFromCLSID(ClsidQuietHoursSettings);
        if (comType is null) return false;

        object? instance = null;
        try
        {
            instance = Activator.CreateInstance(comType);
            if (instance is IQuietHoursSettings qh)
                return qh.put_UserSelectedProfile(profileId) == 0;
        }
        catch (Exception ex)
        {
            LogService.Error($"WindowsToastSuppressor: put_UserSelectedProfile({profileId}) failed.", ex);
        }
        finally
        {
            if (instance is not null && Marshal.IsComObject(instance))
                Marshal.ReleaseComObject(instance);
        }
        return false;
    }

    /// <summary>
    /// Fallback guard that hides any transient Windows toast CoreWindow appearing in the bottom-right corner.
    /// </summary>
    public static void SuppressVisibleToastWindows()
    {
        try
        {
            int screenWidth = (int)System.Windows.SystemParameters.PrimaryScreenWidth;
            int screenHeight = (int)System.Windows.SystemParameters.PrimaryScreenHeight;
            int rightThreshold = screenWidth - 480;
            int bottomThreshold = screenHeight - 360;

            EnumWindows((hWnd, _) =>
            {
                var sb = new StringBuilder(256);
                GetClassName(hWnd, sb, sb.Capacity);
                if (sb.ToString() == "Windows.UI.Core.CoreWindow")
                {
                    GetWindowThreadProcessId(hWnd, out uint pid);
                    try
                    {
                        using var proc = Process.GetProcessById((int)pid);
                        if (proc.ProcessName.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase) &&
                            GetWindowRect(hWnd, out RECT rect) &&
                            rect.Left >= rightThreshold && rect.Top >= bottomThreshold)
                        {
                            ShowWindow(hWnd, SW_HIDE);
                        }
                    }
                    catch { }
                }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RestoreOriginal();
    }
}
