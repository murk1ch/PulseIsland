using System.IO;
using Microsoft.Win32;

namespace PulseIsland.Services;

/// <summary>
/// Manages Windows startup registration in HKCU\Software\Microsoft\Windows\CurrentVersion\Run
/// pointing strictly to the executable inside <portable-root> without writing files to drive C:.
/// </summary>
public static class AutoStartService
{
    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppValueName = "PulseIsland";

    public static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: false);
            var val = key?.GetValue(AppValueName) as string;
            return !string.IsNullOrWhiteSpace(val);
        }
        catch (Exception ex)
        {
            LogService.Warn($"Could not read autostart registry key: {ex.Message}");
            return false;
        }
    }

    public static bool SetAutoStart(bool enable, string? customExePath = null)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: true);
            if (key is null)
                return false;

            if (!enable)
            {
                if (key.GetValue(AppValueName) is not null)
                    key.DeleteValue(AppValueName, throwOnMissingValue: false);
                return true;
            }

            string exePath = customExePath ?? ResolveExecutablePath();
            key.SetValue(AppValueName, $"\"{exePath}\"");
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("Failed to update Windows autostart registry setting.", ex);
            return false;
        }
    }

    public static string ResolveExecutablePath()
    {
        var rootExe = Path.Combine(Core.PortablePaths.RootDirectory, "PulseIsland.exe");
        if (File.Exists(rootExe))
            return rootExe;

        var currentProcessExe = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(currentProcessExe) && File.Exists(currentProcessExe))
            return currentProcessExe;

        return rootExe;
    }
}
