using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PulseIsland.Services;

public static class AlbumColorHelper
{
    public static Color ExtractDominantColor(BitmapSource? bitmap, Color fallback)
    {
        if (bitmap is null || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
            return fallback;

        try
        {
            var converted = bitmap.Format == PixelFormats.Bgra32
                ? bitmap
                : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);

            int w = converted.PixelWidth;
            int h = converted.PixelHeight;
            int stride = w * 4;
            byte[] pixels = new byte[stride * h];
            converted.CopyPixels(pixels, stride, 0);

            long sumR = 0, sumG = 0, sumB = 0, count = 0;
            int stepX = Math.Max(1, w / 24);
            int stepY = Math.Max(1, h / 24);

            for (int y = 0; y < h; y += stepY)
            {
                for (int x = 0; x < w; x += stepX)
                {
                    int idx = y * stride + x * 4;
                    byte b = pixels[idx];
                    byte g = pixels[idx + 1];
                    byte r = pixels[idx + 2];
                    byte a = pixels[idx + 3];

                    if (a < 128) continue;

                    // Skip near-black and near-white pixels to get vivid accent colors
                    int max = Math.Max(r, Math.Max(g, b));
                    int min = Math.Min(r, Math.Min(g, b));
                    if (max < 40 || (max - min) < 20) continue;

                    sumR += r;
                    sumG += g;
                    sumB += b;
                    count++;
                }
            }

            if (count == 0)
                return fallback;

            return Color.FromRgb(
                (byte)Math.Clamp(sumR / count, 60, 255),
                (byte)Math.Clamp(sumG / count, 60, 255),
                (byte)Math.Clamp(sumB / count, 60, 255));
        }
        catch
        {
            return fallback;
        }
    }
}
