using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PulseIsland.Interop;

public sealed record CapturedScreenRegion(
    int Width,
    int Height,
    byte[] Bgra32Pixels);

/// <summary>
/// Captures a physical screen rectangle directly into an in-memory BGRA32 buffer via GDI BitBlt
/// without creating temporary files on disk, and provides in-memory WPF BitmapSource / JPEG conversion
/// for Vision LLM (VLM) queries.
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

    /// <summary>
    /// Creates a frozen WPF BitmapSource directly from in-memory BGRA32 screen pixels (no disk I/O).
    /// </summary>
    public static BitmapSource CreateBitmapSource(CapturedScreenRegion region)
    {
        int stride = region.Width * 4;
        var bmp = BitmapSource.Create(
            region.Width,
            region.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            region.Bgra32Pixels,
            stride);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>
    /// Downscales (if larger than <paramref name="maxSide"/>), composites any transparent pixels onto white,
    /// and encodes a captured screen region to in-memory JPEG bytes for Vision LLM multimodal chat requests.
    /// </summary>
    public static byte[] EncodeToJpegBytes(CapturedScreenRegion region, int maxSide = 1024, int quality = 86)
    {
        byte[] opaquePixels = CompositeBgraOverWhiteIfNeeded(region.Bgra32Pixels);
        BitmapSource source = BitmapSource.Create(
            region.Width,
            region.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            opaquePixels,
            region.Width * 4);
        source.Freeze();

        int longest = Math.Max(region.Width, region.Height);
        if (maxSide > 0 && longest > maxSide)
        {
            double scale = (double)maxSide / longest;
            var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            scaled.Freeze();
            source = scaled;
        }

        var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 50, 95) };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Alpha-blends any non-opaque BGRA32 pixels onto a clean white background (255,255,255)
    /// so transparent PNG/WebP/GIF stickers and dark-text diagrams never turn black in JPEG.
    /// </summary>
    public static byte[] CompositeBgraOverWhiteIfNeeded(byte[] bgraPixels)
    {
        bool hasTransparency = false;
        for (int i = 3; i < bgraPixels.Length; i += 4)
        {
            if (bgraPixels[i] < 255)
            {
                hasTransparency = true;
                break;
            }
        }
        if (!hasTransparency)
            return bgraPixels;

        byte[] outBuf = new byte[bgraPixels.Length];
        for (int i = 0; i + 3 < bgraPixels.Length; i += 4)
        {
            int a = bgraPixels[i + 3];
            int inv = 255 - a;
            outBuf[i] = (byte)((bgraPixels[i] * a + 255 * inv + 127) / 255);
            outBuf[i + 1] = (byte)((bgraPixels[i + 1] * a + 255 * inv + 127) / 255);
            outBuf[i + 2] = (byte)((bgraPixels[i + 2] * a + 255 * inv + 127) / 255);
            outBuf[i + 3] = 255;
        }
        return outBuf;
    }

    /// <summary>
    /// Converts any WPF BitmapSource (e.g. from Clipboard.GetImage()) into a normalized BGRA32 CapturedScreenRegion.
    /// </summary>
    public static CapturedScreenRegion? FromBitmapSource(BitmapSource? source)
    {
        if (source is null || source.PixelWidth <= 0 || source.PixelHeight <= 0)
            return null;

        BitmapSource bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        int width = bgra.PixelWidth;
        int height = bgra.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        bgra.CopyPixels(pixels, stride, 0);

        // Clipboard screenshots (CF_DIB / Snipping Tool) sometimes arrive with alpha byte = 0; normalize to opaque 255
        bool allZeroAlpha = true;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
            {
                allZeroAlpha = false;
                break;
            }
        }
        if (allZeroAlpha)
        {
            for (int i = 3; i < pixels.Length; i += 4)
                pixels[i] = 255;
        }

        return new CapturedScreenRegion(width, height, pixels);
    }

    /// <summary>
    /// Loads a local image file (.png, .jpg, .bmp, .webp, .gif) into an in-memory CapturedScreenRegion
    /// via FileShare.ReadWrite + MemoryStream so files are never locked or served stale from WPF URI cache.
    /// </summary>
    public static CapturedScreenRegion? LoadFromImageFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return null;

        try
        {
            byte[] rawBytes;
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                rawBytes = new byte[fs.Length];
                int offset = 0;
                while (offset < rawBytes.Length)
                {
                    int r = fs.Read(rawBytes, offset, rawBytes.Length - offset);
                    if (r <= 0) break;
                    offset += r;
                }
            }

            using var ms = new MemoryStream(rawBytes);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
                return null;

            return FromBitmapSource(decoder.Frames[0]);
        }
        catch
        {
            return null;
        }
    }
}
