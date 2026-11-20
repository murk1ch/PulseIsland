using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace PulseIsland.Interop;

/// <summary>
/// Applies real Windows 10/11 DWM backdrop blur (SetWindowCompositionAttribute + ACCENT_ENABLE_BLURBEHIND)
/// strictly clipped to the rounded bounds of a target element via SetWindowRgn.
/// (DwmEnableBlurBehindWindow is deprecated on Windows 10/11 and no longer blurs behind windows.)
/// </summary>
public static class DwmBackdropService
{
    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_DISABLED = 0;
    private const int ACCENT_ENABLE_BLURBEHIND = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(
        int nLeftRect, int nTopRect, int nRightRect, int nBottomRect,
        int nWidthEllipse, int nHeightEllipse);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, [MarshalAs(UnmanagedType.Bool)] bool bRedraw);

    public static void ApplyToElement(Window window, FrameworkElement? targetElement, bool enable, double cornerRadiusDip)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            if (!enable || targetElement is null || !targetElement.IsVisible)
            {
                ApplyAccentPolicy(hwnd, ACCENT_DISABLED);
                SetWindowRgn(hwnd, IntPtr.Zero, true);
                return;
            }

            double w = targetElement.ActualWidth > 0 ? targetElement.ActualWidth : targetElement.Width;
            double h = targetElement.ActualHeight > 0 ? targetElement.ActualHeight : targetElement.Height;
            if (double.IsNaN(w) || double.IsNaN(h) || w <= 2 || h <= 2)
                return;

            var topLeft = targetElement.TransformToAncestor(window).Transform(new Point(0, 0));
            var dpi = VisualTreeHelper.GetDpi(window);

            int x1 = (int)Math.Floor(topLeft.X * dpi.DpiScaleX);
            int y1 = (int)Math.Floor(topLeft.Y * dpi.DpiScaleY);
            int x2 = (int)Math.Ceiling((topLeft.X + w) * dpi.DpiScaleX);
            int y2 = (int)Math.Ceiling((topLeft.Y + h) * dpi.DpiScaleY);
            int ew = Math.Max(2, (int)Math.Round(cornerRadiusDip * 2.0 * dpi.DpiScaleX));
            int eh = Math.Max(2, (int)Math.Round(cornerRadiusDip * 2.0 * dpi.DpiScaleY));

            IntPtr hrgn = CreateRoundRectRgn(x1, y1, x2 + 1, y2 + 1, ew, eh);
            if (hrgn != IntPtr.Zero)
            {
                // SetWindowRgn takes ownership of hrgn when it succeeds (non-zero return).
                if (SetWindowRgn(hwnd, hrgn, true) == 0)
                {
                    Win32Api.DeleteObject(hrgn);
                }
            }

            ApplyAccentPolicy(hwnd, ACCENT_ENABLE_BLURBEHIND);
        }
        catch
        {
            // Ignore on unsupported composition environments
        }
    }

    public static void ApplyToCard(Window window, Border? card, bool enable, double cornerRadiusDip, byte blurAlpha = 0x9C)
    {
        if (card is not null)
        {
            if (enable && window.TryFindResource("ThemeSurfaceBrush") is SolidColorBrush surfaceBrush)
            {
                var c = surfaceBrush.Color;
                card.Background = new SolidColorBrush(Color.FromArgb(blurAlpha, c.R, c.G, c.B));
            }
            else
            {
                card.SetResourceReference(Border.BackgroundProperty, "ThemeSurfaceBrush");
            }
        }

        ApplyToElement(window, card, enable, cornerRadiusDip);
    }

    private static void ApplyAccentPolicy(IntPtr hwnd, int accentState)
    {
        var accent = new AccentPolicy
        {
            AccentState = accentState,
            AccentFlags = accentState == ACCENT_ENABLE_BLURBEHIND ? 2 : 0,
            GradientColor = 0,
            AnimationId = 0
        };

        int size = Marshal.SizeOf<AccentPolicy>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WCA_ACCENT_POLICY,
                Data = ptr,
                SizeOfData = size
            };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
