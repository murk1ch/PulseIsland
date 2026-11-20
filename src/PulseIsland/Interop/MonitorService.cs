using System.Windows;
using System.Windows.Forms;
using System.Windows.Media;

namespace PulseIsland.Interop;

public sealed record MonitorBounds(
    int Index,
    string DeviceName,
    Rect PhysicalBounds,
    Rect DipBounds,
    double DpiScaleX,
    double DpiScaleY);

public static class MonitorService
{
    public static IReadOnlyList<MonitorBounds> GetAllMonitors(Visual? referenceVisual = null)
    {
        var screens = Screen.AllScreens;
        var result = new List<MonitorBounds>(screens.Length);

        var (fallbackScaleX, fallbackScaleY) = GetVisualDpiScale(referenceVisual);

        for (int i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            var (scaleX, scaleY) = GetMonitorDpiScale(s, fallbackScaleX, fallbackScaleY);

            var physical = new Rect(s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height);
            var dip = new Rect(
                s.Bounds.X / scaleX,
                s.Bounds.Y / scaleY,
                s.Bounds.Width / scaleX,
                s.Bounds.Height / scaleY);

            result.Add(new MonitorBounds(i, s.DeviceName, physical, dip, scaleX, scaleY));
        }

        return result;
    }

    public static MonitorBounds GetTargetMonitor(int monitorIndex, Visual? referenceVisual = null)
    {
        var all = GetAllMonitors(referenceVisual);
        if (all.Count == 0)
            return new MonitorBounds(0, "PRIMARY", new Rect(0, 0, 1920, 1080), new Rect(0, 0, 1920, 1080), 1.0, 1.0);

        if (monitorIndex >= 0 && monitorIndex < all.Count)
            return all[monitorIndex];

        return all[0];
    }

    public static MonitorBounds GetMonitorForPhysicalPoint(int physicalX, int physicalY, Visual? referenceVisual = null)
    {
        var screens = Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            if (screens[i].Bounds.Contains(physicalX, physicalY))
            {
                return GetTargetMonitor(i, referenceVisual);
            }
        }
        return GetTargetMonitor(0, referenceVisual);
    }

    public static Rect GetVirtualScreenPhysicalBounds()
    {
        var vs = SystemInformation.VirtualScreen;
        return new Rect(vs.X, vs.Y, vs.Width, vs.Height);
    }

    public static (double ScaleX, double ScaleY) GetMonitorDpiScale(Screen screen, double fallbackX, double fallbackY)
    {
        try
        {
            var pt = new Win32Api.POINT
            {
                X = screen.Bounds.Left + screen.Bounds.Width / 2,
                Y = screen.Bounds.Top + screen.Bounds.Height / 2
            };
            IntPtr hMon = Win32Api.MonitorFromPoint(pt, 2 /* MONITOR_DEFAULTTONEAREST */);
            if (hMon != IntPtr.Zero && Win32Api.GetDpiForMonitor(hMon, Win32Api.MDT_EFFECTIVE_DPI, out uint dpiX, out uint dpiY) == 0)
            {
                if (dpiX >= 72 && dpiY >= 72)
                    return (dpiX / 96.0, dpiY / 96.0);
            }
        }
        catch
        {
            // Older Windows fallback
        }

        return (fallbackX, fallbackY);
    }

    public static (double ScaleX, double ScaleY) GetVisualDpiScale(Visual? visual)
    {
        if (visual is not null)
        {
            var source = PresentationSource.FromVisual(visual);
            if (source?.CompositionTarget is not null)
            {
                var m = source.CompositionTarget.TransformToDevice;
                return (m.M11, m.M22);
            }

            var dpi = VisualTreeHelper.GetDpi(visual);
            return (dpi.DpiScaleX, dpi.DpiScaleY);
        }

        return (1.0, 1.0);
    }

    public static bool IsForegroundWindowFullscreen(MonitorBounds monitor, IntPtr ownWindowHandle)
    {
        IntPtr fg = Win32Api.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == ownWindowHandle)
            return false;

        if (fg == Win32Api.GetDesktopWindow() || fg == Win32Api.GetShellWindow())
            return false;

        string className = Win32Api.GetWindowClassName(fg);
        if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow")
            return false;

        // Standard maximized windows still have WS_CAPTION; only true borderless/exclusive fullscreen apps lack it
        int style = Win32Api.GetWindowLong(fg, Win32Api.GWL_STYLE);
        if ((style & Win32Api.WS_CAPTION) != 0)
            return false;

        if (!Win32Api.GetWindowRect(fg, out var rect))
            return false;

        var pb = monitor.PhysicalBounds;
        return rect.Left <= pb.Left &&
               rect.Top <= pb.Top &&
               rect.Right >= pb.Right &&
               rect.Bottom >= pb.Bottom;
    }

    public static bool IsDesktopActive()
    {
        IntPtr fg = Win32Api.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        if (fg == Win32Api.GetDesktopWindow() || fg == Win32Api.GetShellWindow())
            return true;

        string className = Win32Api.GetWindowClassName(fg);
        return className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }
}
