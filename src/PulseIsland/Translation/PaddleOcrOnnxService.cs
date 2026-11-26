using System.IO;
using System.Text;
using PulseIsland.Core;
using PulseIsland.Interop;
using PulseIsland.Services;

namespace PulseIsland.Translation;

/// <summary>
/// High-accuracy, 100% offline neural OCR engine powered by official PaddleOCR PP-OCRv5 ONNX models
/// (DBNet++ detector + SVTR East-Slavic Cyrillic/Latin/Math/Handwriting recognizer + English recognizer).
/// Runs natively on models\_runtime\onnxruntime.dll via OrtNativeRuntime with zero external NuGet dependencies.
/// </summary>
public sealed class PaddleOcrOnnxService : IDisposable
{
    public const string DetFileName = "ppocrv5_det.onnx";
    public const string EslavRecFileName = "ppocrv5_eslav_rec.onnx";
    public const string EslavYmlFileName = "ppocrv5_eslav_rec.yml";
    public const string EnRecFileName = "ppocrv5_en_rec.onnx";
    public const string EnYmlFileName = "ppocrv5_en_rec.yml";

    public const string DetUrl = "https://huggingface.co/PaddlePaddle/PP-OCRv5_mobile_det_onnx/resolve/main/inference.onnx";
    public const string EslavRecUrl = "https://huggingface.co/PaddlePaddle/eslav_PP-OCRv5_mobile_rec_onnx/resolve/main/inference.onnx";
    public const string EslavYmlUrl = "https://huggingface.co/PaddlePaddle/eslav_PP-OCRv5_mobile_rec_onnx/resolve/main/inference.yml";
    public const string EnRecUrl = "https://huggingface.co/PaddlePaddle/en_PP-OCRv5_mobile_rec_onnx/resolve/main/inference.onnx";
    public const string EnYmlUrl = "https://huggingface.co/PaddlePaddle/en_PP-OCRv5_mobile_rec_onnx/resolve/main/inference.yml";

    public static string OcrModelsDirectory => Path.Combine(PortablePaths.ModelsDirectory, "_ocr");

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private OrtNativeRuntime.OrtFloatSession? _detSession;
    private OrtNativeRuntime.OrtFloatSession? _eslavRecSession;
    private OrtNativeRuntime.OrtFloatSession? _enRecSession;
    private IReadOnlyList<string> _eslavDict = Array.Empty<string>();
    private IReadOnlyList<string> _enDict = Array.Empty<string>();
    private bool _disposed;

    public static bool IsInstalled()
    {
        if (!OrtNativeRuntime.IsRuntimeAvailable())
            return false;

        string dir = OcrModelsDirectory;
        return File.Exists(Path.Combine(dir, DetFileName)) &&
               File.Exists(Path.Combine(dir, EslavRecFileName)) &&
               File.Exists(Path.Combine(dir, EslavYmlFileName));
    }

    public static async Task DownloadModelsAsync(
        IProgress<DownloadProgressInfo>? progress = null,
        CancellationToken ct = default)
    {
        PortablePaths.EnsureDirectoriesExist();
        await OrtNativeRuntime.EnsureRuntimeAvailableAsync(ct).ConfigureAwait(false);
        Directory.CreateDirectory(OcrModelsDirectory);

        (string Url, string FileName)[] files =
        [
            (DetUrl, DetFileName),
            (EslavRecUrl, EslavRecFileName),
            (EslavYmlUrl, EslavYmlFileName),
            (EnRecUrl, EnRecFileName),
            (EnYmlUrl, EnYmlFileName)
        ];

        using var downloader = new HuggingFaceDownloader();
        foreach (var (url, fileName) in files)
        {
            string target = Path.Combine(OcrModelsDirectory, fileName);
            if (File.Exists(target) && new FileInfo(target).Length > 1000)
                continue;

            await downloader.DownloadFileAsync(url, "_ocr", fileName, progress, ct).ConfigureAwait(false);
        }
    }

    public async Task<(OcrResult Result, double Confidence)> RecognizeAsync(
        CapturedScreenRegion region,
        bool autoDetectLanguage = true,
        string manualLanguageTag = "en",
        CancellationToken ct = default)
    {
        if (region.Width <= 0 || region.Height <= 0 || region.Bgra32Pixels.Length < region.Width * region.Height * 4)
            return (new OcrResult(false, string.Empty, "und", LocalizationService.Pick("Empty image.", "Пустое изображение.")), 0.0);

        if (!IsInstalled())
            return (new OcrResult(false, string.Empty, "und", LocalizationService.Pick("PP-OCRv5 neural OCR is not installed.", "Нейросеть PP-OCRv5 не установлена.")), 0.0);

        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        if (_detSession is null || _eslavRecSession is null)
            return (new OcrResult(false, string.Empty, "und", LocalizationService.Pick("Failed to load PP-OCRv5 sessions.", "Не удалось загрузить сессии PP-OCRv5.")), 0.0);

        return await Task.Run(() => RecognizeInternal(region, autoDetectLanguage, manualLanguageTag, ct), ct)
            .ConfigureAwait(false);
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_detSession is not null && _eslavRecSession is not null)
            return;

        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_detSession is not null && _eslavRecSession is not null)
                return;

            string dir = OcrModelsDirectory;
            _detSession = new OrtNativeRuntime.OrtFloatSession(Path.Combine(dir, DetFileName));
            _eslavRecSession = new OrtNativeRuntime.OrtFloatSession(Path.Combine(dir, EslavRecFileName));
            _eslavDict = LoadCharacterDictFromYaml(Path.Combine(dir, EslavYmlFileName));

            string enModelPath = Path.Combine(dir, EnRecFileName);
            string enYmlPath = Path.Combine(dir, EnYmlFileName);
            if (File.Exists(enModelPath) && File.Exists(enYmlPath))
            {
                _enRecSession = new OrtNativeRuntime.OrtFloatSession(enModelPath);
                _enDict = LoadCharacterDictFromYaml(enYmlPath);
            }

            LogService.Info($"PaddleOcrOnnxService loaded PP-OCRv5 (eslavDict={_eslavDict.Count}, enDict={_enDict.Count}).");
        }
        finally
        {
            _initLock.Release();
        }
    }

    private (OcrResult Result, double Confidence) RecognizeInternal(
        CapturedScreenRegion region,
        bool autoDetectLanguage,
        string manualLanguageTag,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        string? preferredLang = autoDetectLanguage ? null : manualLanguageTag;
        var boxes = DetectTextBoxes(region);

        string detText = string.Empty;
        double detConf = 0.0;

        if (boxes.Count > 0)
        {
            var recognizedBoxes = new List<RecognizedBox>(boxes.Count);
            foreach (var box in boxes)
            {
                ct.ThrowIfCancellationRequested();
                var crop = CropRegion(region, box.X, box.Y, box.W, box.H);
                if (crop is null) continue;

                var (txt, conf) = RecognizeLineCropBest(crop, preferredLang);
                if (!string.IsNullOrWhiteSpace(txt) && conf >= 0.28)
                {
                    recognizedBoxes.Add(new RecognizedBox(box.X, box.Y, box.W, box.H, txt.Trim(), conf));
                }
            }

            if (recognizedBoxes.Count > 0)
            {
                detText = AssembleReadingOrder(recognizedBoxes);
                detConf = recognizedBoxes.Average(b => b.Confidence);
            }
        }

        // Direct full-crop recognition fallback for single-line / compact selections or when DBNet missed thin/handwritten strokes
        string directText = string.Empty;
        double directConf = 0.0;
        bool shouldTryDirect = boxes.Count == 0 ||
                               string.IsNullOrWhiteSpace(detText) ||
                               (region.Height <= 150 && boxes.Count <= 2 && detConf < 0.88);

        if (shouldTryDirect)
        {
            ct.ThrowIfCancellationRequested();
            var (txt, conf) = RecognizeLineCropBest(region, preferredLang);
            if (!string.IsNullOrWhiteSpace(txt))
            {
                directText = txt.Trim();
                directConf = conf;
            }
        }

        string chosenText;
        double chosenConf;

        if (string.IsNullOrWhiteSpace(detText))
        {
            chosenText = directText;
            chosenConf = directConf;
        }
        else if (string.IsNullOrWhiteSpace(directText))
        {
            chosenText = detText;
            chosenConf = detConf;
        }
        else
        {
            // Compare det+rec vs direct full-crop: prefer whichever captures complete text with higher confidence
            double detQuality = detConf + (detText.Length > directText.Length + 2 ? 0.08 : 0.0);
            double directQuality = directConf + (directText.Contains(detText, StringComparison.OrdinalIgnoreCase) && directText.Length > detText.Length ? 0.12 : 0.0);
            if (directQuality > detQuality)
            {
                chosenText = directText;
                chosenConf = directConf;
            }
            else
            {
                chosenText = detText;
                chosenConf = detConf;
            }
        }

        chosenText = WindowsOcrService.CleanOcrText(chosenText);
        if (string.IsNullOrWhiteSpace(chosenText))
            return (new OcrResult(false, string.Empty, "und", LocalizationService.Pick("No text detected.", "Текст не обнаружен.")), 0.0);

        string resolvedLang = WindowsOcrService.InferLanguageTagFromText(chosenText, preferredLang ?? "en");
        return (new OcrResult(true, chosenText, resolvedLang), chosenConf);
    }

    private readonly record struct TextBox(int X, int Y, int W, int H, double Score);
    private readonly record struct RecognizedBox(int X, int Y, int W, int H, string Text, double Confidence);

    private List<TextBox> DetectTextBoxes(CapturedScreenRegion region)
    {
        if (_detSession is null)
            return new List<TextBox>();

        // 1. Add quiet border with border median color so glyphs touching the selection edge are never clipped by DBNet
        var bg = OcrImagePreprocessor.EstimateBorderBackground(region.Bgra32Pixels, region.Width, region.Height);
        int quietPad = Math.Clamp(Math.Min(region.Width, region.Height) / 5, 14, 28);
        byte[] padded = PadBgraWithColor(region.Bgra32Pixels, region.Width, region.Height, quietPad, bg.B, bg.G, bg.R, out int pw, out int ph);

        // 2. Choose optimal scale (upscale small selections so tiny 8-12px fonts trigger DBNet cleanly; downscale >960px)
        double scale = 1.0;
        int minSide = Math.Min(pw, ph);
        int maxSide = Math.Max(pw, ph);
        if (minSide < 160)
            scale = Math.Min(2.4, 176.0 / Math.Max(1, minSide));
        if (maxSide * scale > 960.0)
            scale = 960.0 / maxSide;

        int detW = Math.Max(32, ((int)Math.Round(pw * scale) + 31) / 32 * 32);
        int detH = Math.Max(32, ((int)Math.Round(ph * scale) + 31) / 32 * 32);
        double scaleX = (double)detW / pw;
        double scaleY = (double)detH / ph;

        // 3. Resize & normalize BGR into NCHW [1, 3, detH, detW] using official PaddleOCR ImageNet mean/std
        float[] input = ResizeBgraToDetTensor(padded, pw, ph, detW, detH);
        var (probMap, shape) = _detSession.RunFloat(input, [1, 3, detH, detW]);

        int mapH = shape.Length >= 4 ? (int)shape[2] : detH;
        int mapW = shape.Length >= 4 ? (int)shape[3] : detW;
        if (probMap.Length < mapH * mapW)
            return new List<TextBox>();

        // 4. Connected-component box extraction with DBNet unclip expansion
        // Use a balanced threshold (0.24 / 0.28) so small single digits/fractions/superscripts are never dropped
        // (the SVTR recognizer filters out any false background patches cleanly via CTC confidence).
        var boxes = ExtractBoxesFromProbMap(probMap, mapW, mapH, thresh: 0.26f, boxThresh: 0.34f,
            scaleX, scaleY, quietPad, region.Width, region.Height);

        if (boxes.Count == 0)
        {
            boxes = ExtractBoxesFromProbMap(probMap, mapW, mapH, thresh: 0.14f, boxThresh: 0.18f,
                scaleX, scaleY, quietPad, region.Width, region.Height);
        }

        // If detection found few/narrow boxes on a colorful or gradient image (e.g., handwriting over a white-to-green gradient),
        // also run DBNet on the local RGB chromatic-contrast map and merge any newly uncovered text boxes!
        int coveredWidth = boxes.Count > 0 ? boxes.Max(b => b.X + b.W) - boxes.Min(b => b.X) : 0;
        if (boxes.Count == 0 || (region.Width >= 80 && coveredWidth < region.Width * 0.58))
        {
            byte[] chromaGray = OcrImagePreprocessor.BuildChromaticDistanceGrayscale(region.Bgra32Pixels, region.Width, region.Height, bg.R, bg.G, bg.B);
            byte[] chromaBgra = GrayToBgra(chromaGray);
            byte[] chromaPadded = PadBgraWithColor(chromaBgra, region.Width, region.Height, quietPad, 255, 255, 255, out int cpw, out int cph);
            float[] chromaInput = ResizeBgraToDetTensor(chromaPadded, cpw, cph, detW, detH);
            var (chromaMap, cShape) = _detSession.RunFloat(chromaInput, [1, 3, detH, detW]);
            int cmapH = cShape.Length >= 4 ? (int)cShape[2] : detH;
            int cmapW = cShape.Length >= 4 ? (int)cShape[3] : detW;
            if (chromaMap.Length >= cmapH * cmapW)
            {
                var chromaBoxes = ExtractBoxesFromProbMap(chromaMap, cmapW, cmapH, thresh: 0.22f, boxThresh: 0.26f,
                    scaleX, scaleY, quietPad, region.Width, region.Height);
                foreach (var cb in chromaBoxes)
                {
                    // Only add boxes from the chromatic pass that cover genuinely NEW regions missed by RGB DBNet
                    bool overlapsExisting = boxes.Any(eb => ComputeBoxOverlapRatio(cb, eb) >= 0.25);
                    if (!overlapsExisting)
                        boxes.Add(cb);
                }
            }
        }

        return DeduplicateBoxes(boxes);
    }

    private static double ComputeBoxOverlapRatio(TextBox a, TextBox b)
    {
        int ix0 = Math.Max(a.X, b.X);
        int iy0 = Math.Max(a.Y, b.Y);
        int ix1 = Math.Min(a.X + a.W, b.X + b.W);
        int iy1 = Math.Min(a.Y + a.H, b.Y + b.H);
        int inter = Math.Max(0, ix1 - ix0) * Math.Max(0, iy1 - iy0);
        int minArea = Math.Max(1, Math.Min(a.W * a.H, b.W * b.H));
        return (double)inter / minArea;
    }

    private static List<TextBox> ExtractBoxesFromProbMap(
        float[] prob,
        int w,
        int h,
        float thresh,
        float boxThresh,
        double scaleX,
        double scaleY,
        int quietPad,
        int origW,
        int origH)
    {
        bool[] visited = new bool[w * h];
        var queue = new Queue<int>(256);
        var result = new List<TextBox>();

        int[] dx = [-1, 0, 1, -1, 1, -1, 0, 1];
        int[] dy = [-1, -1, -1, 0, 0, 1, 1, 1];

        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int idx = row + x;
                if (visited[idx] || prob[idx] < thresh)
                    continue;

                visited[idx] = true;
                queue.Clear();
                queue.Enqueue(idx);

                int minX = x, maxX = x, minY = y, maxY = y;
                int count = 0;
                double sumProb = 0.0;

                while (queue.Count > 0)
                {
                    int cur = queue.Dequeue();
                    int cx = cur % w;
                    int cy = cur / w;

                    if (cx < minX) minX = cx;
                    if (cx > maxX) maxX = cx;
                    if (cy < minY) minY = cy;
                    if (cy > maxY) maxY = cy;
                    count++;
                    sumProb += prob[cur];

                    for (int k = 0; k < 8; k++)
                    {
                        int nx = cx + dx[k];
                        int ny = cy + dy[k];
                        if ((uint)nx < (uint)w && (uint)ny < (uint)h)
                        {
                            int nIdx = ny * w + nx;
                            if (!visited[nIdx] && prob[nIdx] >= thresh)
                            {
                                visited[nIdx] = true;
                                queue.Enqueue(nIdx);
                            }
                        }
                    }
                }

                int bw = maxX - minX + 1;
                int bh = maxY - minY + 1;
                if (count < 6 || bw < 3 || bh < 3)
                    continue;

                double meanScore = sumProb / count;
                if (meanScore < boxThresh)
                    continue;

                // DBNet Vatti unclip approximation: d = area * unclip_ratio / perimeter
                double boxArea = (double)bw * bh;
                double perimeter = 2.0 * (bw + bh);
                double unclip = (boxArea * 1.55) / Math.Max(1.0, perimeter);

                double expandX = Math.Clamp(unclip * 1.20, 4.0, Math.Max(6.0, bh * 0.95));
                double expandY = Math.Clamp(unclip * 0.95, 3.0, Math.Max(5.0, bh * 0.65));

                double rx0 = (minX - expandX) / scaleX - quietPad;
                double ry0 = (minY - expandY) / scaleY - quietPad;
                double rx1 = (maxX + 1 + expandX) / scaleX - quietPad;
                double ry1 = (maxY + 1 + expandY) / scaleY - quietPad;

                int x0 = Math.Clamp((int)Math.Floor(rx0), 0, origW - 1);
                int y0 = Math.Clamp((int)Math.Floor(ry0), 0, origH - 1);
                int x1 = Math.Clamp((int)Math.Ceiling(rx1), x0 + 1, origW);
                int y1 = Math.Clamp((int)Math.Ceiling(ry1), y0 + 1, origH);

                if (x1 - x0 >= 4 && y1 - y0 >= 4)
                    result.Add(new TextBox(x0, y0, x1 - x0, y1 - y0, meanScore));
            }
        }

        return result;
    }

    private static List<TextBox> DeduplicateBoxes(List<TextBox> boxes)
    {
        if (boxes.Count <= 1)
            return boxes;

        var ordered = boxes.OrderByDescending(b => b.W * b.H).ToList();
        var kept = new List<TextBox>(ordered.Count);

        foreach (var cand in ordered)
        {
            bool redundant = false;
            foreach (var existing in kept)
            {
                int ix0 = Math.Max(cand.X, existing.X);
                int iy0 = Math.Max(cand.Y, existing.Y);
                int ix1 = Math.Min(cand.X + cand.W, existing.X + existing.W);
                int iy1 = Math.Min(cand.Y + cand.H, existing.Y + existing.H);
                int inter = Math.Max(0, ix1 - ix0) * Math.Max(0, iy1 - iy0);
                int candArea = Math.Max(1, cand.W * cand.H);
                if ((double)inter / candArea >= 0.82)
                {
                    redundant = true;
                    break;
                }
            }
            if (!redundant)
                kept.Add(cand);
        }

        return kept;
    }

    private (string Text, double Confidence) RecognizeLineCropBest(CapturedScreenRegion crop, string? preferredLang)
    {
        // Pass 1: Clean RGB crop (best for normal fonts, ClearType, colorful handwriting, gradients)
        var best = RecognizeSingleVariant(crop.Bgra32Pixels, crop.Width, crop.Height, preferredLang);

        // Pass 2: If confidence is below 0.82 (e.g., low-contrast muted gray, noisy photo, colored-on-colored gradient),
        // also evaluate our local RGB chromatic contrast & normalized grayscale variants and pick the highest CTC confidence!
        if (best.Confidence < 0.82 || string.IsNullOrWhiteSpace(best.Text))
        {
            var bg = OcrImagePreprocessor.EstimateBorderBackground(crop.Bgra32Pixels, crop.Width, crop.Height);
            byte[] chromaGray = OcrImagePreprocessor.BuildChromaticDistanceGrayscale(crop.Bgra32Pixels, crop.Width, crop.Height, bg.R, bg.G, bg.B);
            byte[] chromaBgra = GrayToBgra(chromaGray);
            var chromaRes = RecognizeSingleVariant(chromaBgra, crop.Width, crop.Height, preferredLang);
            if (IsBetterRecResult(chromaRes, best))
                best = chromaRes;

            if (best.Confidence < 0.80 || string.IsNullOrWhiteSpace(best.Text))
            {
                bool isDark = bg.Luminance < 126.0;
                byte[] normGray = OcrImagePreprocessor.BuildNormalizedGrayscale(crop.Bgra32Pixels, crop.Width, crop.Height, invertForDarkBg: isDark);
                byte[] normBgra = GrayToBgra(normGray);
                var normRes = RecognizeSingleVariant(normBgra, crop.Width, crop.Height, preferredLang);
                if (IsBetterRecResult(normRes, best))
                    best = normRes;
            }
        }

        return best;
    }

    private (string Text, double Confidence) RecognizeSingleVariant(byte[] bgra, int w, int h, string? preferredLang)
    {
        if (_eslavRecSession is null)
            return (string.Empty, 0.0);

        var (tensor, recW) = BuildRecTensor(bgra, w, h, targetHeight: 48);
        var eslavRes = RunRecSession(_eslavRecSession, _eslavDict, tensor, 48, recW);

        // If English recognizer is available and text looks Latin-only (no Cyrillic, no Greek/math symbols unique to eslav),
        // run en_PP-OCRv5 too and prefer en only when it is clearly more confident without losing characters.
        bool hasEslavSpecific = eslavRes.Text.Any(c =>
            (c >= 'А' && c <= 'я') ||
            c is 'Ё' or 'ё' or 'І' or 'і' or 'Ї' or 'ї' or 'Є' or 'є' or 'Ґ' or 'ґ' or
                 'α' or 'β' or 'γ' or 'δ' or 'ε' or 'θ' or 'λ' or 'μ' or 'π' or 'σ' or 'φ' or 'ψ' or 'ω' or
                 'Γ' or 'Δ' or 'Θ' or 'Λ' or 'Π' or 'Σ' or 'Φ' or 'Ψ' or 'Ω' or
                 '±' or '≤' or '≥' or '≠' or '≈' or '∞' or '√' or '∫' or '∑' or '₽' or '№');
        bool forceEn = !string.IsNullOrWhiteSpace(preferredLang) && preferredLang.StartsWith("en", StringComparison.OrdinalIgnoreCase);

        if (_enRecSession is not null && (!hasEslavSpecific || forceEn))
        {
            var enRes = RunRecSession(_enRecSession, _enDict, tensor, 48, recW);
            if (forceEn && !string.IsNullOrWhiteSpace(enRes.Text) && enRes.Confidence >= eslavRes.Confidence - 0.06)
                return enRes;
            // Only let en_rec replace eslav_rec if it does not truncate the reading and has higher confidence
            if (enRes.Text.Length >= eslavRes.Text.Length && IsBetterRecResult(enRes, eslavRes))
                return enRes;
        }

        return eslavRes;
    }

    private static bool IsBetterRecResult((string Text, double Confidence) candidate, (string Text, double Confidence) current)
    {
        if (string.IsNullOrWhiteSpace(candidate.Text))
            return false;
        if (string.IsNullOrWhiteSpace(current.Text))
            return true;

        double candScore = candidate.Confidence + Math.Min(0.06, candidate.Text.Count(char.IsLetterOrDigit) * 0.004);
        double currScore = current.Confidence + Math.Min(0.06, current.Text.Count(char.IsLetterOrDigit) * 0.004);
        return candScore > currScore + 0.015;
    }

    private static (string Text, double Confidence) RunRecSession(
        OrtNativeRuntime.OrtFloatSession session,
        IReadOnlyList<string> dict,
        float[] tensor,
        int h,
        int w)
    {
        var (probs, shape) = session.RunFloat(tensor, [1, 3, h, w]);
        if (shape.Length < 3)
            return (string.Empty, 0.0);

        int timeSteps = (int)shape[1];
        int numClasses = (int)shape[2];
        if (probs.Length < timeSteps * numClasses)
            return (string.Empty, 0.0);

        // Standard PaddleOCR CTC decoding: index 0 = blank, 1..dict.Count = dict[i-1], dict.Count+1 = space (' ')
        bool hasTrailingSpace = numClasses >= dict.Count + 2;
        var sb = new StringBuilder(timeSteps);
        int prevIdx = 0;
        double confSum = 0.0;
        int charCount = 0;

        for (int t = 0; t < timeSteps; t++)
        {
            int offset = t * numClasses;
            int bestIdx = 0;
            float bestVal = probs[offset];
            for (int c = 1; c < numClasses; c++)
            {
                float v = probs[offset + c];
                if (v > bestVal)
                {
                    bestVal = v;
                    bestIdx = c;
                }
            }

            if (bestIdx != 0 && bestIdx != prevIdx)
            {
                if (bestIdx - 1 < dict.Count)
                {
                    sb.Append(dict[bestIdx - 1]);
                    confSum += bestVal;
                    charCount++;
                }
                else if (hasTrailingSpace && bestIdx == dict.Count + 1)
                {
                    sb.Append(' ');
                }
            }

            prevIdx = bestIdx;
        }

        double avgConf = charCount > 0 ? confSum / charCount : 0.0;
        return (sb.ToString().Trim(), avgConf);
    }

    /// <summary>
    /// Orders detected boxes into natural reading lines (top-to-bottom, left-to-right),
    /// and merges stacked compact numerator/denominator pairs (e.g. "3" over "5" right of "=") into "3/5".
    /// </summary>
    private static string AssembleReadingOrder(List<RecognizedBox> boxes)
    {
        if (boxes.Count == 0)
            return string.Empty;
        if (boxes.Count == 1)
            return boxes[0].Text;

        // Detect stacked vertical math fraction pairs (e.g. "3" directly over "5" to the right of "sin α =")
        // Strict guard: both numerator & denominator must look like math terms (numbers or 1-2 char math vars),
        // and the left partner must actually contain a math operator like "=", "+", "-", "≈", etc.
        var merged = new List<RecognizedBox>(boxes);
        for (int i = 0; i < merged.Count; i++)
        {
            var top = merged[i];
            if (!LooksLikeFractionPart(top.Text) || top.W > top.H * 2.4) continue;

            for (int j = 0; j < merged.Count; j++)
            {
                if (i == j) continue;
                var bot = merged[j];
                if (!LooksLikeFractionPart(bot.Text) || bot.W > bot.H * 2.4) continue;
                if (bot.Y <= top.Y + top.H * 0.5) continue;

                int xOverlap = Math.Min(top.X + top.W, bot.X + bot.W) - Math.Max(top.X, bot.X);
                int minW = Math.Max(1, Math.Min(top.W, bot.W));
                int verticalGap = bot.Y - (top.Y + top.H);
                int maxH = Math.Max(top.H, bot.H);

                if (xOverlap >= minW * 0.45 && verticalGap >= -maxH * 0.25 && verticalGap <= maxH * 1.45)
                {
                    int midY = (top.Y + bot.Y + bot.H) / 2;
                    bool hasLeftMathOperator = merged.Any(other =>
                        other != top && other != bot &&
                        other.X + other.W <= Math.Min(top.X, bot.X) + 20 &&
                        midY >= other.Y - other.H && midY <= other.Y + other.H * 2 &&
                        (other.Text.Contains('=') || other.Text.Contains('+') || other.Text.Contains('-') ||
                         other.Text.Contains('±') || other.Text.Contains('≈') || other.Text.Contains('≤') || other.Text.Contains('≥')));

                    if (hasLeftMathOperator)
                    {
                        int nx = Math.Min(top.X, bot.X);
                        int ny = top.Y;
                        int nw = Math.Max(top.X + top.W, bot.X + bot.W) - nx;
                        int nh = (bot.Y + bot.H) - ny;
                        var fracBox = new RecognizedBox(nx, ny, nw, nh, $"{top.Text}/{bot.Text}", (top.Confidence + bot.Confidence) * 0.5);

                        int hi = Math.Max(i, j);
                        int lo = Math.Min(i, j);
                        merged.RemoveAt(hi);
                        merged.RemoveAt(lo);
                        merged.Add(fracBox);
                        i = -1; // restart scan cleanly
                        break;
                    }
                }
            }
        }

        // Cluster boxes into reading lines by vertical center proximity
        var sorted = merged.OrderBy(b => b.Y + b.H * 0.5).ThenBy(b => b.X).ToList();
        var lines = new List<List<RecognizedBox>>();

        foreach (var box in sorted)
        {
            double cy = box.Y + box.H * 0.5;
            List<RecognizedBox>? matchedLine = null;
            foreach (var line in lines)
            {
                double lineCy = line.Average(b => b.Y + b.H * 0.5);
                double lineAvgH = line.Average(b => (double)b.H);
                if (Math.Abs(cy - lineCy) <= Math.Max(lineAvgH, box.H) * 0.58)
                {
                    matchedLine = line;
                    break;
                }
            }

            if (matchedLine is not null)
                matchedLine.Add(box);
            else
                lines.Add([box]);
        }

        var outputLines = new List<string>(lines.Count);
        foreach (var line in lines.OrderBy(l => l.Average(b => b.Y)))
        {
            var leftToRight = line.OrderBy(b => b.X).ToList();
            outputLines.Add(string.Join(" ", leftToRight.Select(b => b.Text)));
        }

        return string.Join(Environment.NewLine, outputLines);
    }

    private static bool LooksLikeFractionPart(string text)
    {
        string t = text.Trim();
        if (t.Length == 0 || t.Length > 4)
            return false;

        // Pure digits (e.g. "3", "5", "12") or 1-2 char math variables/constants (e.g. "x", "2x", "π", "α")
        int letters = t.Count(char.IsLetter);
        int digits = t.Count(char.IsDigit);
        if (digits > 0 && letters <= 1)
            return true;
        if (letters == 1 && t.Length <= 2)
            return true;
        return false;
    }

    private static (float[] Tensor, int RecWidth) BuildRecTensor(byte[] bgra, int w, int h, int targetHeight)
    {
        // Add subtle side padding (4px) using border color so edge glyphs (first/last letter) have room for CTC peaks
        var bg = OcrImagePreprocessor.EstimateBorderBackground(bgra, w, h);
        int sidePad = Math.Clamp(h / 8, 3, 10);
        int topPad = Math.Clamp(h / 12, 2, 6);
        int pw = w + sidePad * 2;
        int ph = h + topPad * 2;
        byte[] padded = new byte[pw * ph * 4];
        for (int i = 0; i < pw * ph; i++)
        {
            int p = i * 4;
            padded[p + 0] = bg.B;
            padded[p + 1] = bg.G;
            padded[p + 2] = bg.R;
            padded[p + 3] = 255;
        }
        for (int y = 0; y < h; y++)
            Buffer.BlockCopy(bgra, y * w * 4, padded, ((y + topPad) * pw + sidePad) * 4, w * 4);

        double ratio = (double)pw / Math.Max(1, ph);
        int recW = Math.Clamp((int)Math.Ceiling(targetHeight * ratio), 32, 1600);

        // NCHW BGR normalized to [-1, +1]: (px / 255.0 - 0.5) / 0.5
        int planeSize = targetHeight * recW;
        float[] tensor = new float[3 * planeSize];
        double scaleX = (double)pw / recW;
        double scaleY = (double)ph / targetHeight;

        for (int y = 0; y < targetHeight; y++)
        {
            double sy = (y + 0.5) * scaleY - 0.5;
            int y0 = Math.Clamp((int)Math.Floor(sy), 0, ph - 1);
            int y1 = Math.Min(ph - 1, y0 + 1);
            double fy = Math.Clamp(sy - y0, 0.0, 1.0);
            double iy = 1.0 - fy;

            int row0 = y0 * pw * 4;
            int row1 = y1 * pw * 4;
            int outRow = y * recW;

            for (int x = 0; x < recW; x++)
            {
                double sx = (x + 0.5) * scaleX - 0.5;
                int x0 = Math.Clamp((int)Math.Floor(sx), 0, pw - 1);
                int x1 = Math.Min(pw - 1, x0 + 1);
                double fx = Math.Clamp(sx - x0, 0.0, 1.0);
                double ix = 1.0 - fx;

                int i00 = row0 + x0 * 4;
                int i10 = row0 + x1 * 4;
                int i01 = row1 + x0 * 4;
                int i11 = row1 + x1 * 4;

                double b = (padded[i00 + 0] * ix + padded[i10 + 0] * fx) * iy + (padded[i01 + 0] * ix + padded[i11 + 0] * fx) * fy;
                double g = (padded[i00 + 1] * ix + padded[i10 + 1] * fx) * iy + (padded[i01 + 1] * ix + padded[i11 + 1] * fx) * fy;
                double r = (padded[i00 + 2] * ix + padded[i10 + 2] * fx) * iy + (padded[i01 + 2] * ix + padded[i11 + 2] * fx) * fy;

                int dst = outRow + x;
                tensor[dst] = (float)(b / 127.5 - 1.0);
                tensor[planeSize + dst] = (float)(g / 127.5 - 1.0);
                tensor[2 * planeSize + dst] = (float)(r / 127.5 - 1.0);
            }
        }

        return (tensor, recW);
    }

    private static float[] ResizeBgraToDetTensor(byte[] bgra, int srcW, int srcH, int dstW, int dstH)
    {
        const float mean0 = 0.485f, mean1 = 0.456f, mean2 = 0.406f;
        const float invStd0 = 1f / 0.229f, invStd1 = 1f / 0.224f, invStd2 = 1f / 0.225f;

        int planeSize = dstW * dstH;
        float[] tensor = new float[3 * planeSize];
        double scaleX = (double)srcW / dstW;
        double scaleY = (double)srcH / dstH;

        for (int y = 0; y < dstH; y++)
        {
            double sy = (y + 0.5) * scaleY - 0.5;
            int y0 = Math.Clamp((int)Math.Floor(sy), 0, srcH - 1);
            int y1 = Math.Min(srcH - 1, y0 + 1);
            double fy = Math.Clamp(sy - y0, 0.0, 1.0);
            double iy = 1.0 - fy;

            int row0 = y0 * srcW * 4;
            int row1 = y1 * srcW * 4;
            int outRow = y * dstW;

            for (int x = 0; x < dstW; x++)
            {
                double sx = (x + 0.5) * scaleX - 0.5;
                int x0 = Math.Clamp((int)Math.Floor(sx), 0, srcW - 1);
                int x1 = Math.Min(srcW - 1, x0 + 1);
                double fx = Math.Clamp(sx - x0, 0.0, 1.0);
                double ix = 1.0 - fx;

                int i00 = row0 + x0 * 4;
                int i10 = row0 + x1 * 4;
                int i01 = row1 + x0 * 4;
                int i11 = row1 + x1 * 4;

                float b = (float)(((bgra[i00 + 0] * ix + bgra[i10 + 0] * fx) * iy + (bgra[i01 + 0] * ix + bgra[i11 + 0] * fx) * fy) / 255.0);
                float g = (float)(((bgra[i00 + 1] * ix + bgra[i10 + 1] * fx) * iy + (bgra[i01 + 1] * ix + bgra[i11 + 1] * fx) * fy) / 255.0);
                float r = (float)(((bgra[i00 + 2] * ix + bgra[i10 + 2] * fx) * iy + (bgra[i01 + 2] * ix + bgra[i11 + 2] * fx) * fy) / 255.0);

                int dst = outRow + x;
                tensor[dst] = (b - mean0) * invStd0;
                tensor[planeSize + dst] = (g - mean1) * invStd1;
                tensor[2 * planeSize + dst] = (r - mean2) * invStd2;
            }
        }

        return tensor;
    }

    private static CapturedScreenRegion? CropRegion(CapturedScreenRegion src, int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0 || x < 0 || y < 0 || x + w > src.Width || y + h > src.Height)
            return null;

        byte[] cropped = new byte[w * h * 4];
        int srcStride = src.Width * 4;
        int dstStride = w * 4;
        for (int row = 0; row < h; row++)
            Buffer.BlockCopy(src.Bgra32Pixels, (y + row) * srcStride + x * 4, cropped, row * dstStride, dstStride);

        return new CapturedScreenRegion(w, h, cropped);
    }

    private static byte[] PadBgraWithColor(byte[] src, int w, int h, int pad, byte b, byte g, byte r, out int outW, out int outH)
    {
        outW = w + pad * 2;
        outH = h + pad * 2;
        byte[] dst = new byte[outW * outH * 4];
        for (int i = 0; i < outW * outH; i++)
        {
            int p = i * 4;
            dst[p + 0] = b;
            dst[p + 1] = g;
            dst[p + 2] = r;
            dst[p + 3] = 255;
        }
        int srcStride = w * 4;
        int dstStride = outW * 4;
        for (int y = 0; y < h; y++)
            Buffer.BlockCopy(src, y * srcStride, dst, (y + pad) * dstStride + pad * 4, srcStride);
        return dst;
    }

    private static byte[] GrayToBgra(byte[] gray)
    {
        byte[] bgra = new byte[gray.Length * 4];
        for (int i = 0; i < gray.Length; i++)
        {
            byte v = gray[i];
            int p = i * 4;
            bgra[p + 0] = v;
            bgra[p + 1] = v;
            bgra[p + 2] = v;
            bgra[p + 3] = 255;
        }
        return bgra;
    }

    internal static IReadOnlyList<string> LoadCharacterDictFromYaml(string ymlPath)
    {
        var chars = new List<string>(512);
        bool inDict = false;

        foreach (string rawLine in File.ReadAllLines(ymlPath, Encoding.UTF8))
        {
            if (!inDict)
            {
                if (rawLine.TrimStart().StartsWith("character_dict:", StringComparison.Ordinal))
                    inDict = true;
                continue;
            }

            if (rawLine.Length > 0 && !char.IsWhiteSpace(rawLine[0]))
                break;

            string trimmed = rawLine.Trim();
            if (!trimmed.StartsWith("- ", StringComparison.Ordinal))
                continue;

            string val = trimmed[2..];
            if (val.Length >= 2 && val[0] == '\'' && val[^1] == '\'')
            {
                val = val[1..^1].Replace("''", "'");
            }
            else if (val.Length >= 2 && val[0] == '"' && val[^1] == '"')
            {
                val = val[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
            }

            chars.Add(val);
        }

        return chars;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _detSession?.Dispose();
        _eslavRecSession?.Dispose();
        _enRecSession?.Dispose();
        _initLock.Dispose();
    }
}
