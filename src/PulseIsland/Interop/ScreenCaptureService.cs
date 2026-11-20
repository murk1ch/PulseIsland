using System.Runtime.InteropServices;
using System.Windows;

namespace PulseIsland.Interop;

public sealed record CapturedScreenRegion(
    int Width,
    int Height,
    byte[] Bgra32Pixels);

/// <summary>
/// Captures a physical screen rectangle directly into an in-memory BGRA32 buffer via GDI BitBlt
/// without creating temporary files on disk.
/// </summary>
public static class ScreenCaptureService
{
    public static CapturedScreenRegion? CaptureRegion(Int32Rect physicalRect)
    {
        if (physicalRect.Width <= 0 || physicalRect.Height <= 0)
            return null;

        IntPtr screenDc = Win32Api.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
            return null;

        IntPtr memDc = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;

        try
        {
            memDc = Win32Api.CreateCompatibleDC(screenDc);
            hBitmap = Win32Api.CreateCompatibleBitmap(screenDc, physicalRect.Width, physicalRect.Height);
            oldBitmap = Win32Api.SelectObject(memDc, hBitmap);

            if (!Win32Api.BitBlt(
                memDc, 0, 0, physicalRect.Width, physicalRect.Height,
                screenDc, physicalRect.X, physicalRect.Y, Win32Api.SRCCOPY))
            {
                return null;
            }

            var bmi = new Win32Api.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<Win32Api.BITMAPINFOHEADER>(),
                biWidth = physicalRect.Width,
                biHeight = -physicalRect.Height, // top-down DIB
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0 // BI_RGB
            };

            byte[] pixels = new byte[physicalRect.Width * physicalRect.Height * 4];
            int scanLines = Win32Api.GetDIBits(memDc, hBitmap, 0, (uint)physicalRect.Height, pixels, ref bmi, 0);
            if (scanLines == 0)
                return null;

            return new CapturedScreenRegion(physicalRect.Width, physicalRect.Height, pixels);
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero && memDc != IntPtr.Zero)
                Win32Api.SelectObject(memDc, oldBitmap);
            if (hBitmap != IntPtr.Zero)
                Win32Api.DeleteObject(hBitmap);
            if (memDc != IntPtr.Zero)
                Win32Api.DeleteDC(memDc);
            Win32Api.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
