using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PulseIsland.Interop;

namespace PulseIsland.Translation;

public sealed record OcrPreparedVariant(
    string Name,
    byte[] PngBytes,
    bool IsFastPass);

/// <summary>
/// Pure C# (Zero-NuGet) adaptive image preprocessing pipeline for Windows.Media.Ocr.
/// Handles arbitrary font sizes (6pt pixel UI text up to 200pt display headers),
/// dark/light themes, colored-on-colored text, gradients, game HUDs, thin/italic/stylized fonts,
/// and tight lasso crops by adding quiet-zone margins, normalizing polarity, enhancing contrast,
/// sharpening upscaled glyphs, and generating adaptive binarized and multi-scale fallback variants.
/// </summary>
public static class OcrImagePreprocessor
{
    private const int MaxOcrDimension = 2200; // Windows.Media.Ocr hard cap is 2600px

    public static IReadOnlyList<OcrPreparedVariant> BuildVariants(CapturedScreenRegion region)
    {
        if (region.Width <= 0 || region.Height <= 0 || region.Bgra32Pixels.Length < region.Width * region.Height * 4)
            return Array.Empty<OcrPreparedVariant>();

        var variants = new List<OcrPreparedVariant>(5);
        var bg = EstimateBorderBackground(region.Bgra32Pixels, region.Width, region.Height);
        bool isDarkBg = bg.Luminance < 126.0;

        double primaryScale = ComputeOptimalUpscale(region.Width, region.Height);
        int quietMargin = ComputeQuietMargin(region.Width, region.Height, primaryScale);

        // --- FAST PASS 1: Polarity-normalized (black text on pure white) + contrast stretch + bicubic + quiet border ---
        // Conquers dark themes (Discord/VS Code/games/subtitles), low-contrast gray text, and standard light UI.
        byte[] grayNorm = BuildNormalizedGrayscale(region.Bgra32Pixels, region.Width, region.Height, invertForDarkBg: isDarkBg);
        byte[] scaledNorm = primaryScale > 1.05
            ? ResizeGrayscaleBicubic(grayNorm, region.Width, region.Height, primaryScale, out int sw, out int sh)
            : ResizeGrayscaleBilinear(grayNorm, region.Width, region.Height, primaryScale, out sw, out sh);
        if (primaryScale > 1.25)
            scaledNorm = ApplyUnsharpMask(scaledNorm, sw, sh, amount: 0.45);
        byte[] paddedNorm = PadGrayscale(scaledNorm, sw, sh, quietMargin, fillValue: 255, out int pw, out int ph);
        variants.Add(new OcrPreparedVariant("norm-contrast", EncodeGrayPng(paddedNorm, pw, ph), IsFastPass: true));

        // --- FAST PASS 2: Clean original RGB with background-matched quiet border + smooth scaling ---
        // Preserves ClearType subpixel antialiasing and clean color cues for crisp UI / web fonts.
        byte[] rgbScaled = ResizeBgraBilinear(region.Bgra32Pixels, region.Width, region.Height, primaryScale, out int rw, out int rh);
        byte[] rgbPadded = PadBgra(rgbScaled, rw, rh, quietMargin, bg.B, bg.G, bg.R, out int rpw, out int rph);
        variants.Add(new OcrPreparedVariant("rgb-clean", EncodeBgraPng(rgbPadded, rpw, rph), IsFastPass: true));

        // --- FAST PASS 3: Foreground-Color Cluster Isolation (black text on pure white) ---
        // Locks onto the dominant text ink color (e.g. yellow/white meme/subtitle/HUD text over mixed dark+bright photos,
        // or dark text over mixed light+gray photos) so background pillars/shadows vanish completely!
        byte[] fgCluster = BuildForegroundClusterGrayscale(region.Bgra32Pixels, region.Width, region.Height, targetBrightForeground: isDarkBg);
        byte[] fgScaled = primaryScale > 1.05
            ? ResizeGrayscaleBicubic(fgCluster, region.Width, region.Height, primaryScale, out int fw, out int fh)
            : ResizeGrayscaleBilinear(fgCluster, region.Width, region.Height, primaryScale, out fw, out fh);
        byte[] fgPadded = PadGrayscale(fgScaled, fw, fh, quietMargin, fillValue: 255, out int fpw, out int fph);
        variants.Add(new OcrPreparedVariant("fg-cluster", EncodeGrayPng(fgPadded, fpw, fph), IsFastPass: true));

        // --- DEEP PASS 4: Chromatic RGB background-distance map (black text on white) ---
        // Conquers colored text on colored backgrounds (red on green, cyan on navy, yellow on gray, neon text).
        byte[] chromaGray = BuildChromaticDistanceGrayscale(region.Bgra32Pixels, region.Width, region.Height, bg.R, bg.G, bg.B);
        byte[] chromaScaled = ResizeGrayscaleBilinear(chromaGray, region.Width, region.Height, primaryScale, out int cw, out int ch);
        chromaScaled = ApplyUnsharpMask(chromaScaled, cw, ch, amount: 0.85);
        byte[] chromaPadded = PadGrayscale(chromaScaled, cw, ch, quietMargin, fillValue: 255, out int cpw, out int cph);
        variants.Add(new OcrPreparedVariant("chroma-dist", EncodeGrayPng(chromaPadded, cpw, cph), IsFastPass: false));

        // --- DEEP PASS 4: Local Adaptive Thresholding (Integral-Image Sauvola/Mean) ---
        // Conquers gradients, textured game/photo backgrounds, transparent glass overlays, and uneven lighting.
        int localWindow = Math.Clamp((int)Math.Round(Math.Min(sw, sh) * 0.28), 15, 65) | 1;
        byte[] adaptiveBin = ApplyLocalAdaptiveThreshold(scaledNorm, sw, sh, localWindow, sensitivity: 12.0);
        byte[] adaptivePadded = PadGrayscale(adaptiveBin, sw, sh, quietMargin, fillValue: 255, out int apw, out int aph);
        variants.Add(new OcrPreparedVariant("adaptive-bin", EncodeGrayPng(adaptivePadded, apw, aph), IsFastPass: false));

        // --- DEEP PASS 5: Extreme-Size / Polarity-Flip Specialist ---
        if (region.Height >= 145 || region.Width >= 720)
        {
            // Giant display fonts / banners / posters / memes: Windows.Media.Ocr misses huge glyphs unless downscaled.
            double downScale = Math.Min(0.45, 130.0 / Math.Max(145, region.Height));
            downScale = Math.Clamp(downScale, 0.22, 0.55);
            byte[] downScaled = ResizeGrayscaleBilinear(grayNorm, region.Width, region.Height, downScale, out int dw, out int dh);
            if (dw >= 20 && dh >= 16)
            {
                byte[] downPadded = PadGrayscale(downScaled, dw, dh, 28, fillValue: 255, out int dpw, out int dph);
                variants.Add(new OcrPreparedVariant("downscale-giant", EncodeGrayPng(downPadded, dpw, dph), IsFastPass: false));
            }
        }
        else if (region.Height <= 68)
        {
            // Ultra-small / thin / pixel fonts (6pt-10pt): crisp 4.2x Catmull-Rom bicubic zoom
            // + midtone gap-clearing gamma lift (opens 1px anti-aliased bridges between touching tiny glyphs like o+n, r+n).
            double highScale = ClampScale(region.Width, region.Height, 4.2);
            byte[] hiScaled = ResizeGrayscaleBicubic(grayNorm, region.Width, region.Height, highScale, out int hw, out int hh);
            hiScaled = ApplyMidtoneGapLift(hiScaled, gamma: 0.68);
            hiScaled = ApplyUnsharpMask(hiScaled, hw, hh, amount: 0.55);
            byte[] hiPadded = PadGrayscale(hiScaled, hw, hh, 36, fillValue: 255, out int hpw, out int hph);
            variants.Add(new OcrPreparedVariant("upscale-tiny", EncodeGrayPng(hiPadded, hpw, hph), IsFastPass: false));
        }
        else
        {
            // Opposite-polarity fallback for cases where border color differed from the text card interior
            // (e.g. white text inside a dark badge surrounded by a light page).
            byte[] oppGray = BuildNormalizedGrayscale(region.Bgra32Pixels, region.Width, region.Height, invertForDarkBg: !isDarkBg);
            byte[] oppScaled = ResizeGrayscaleBilinear(oppGray, region.Width, region.Height, primaryScale, out int ow, out int oh);
            byte[] oppPadded = PadGrayscale(oppScaled, ow, oh, quietMargin, fillValue: 255, out int opw, out int oph);
            variants.Add(new OcrPreparedVariant("invert-polarity", EncodeGrayPng(oppPadded, opw, oph), IsFastPass: false));
        }

        return variants;
    }

    internal readonly record struct BackgroundEstimate(byte R, byte G, byte B, double Luminance);

    internal static BackgroundEstimate EstimateBorderBackground(byte[] bgra, int w, int h)
    {
        var rSamples = new List<byte>(Math.Min(512, (w + h) * 2));
        var gSamples = new List<byte>(Math.Min(512, (w + h) * 2));
        var bSamples = new List<byte>(Math.Min(512, (w + h) * 2));

        int stepX = Math.Max(1, w / 64);
        int stepY = Math.Max(1, h / 64);
        int borderDepth = Math.Min(3, Math.Min(w, h) / 2);
        if (borderDepth < 1) borderDepth = 1;

        for (int d = 0; d < borderDepth; d++)
        {
            int topY = d;
            int botY = h - 1 - d;
            for (int x = 0; x < w; x += stepX)
            {
                AddSample(bgra, (topY * w + x) * 4, rSamples, gSamples, bSamples);
                if (botY != topY)
                    AddSample(bgra, (botY * w + x) * 4, rSamples, gSamples, bSamples);
            }

            int leftX = d;
            int rightX = w - 1 - d;
            for (int y = borderDepth; y < h - borderDepth; y += stepY)
            {
                AddSample(bgra, (y * w + leftX) * 4, rSamples, gSamples, bSamples);
                if (rightX != leftX)
                    AddSample(bgra, (y * w + rightX) * 4, rSamples, gSamples, bSamples);
            }
        }

        if (rSamples.Count == 0)
            return new BackgroundEstimate(255, 255, 255, 255.0);

        rSamples.Sort();
        gSamples.Sort();
        bSamples.Sort();
        int mid = rSamples.Count / 2;
        byte medR = rSamples[mid];
        byte medG = gSamples[mid];
        byte medB = bSamples[mid];
        double lum = 0.299 * medR + 0.587 * medG + 0.114 * medB;
        return new BackgroundEstimate(medR, medG, medB, lum);
    }

    private static void AddSample(byte[] bgra, int idx, List<byte> rs, List<byte> gs, List<byte> bs)
    {
        bs.Add(bgra[idx + 0]);
        gs.Add(bgra[idx + 1]);
        rs.Add(bgra[idx + 2]);
    }

    internal static double ComputeOptimalUpscale(int width, int height)
    {
        double desired;
        if (height < 36 || width < 100)
            desired = 3.5;
        else if (height < 64)
            desired = 2.8;
        else if (height < 115)
            desired = 2.2;
        else if (height < 200)
            desired = 1.65;
        else if (height < 360)
            desired = 1.25;
        else
            desired = 1.0;

        return ClampScale(width, height, desired);
    }

    private static double ClampScale(int width, int height, double desired)
    {
        int maxSide = Math.Max(width, height);
        if (maxSide <= 0) return 1.0;
        double maxAllowed = (MaxOcrDimension - 96.0) / maxSide;
        if (maxAllowed < 1.0)
            return Math.Max(0.2, maxAllowed);
        return Math.Min(desired, maxAllowed);
    }

    private static int ComputeQuietMargin(int width, int height, double scale)
    {
        int scaledMax = (int)Math.Round(Math.Max(width, height) * scale);
        int margin = Math.Clamp((int)Math.Round(Math.Min(width, height) * scale * 0.18), 22, 40);
        if (scaledMax + margin * 2 > MaxOcrDimension)
            margin = Math.Max(12, (MaxOcrDimension - scaledMax) / 2);
        return margin;
    }

    internal static byte[] BuildNormalizedGrayscale(byte[] bgra, int w, int h, bool invertForDarkBg)
    {
        int count = w * h;
        byte[] gray = new byte[count];
        int[] hist = new int[256];

        for (int i = 0; i < count; i++)
        {
            int idx = i * 4;
            int lum = (77 * bgra[idx + 2] + 150 * bgra[idx + 1] + 29 * bgra[idx + 0]) >> 8;
            if (invertForDarkBg)
                lum = 255 - lum;
            gray[i] = (byte)lum;
            hist[lum]++;
        }

        // Percentile contrast stretch (P1.5 .. P98.5 -> 0 .. 255) so faint/muted text becomes sharp black-on-white
        int lowTarget = Math.Max(1, (int)(count * 0.015));
        int highTarget = Math.Max(lowTarget + 1, (int)(count * 0.985));

        int acc = 0;
        int minVal = 0;
        for (int v = 0; v < 256; v++)
        {
            acc += hist[v];
            if (acc >= lowTarget)
            {
                minVal = v;
                break;
            }
        }

        acc = 0;
        int maxVal = 255;
        for (int v = 0; v < 256; v++)
        {
            acc += hist[v];
            if (acc >= highTarget)
            {
                maxVal = v;
                break;
            }
        }

        int range = maxVal - minVal;
        if (range >= 10 && (minVal > 8 || maxVal < 247))
        {
            double invRange = 255.0 / range;
            for (int i = 0; i < count; i++)
            {
                int stretched = (int)Math.Round((gray[i] - minVal) * invRange);
                gray[i] = (byte)Math.Clamp(stretched, 0, 255);
            }
        }

        return gray;
    }

    internal static byte[] BuildForegroundClusterGrayscale(byte[] bgra, int w, int h, bool targetBrightForeground)
    {
        int count = w * h;
        byte[] lums = new byte[count];
        int[] lumHist = new int[256];

        for (int i = 0; i < count; i++)
        {
            int idx = i * 4;
            int lum = (77 * bgra[idx + 2] + 150 * bgra[idx + 1] + 29 * bgra[idx + 0]) >> 8;
            lums[i] = (byte)lum;
            lumHist[lum]++;
        }

        // Select top-8% brightest pixels (for light/yellow/white text on dark/photo backgrounds)
        // or bottom-8% darkest pixels (for dark text on light/photo backgrounds) to lock onto the true ink RGB color.
        int targetCount = Math.Max(4, (int)(count * (targetBrightForeground ? 0.92 : 0.08)));
        int acc = 0;
        int thresholdLum = targetBrightForeground ? 210 : 45;
        for (int v = 0; v < 256; v++)
        {
            acc += lumHist[v];
            if (acc >= targetCount)
            {
                thresholdLum = v;
                break;
            }
        }

        int stepX = Math.Max(1, w / 200);
        int stepY = Math.Max(1, h / 120);
        var rs = new List<byte>(512);
        var gs = new List<byte>(512);
        var bs = new List<byte>(512);

        for (int y = 0; y < h; y += stepY)
        {
            int row = y * w;
            for (int x = 0; x < w; x += stepX)
            {
                byte lum = lums[row + x];
                bool match = targetBrightForeground ? lum >= thresholdLum : lum <= thresholdLum;
                if (match)
                {
                    int idx = (row + x) * 4;
                    bs.Add(bgra[idx + 0]);
                    gs.Add(bgra[idx + 1]);
                    rs.Add(bgra[idx + 2]);
                }
            }
        }

        byte fgR = targetBrightForeground ? (byte)255 : (byte)0;
        byte fgG = fgR;
        byte fgB = fgR;
        if (rs.Count > 0)
        {
            rs.Sort();
            gs.Sort();
            bs.Sort();
            int mid = rs.Count / 2;
            fgR = rs[mid];
            fgG = gs[mid];
            fgB = bs[mid];
        }

        byte[] output = new byte[count];
        for (int i = 0; i < count; i++)
        {
            int idx = i * 4;
            int db = bgra[idx + 0] - fgB;
            int dg = bgra[idx + 1] - fgG;
            int dr = bgra[idx + 2] - fgR;
            double dist = Math.Sqrt(dr * dr + dg * dg + db * db);
            // Ink pixels within ~34 RGB units of dominant foreground color become black (0),
            // anything further than ~78 units (both dark shadows AND bright beige pillars!) becomes pure white (255).
            double t = Math.Clamp((dist - 34.0) / 44.0, 0.0, 1.0);
            output[i] = (byte)Math.Round(t * 255.0);
        }

        return output;
    }

    internal static byte[] BuildChromaticDistanceGrayscale(byte[] bgra, int w, int h, byte bgR, byte bgG, byte bgB)
    {
        int count = w * h;
        byte[] distMap = new byte[count];
        int[] hist = new int[256];

        // Compute fast O(W*H) per-channel RGB integral images so colored/handwritten ink on multi-color gradients
        // (e.g., red handwriting on a white-to-green gradient) is measured against its LOCAL background, not one global average!
        int stride = w + 1;
        long[] intR = new long[(w + 1) * (h + 1)];
        long[] intG = new long[(w + 1) * (h + 1)];
        long[] intB = new long[(w + 1) * (h + 1)];

        for (int y = 0; y < h; y++)
        {
            long rowR = 0, rowG = 0, rowB = 0;
            int dstRow = (y + 1) * stride;
            int prevRow = y * stride;
            int srcRow = y * w;
            for (int x = 0; x < w; x++)
            {
                int p = (srcRow + x) * 4;
                rowB += bgra[p + 0];
                rowG += bgra[p + 1];
                rowR += bgra[p + 2];
                intB[dstRow + x + 1] = intB[prevRow + x + 1] + rowB;
                intG[dstRow + x + 1] = intG[prevRow + x + 1] + rowG;
                intR[dstRow + x + 1] = intR[prevRow + x + 1] + rowR;
            }
        }

        int half = Math.Clamp(Math.Min(w, h) / 4, 10, 44);
        const double norm = 255.0 / 320.0;

        for (int y = 0; y < h; y++)
        {
            int y0 = Math.Max(0, y - half);
            int y1 = Math.Min(h - 1, y + half);
            int rowA = y0 * stride;
            int rowB = (y1 + 1) * stride;
            int outRow = y * w;

            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Max(0, x - half);
                int x1 = Math.Min(w - 1, x + half);
                int area = (x1 - x0 + 1) * (y1 - y0 + 1);

                double localR = (double)(intR[rowB + x1 + 1] - intR[rowA + x1 + 1] - intR[rowB + x0] + intR[rowA + x0]) / area;
                double localG = (double)(intG[rowB + x1 + 1] - intG[rowA + x1 + 1] - intG[rowB + x0] + intG[rowA + x0]) / area;
                double localB = (double)(intB[rowB + x1 + 1] - intB[rowA + x1 + 1] - intB[rowB + x0] + intB[rowA + x0]) / area;

                int idx = (outRow + x) * 4;
                double db = bgra[idx + 0] - localB;
                double dg = bgra[idx + 1] - localG;
                double dr = bgra[idx + 2] - localR;
                double localDist = Math.Sqrt(dr * dr + dg * dg + db * db);

                // Blend gently with global border distance for uniform solid backgrounds with very thick letters
                double gdb = bgra[idx + 0] - bgB;
                double gdg = bgra[idx + 1] - bgG;
                double gdr = bgra[idx + 2] - bgR;
                double globalDist = Math.Sqrt(gdr * gdr + gdg * gdg + gdb * gdb);

                double dist = Math.Max(localDist, globalDist * 0.42);
                int val = 255 - Math.Clamp((int)Math.Round(dist * norm), 0, 255);
                distMap[outRow + x] = (byte)val;
                hist[val]++;
            }
        }

        // Stretch dynamic range so even subtle color differences become dark ink on pure white
        int lowTarget = Math.Max(1, (int)(count * 0.01));
        int highTarget = Math.Max(lowTarget + 1, (int)(count * 0.90));
        int acc = 0, minVal = 0, maxVal = 255;
        for (int v = 0; v < 256; v++)
        {
            acc += hist[v];
            if (acc >= lowTarget) { minVal = v; break; }
        }
        acc = 0;
        for (int v = 0; v < 256; v++)
        {
            acc += hist[v];
            if (acc >= highTarget) { maxVal = v; break; }
        }

        int range = maxVal - minVal;
        if (range >= 8)
        {
            double invRange = 255.0 / range;
            for (int i = 0; i < count; i++)
            {
                int stretched = (int)Math.Round((distMap[i] - minVal) * invRange);
                distMap[i] = (byte)Math.Clamp(stretched, 0, 255);
            }
        }

        return distMap;
    }

    internal static byte[] ApplyLocalAdaptiveThreshold(byte[] gray, int w, int h, int windowSize, double sensitivity)
    {
        // Fast O(W*H) summed-area table (integral image)
        long[] integral = new long[(w + 1) * (h + 1)];
        int stride = w + 1;

        for (int y = 0; y < h; y++)
        {
            long rowSum = 0;
            int srcRow = y * w;
            int dstRow = (y + 1) * stride;
            int prevRow = y * stride;
            for (int x = 0; x < w; x++)
            {
                rowSum += gray[srcRow + x];
                integral[dstRow + x + 1] = integral[prevRow + x + 1] + rowSum;
            }
        }

        byte[] output = new byte[w * h];
        int half = windowSize / 2;

        for (int y = 0; y < h; y++)
        {
            int y0 = Math.Max(0, y - half);
            int y1 = Math.Min(h - 1, y + half);
            int rowA = y0 * stride;
            int rowB = (y1 + 1) * stride;
            int outRow = y * w;

            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Max(0, x - half);
                int x1 = Math.Min(w - 1, x + half);

                long sum = integral[rowB + x1 + 1]
                         - integral[rowA + x1 + 1]
                         - integral[rowB + x0]
                         + integral[rowA + x0];
                int area = (x1 - x0 + 1) * (y1 - y0 + 1);
                double localMean = (double)sum / area;

                byte px = gray[outRow + x];
                // Soft binarization preserves slight anti-aliasing while stripping uneven background
                double diff = localMean - px;
                if (diff > sensitivity)
                {
                    double t = Math.Clamp((diff - sensitivity) / 14.0, 0.0, 1.0);
                    output[outRow + x] = (byte)Math.Round(255.0 * (1.0 - t));
                }
                else
                {
                    output[outRow + x] = 255;
                }
            }
        }

        return output;
    }

    private static byte[] ApplyMidtoneGapLift(byte[] gray, double gamma)
    {
        // Brightens mid-gray anti-aliased bridges between tightly packed tiny glyphs toward white
        // while keeping dark stroke cores crisp.
        byte[] lut = new byte[256];
        for (int v = 0; v < 256; v++)
        {
            lut[v] = (byte)Math.Clamp((int)Math.Round(Math.Pow(v / 255.0, gamma) * 255.0), 0, 255);
        }

        byte[] dst = new byte[gray.Length];
        for (int i = 0; i < gray.Length; i++)
        {
            dst[i] = lut[gray[i]];
        }
        return dst;
    }

    private static byte[] ApplyUnsharpMask(byte[] gray, int w, int h, double amount)
    {
        byte[] dst = new byte[w * h];
        Array.Copy(gray, dst, gray.Length);

        for (int y = 1; y < h - 1; y++)
        {
            int row = y * w;
            int prev = row - w;
            int next = row + w;
            for (int x = 1; x < w - 1; x++)
            {
                int blur = (gray[prev + x - 1] + 2 * gray[prev + x] + gray[prev + x + 1]
                          + 2 * gray[row + x - 1] + 4 * gray[row + x] + 2 * gray[row + x + 1]
                          + gray[next + x - 1] + 2 * gray[next + x] + gray[next + x + 1]) >> 4;
                int center = gray[row + x];
                int sharp = (int)Math.Round(center + amount * (center - blur));
                dst[row + x] = (byte)Math.Clamp(sharp, 0, 255);
            }
        }

        return dst;
    }

    private static double CubicWeight(double t)
    {
        // Catmull-Rom spline (a = -0.5): sharp, preserves 1px inter-letter gaps without harsh ringing
        double x = Math.Abs(t);
        if (x <= 1.0)
            return (1.5 * x - 2.5) * x * x + 1.0;
        if (x < 2.0)
            return ((-0.5 * x + 2.5) * x - 4.0) * x + 2.0;
        return 0.0;
    }

    private static byte[] ResizeGrayscaleBicubic(byte[] src, int sw, int sh, double scale, out int dw, out int dh)
    {
        if (Math.Abs(scale - 1.0) < 0.02 || sw < 4 || sh < 4)
            return ResizeGrayscaleBilinear(src, sw, sh, scale, out dw, out dh);

        dw = Math.Max(2, (int)Math.Round(sw * scale));
        dh = Math.Max(2, (int)Math.Round(sh * scale));

        // Separable 2-pass bicubic (horizontal -> vertical) for high speed & crisp glyph edges
        double[] temp = new double[dw * sh];
        double xRatio = (double)(sw - 1) / Math.Max(1, dw - 1);
        double yRatio = (double)(sh - 1) / Math.Max(1, dh - 1);

        int[] xBase = new int[dw];
        double[] wx0 = new double[dw], wx1 = new double[dw], wx2 = new double[dw], wx3 = new double[dw];
        for (int x = 0; x < dw; x++)
        {
            double gx = x * xRatio;
            int ix = (int)Math.Floor(gx);
            double fx = gx - ix;
            xBase[x] = ix;
            wx0[x] = CubicWeight(fx + 1.0);
            wx1[x] = CubicWeight(fx);
            wx2[x] = CubicWeight(fx - 1.0);
            wx3[x] = CubicWeight(fx - 2.0);
        }

        for (int y = 0; y < sh; y++)
        {
            int sRow = y * sw;
            int tRow = y * dw;
            for (int x = 0; x < dw; x++)
            {
                int ix = xBase[x];
                int c0 = Math.Clamp(ix - 1, 0, sw - 1);
                int c1 = Math.Clamp(ix, 0, sw - 1);
                int c2 = Math.Clamp(ix + 1, 0, sw - 1);
                int c3 = Math.Clamp(ix + 2, 0, sw - 1);
                temp[tRow + x] = src[sRow + c0] * wx0[x]
                               + src[sRow + c1] * wx1[x]
                               + src[sRow + c2] * wx2[x]
                               + src[sRow + c3] * wx3[x];
            }
        }

        byte[] dst = new byte[dw * dh];
        for (int y = 0; y < dh; y++)
        {
            double gy = y * yRatio;
            int iy = (int)Math.Floor(gy);
            double fy = gy - iy;
            double wy0 = CubicWeight(fy + 1.0);
            double wy1 = CubicWeight(fy);
            double wy2 = CubicWeight(fy - 1.0);
            double wy3 = CubicWeight(fy - 2.0);

            int r0 = Math.Clamp(iy - 1, 0, sh - 1) * dw;
            int r1 = Math.Clamp(iy, 0, sh - 1) * dw;
            int r2 = Math.Clamp(iy + 1, 0, sh - 1) * dw;
            int r3 = Math.Clamp(iy + 2, 0, sh - 1) * dw;
            int dRow = y * dw;

            for (int x = 0; x < dw; x++)
            {
                double val = temp[r0 + x] * wy0
                           + temp[r1 + x] * wy1
                           + temp[r2 + x] * wy2
                           + temp[r3 + x] * wy3;
                dst[dRow + x] = (byte)Math.Clamp((int)Math.Round(val), 0, 255);
            }
        }

        return dst;
    }

    private static byte[] ResizeGrayscaleBilinear(byte[] src, int sw, int sh, double scale, out int dw, out int dh)
    {
        if (Math.Abs(scale - 1.0) < 0.02)
        {
            dw = sw;
            dh = sh;
            return (byte[])src.Clone();
        }

        dw = Math.Max(2, (int)Math.Round(sw * scale));
        dh = Math.Max(2, (int)Math.Round(sh * scale));
        byte[] dst = new byte[dw * dh];

        double xRatio = (double)(sw - 1) / Math.Max(1, dw - 1);
        double yRatio = (double)(sh - 1) / Math.Max(1, dh - 1);

        for (int y = 0; y < dh; y++)
        {
            double gy = y * yRatio;
            int y0 = (int)gy;
            int y1 = Math.Min(sh - 1, y0 + 1);
            double wy = gy - y0;
            int row0 = y0 * sw;
            int row1 = y1 * sw;
            int dstRow = y * dw;

            for (int x = 0; x < dw; x++)
            {
                double gx = x * xRatio;
                int x0 = (int)gx;
                int x1 = Math.Min(sw - 1, x0 + 1);
                double wx = gx - x0;

                double top = src[row0 + x0] * (1.0 - wx) + src[row0 + x1] * wx;
                double bot = src[row1 + x0] * (1.0 - wx) + src[row1 + x1] * wx;
                dst[dstRow + x] = (byte)Math.Clamp((int)Math.Round(top * (1.0 - wy) + bot * wy), 0, 255);
            }
        }

        return dst;
    }

    private static byte[] ResizeBgraBilinear(byte[] src, int sw, int sh, double scale, out int dw, out int dh)
    {
        if (Math.Abs(scale - 1.0) < 0.02)
        {
            dw = sw;
            dh = sh;
            return (byte[])src.Clone();
        }

        dw = Math.Max(2, (int)Math.Round(sw * scale));
        dh = Math.Max(2, (int)Math.Round(sh * scale));
        byte[] dst = new byte[dw * dh * 4];

        double xRatio = (double)(sw - 1) / Math.Max(1, dw - 1);
        double yRatio = (double)(sh - 1) / Math.Max(1, dh - 1);

        for (int y = 0; y < dh; y++)
        {
            double gy = y * yRatio;
            int y0 = (int)gy;
            int y1 = Math.Min(sh - 1, y0 + 1);
            double wy = gy - y0;
            double wy0 = 1.0 - wy;

            for (int x = 0; x < dw; x++)
            {
                double gx = x * xRatio;
                int x0 = (int)gx;
                int x1 = Math.Min(sw - 1, x0 + 1);
                double wx = gx - x0;
                double wx0 = 1.0 - wx;

                int i00 = (y0 * sw + x0) * 4;
                int i10 = (y0 * sw + x1) * 4;
                int i01 = (y1 * sw + x0) * 4;
                int i11 = (y1 * sw + x1) * 4;
                int dIdx = (y * dw + x) * 4;

                for (int c = 0; c < 3; c++)
                {
                    double top = src[i00 + c] * wx0 + src[i10 + c] * wx;
                    double bot = src[i01 + c] * wx0 + src[i11 + c] * wx;
                    dst[dIdx + c] = (byte)Math.Clamp((int)Math.Round(top * wy0 + bot * wy), 0, 255);
                }
                dst[dIdx + 3] = 255;
            }
        }

        return dst;
    }

    private static byte[] PadGrayscale(byte[] src, int w, int h, int margin, byte fillValue, out int pw, out int ph)
    {
        pw = w + margin * 2;
        ph = h + margin * 2;
        byte[] dst = new byte[pw * ph];
        Array.Fill(dst, fillValue);

        for (int y = 0; y < h; y++)
        {
            Buffer.BlockCopy(src, y * w, dst, (y + margin) * pw + margin, w);
        }

        return dst;
    }

    private static byte[] PadBgra(byte[] src, int w, int h, int margin, byte fillB, byte fillG, byte fillR, out int pw, out int ph)
    {
        pw = w + margin * 2;
        ph = h + margin * 2;
        byte[] dst = new byte[pw * ph * 4];

        for (int i = 0; i < pw * ph; i++)
        {
            int idx = i * 4;
            dst[idx + 0] = fillB;
            dst[idx + 1] = fillG;
            dst[idx + 2] = fillR;
            dst[idx + 3] = 255;
        }

        int srcRowBytes = w * 4;
        int dstStride = pw * 4;
        int marginBytes = margin * 4;
        for (int y = 0; y < h; y++)
        {
            Buffer.BlockCopy(src, y * srcRowBytes, dst, (y + margin) * dstStride + marginBytes, srcRowBytes);
        }

        return dst;
    }

    private static byte[] EncodeGrayPng(byte[] gray, int w, int h)
    {
        // Convert to opaque BGRA32 so Windows.Graphics.Imaging.BitmapDecoder always produces Bgra8 SoftwareBitmap directly
        byte[] bgra = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            byte v = gray[i];
            int idx = i * 4;
            bgra[idx + 0] = v;
            bgra[idx + 1] = v;
            bgra[idx + 2] = v;
            bgra[idx + 3] = 255;
        }

        return EncodeBgraPng(bgra, w, h);
    }

    private static byte[] EncodeBgraPng(byte[] bgra, int w, int h)
    {
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
        bmp.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
