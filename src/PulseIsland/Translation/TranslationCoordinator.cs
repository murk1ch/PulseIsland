using System.Windows;
using PulseIsland.Core;
using PulseIsland.Interop;
using PulseIsland.Services;

namespace PulseIsland.Translation;

public sealed class TranslationCoordinator : IDisposable
{
    private readonly WindowsOcrService _ocrService;
    private readonly PaddleOcrOnnxService _paddleOcrService = new();
    private readonly OnlineTranslationService _onlineService;
    private readonly OnnxOfflineTranslationService _offlineService;
    private readonly SettingsService _settingsService;
    private readonly LocalLlmService? _localLlmService;

    public TranslationCoordinator(
        WindowsOcrService ocrService,
        OnlineTranslationService onlineService,
        OnnxOfflineTranslationService offlineService,
        SettingsService settingsService,
        LocalLlmService? localLlmService = null)
    {
        _ocrService = ocrService;
        _onlineService = onlineService;
        _offlineService = offlineService;
        _settingsService = settingsService;
        _localLlmService = localLlmService;
    }

    public async Task<(OcrResult Ocr, TranslationResult Translation)> CaptureOcrAndTranslateAsync(
        Int32Rect physicalRect,
        CancellationToken ct = default)
    {
        var region = ScreenCaptureService.CaptureRegion(physicalRect);
        if (region is null)
        {
            var err = new OcrResult(false, string.Empty, "und", LocalizationService.Pick("Failed to capture screen region.", "Не удалось захватить область экрана."));
            return (err, new TranslationResult(false, string.Empty, "und", _settingsService.Current.TargetLanguage, false, err.ErrorMessage));
        }

        var vs = MonitorService.GetVirtualScreenPhysicalBounds();
        var expandedRect = LassoGeometryHelper.ExpandRectClamped(physicalRect, vs, expandX: 18, expandY: 28);
        CapturedScreenRegion? expandedRegion = expandedRect != physicalRect
            ? ScreenCaptureService.CaptureRegion(expandedRect)
            : null;

        return await OcrAndTranslateRegionAsync(region, expandedRegion, ct);
    }

    public async Task<(OcrResult Ocr, TranslationResult Translation)> OcrAndTranslateRegionAsync(
        CapturedScreenRegion primaryRegion,
        CapturedScreenRegion? expandedFallbackRegion = null,
        CancellationToken ct = default)
    {
        var settings = _settingsService.Current;
        string? installedVisionId = settings.OcrMode != OcrEngineMode.WindowsOcrOnly && _localLlmService is not null
            ? LocalLlmCatalogService.FindBestInstalledVisionModelId(settings.SelectedAiModelId)
            : null;

        OcrResult ocr;

        if (settings.OcrMode == OcrEngineMode.VisionLlmAlways && !string.IsNullOrWhiteSpace(installedVisionId))
        {
            ocr = await TryVisionOcrAsync(primaryRegion, installedVisionId, settings.AiBackend, ct);
            if (!ocr.Success || string.IsNullOrWhiteSpace(ocr.Text))
            {
                ocr = await RunBestLocalOcrAsync(primaryRegion, expandedFallbackRegion, settings, usePaddleNeural: true, ct);
            }
        }
        else if (settings.OcrMode == OcrEngineMode.WindowsOcrOnly)
        {
            ocr = await RunBestLocalOcrAsync(primaryRegion, expandedFallbackRegion, settings, usePaddleNeural: false, ct);
        }
        else
        {
            // AutoSmart (default): PP-OCRv5 Neural ONNX (handles cursive, handwriting, all fonts, °C, Greek/math)
            // backed up by Windows OCR and Vision LLM (Qwen 2.5 VL) when installed.
            ocr = await RunBestLocalOcrAsync(primaryRegion, expandedFallbackRegion, settings, usePaddleNeural: true, ct);

            if (!string.IsNullOrWhiteSpace(installedVisionId) &&
                (!ocr.Success || string.IsNullOrWhiteSpace(ocr.Text) || LooksSuspiciousForVisionFallback(ocr.Text)))
            {
                LogService.Info("AutoSmart OCR: invoking local Vision LLM fallback for complex/handwritten text.");
                var visionOcr = await TryVisionOcrAsync(primaryRegion, installedVisionId, settings.AiBackend, ct);
                if (visionOcr.Success && !string.IsNullOrWhiteSpace(visionOcr.Text))
                    ocr = visionOcr;
            }
        }

        if (!ocr.Success || string.IsNullOrWhiteSpace(ocr.Text))
        {
            return (ocr, new TranslationResult(
                false,
                string.Empty,
                ocr.DetectedLanguageTag,
                settings.TargetLanguage,
                settings.TranslationMode == TranslationEngineMode.OfflineOnnx,
                ocr.ErrorMessage ?? LocalizationService.Pick("No text recognized.", "Текст не распознан.")));
        }

        string verifiedSourceLang = settings.AutoDetectSourceLanguage
            ? WindowsOcrService.InferLanguageTagFromText(ocr.Text, ocr.DetectedLanguageTag)
            : settings.ManualSourceLanguage;

        // Reflow visually wrapped OCR lines (memes, subtitles, paragraphs) into whole sentences
        // so both Online and Offline ONNX translators translate complete grammatical context!
        string reflowedText = ReflowWrappedOcrLines(ocr.Text);
        var refinedOcr = ocr with { Text = reflowedText, DetectedLanguageTag = verifiedSourceLang };
        var translation = await TranslateTextInternalAsync(refinedOcr.Text, verifiedSourceLang, ct);
        return (refinedOcr, translation);
    }

    private async Task<OcrResult> RunBestLocalOcrAsync(
        CapturedScreenRegion primaryRegion,
        CapturedScreenRegion? expandedFallbackRegion,
        AppSettings settings,
        bool usePaddleNeural,
        CancellationToken ct)
    {
        OcrResult? paddleResult = null;
        double paddleConf = 0.0;

        if (usePaddleNeural && PaddleOcrOnnxService.IsInstalled())
        {
            try
            {
                var (pRes, pConf) = await _paddleOcrService.RecognizeAsync(
                    primaryRegion,
                    settings.AutoDetectSourceLanguage,
                    settings.ManualSourceLanguage,
                    ct);

                if ((!pRes.Success || string.IsNullOrWhiteSpace(pRes.Text)) && expandedFallbackRegion is not null)
                {
                    var (pRetry, pRetryConf) = await _paddleOcrService.RecognizeAsync(
                        expandedFallbackRegion,
                        settings.AutoDetectSourceLanguage,
                        settings.ManualSourceLanguage,
                        ct);
                    if (pRetry.Success && !string.IsNullOrWhiteSpace(pRetry.Text))
                    {
                        pRes = pRetry;
                        pConf = pRetryConf;
                    }
                }

                paddleResult = pRes;
                paddleConf = pConf;

                // High-confidence neural PP-OCRv5 result -> return immediately without waiting for WinRT OCR!
                if (pRes.Success && !string.IsNullOrWhiteSpace(pRes.Text) && pConf >= 0.80)
                    return pRes;
            }
            catch (Exception ex)
            {
                LogService.Warn($"PP-OCRv5 ONNX failed, falling back to Windows OCR: {ex.Message}");
            }
        }

        var winOcr = await _ocrService.RecognizeAsync(
            primaryRegion,
            settings.AutoDetectSourceLanguage,
            settings.ManualSourceLanguage,
            ct);

        if ((!winOcr.Success || string.IsNullOrWhiteSpace(winOcr.Text)) && expandedFallbackRegion is not null)
        {
            LogService.Info("Primary OCR selection returned empty; retrying on slightly expanded capture region.");
            var retryOcr = await _ocrService.RecognizeAsync(
                expandedFallbackRegion,
                settings.AutoDetectSourceLanguage,
                settings.ManualSourceLanguage,
                ct);
            if (retryOcr.Success && !string.IsNullOrWhiteSpace(retryOcr.Text))
                winOcr = retryOcr;
        }

        if (paddleResult is null || !paddleResult.Success || string.IsNullOrWhiteSpace(paddleResult.Text))
            return winOcr;

        if (!winOcr.Success || string.IsNullOrWhiteSpace(winOcr.Text))
            return paddleResult;

        // Cross-check uncertain PP-OCRv5 vs Windows OCR using our linguistic scorer
        string? pref = settings.AutoDetectSourceLanguage ? null : settings.ManualSourceLanguage;
        var (pScore, pNoise) = WindowsOcrService.ScoreCandidate(paddleResult.Text, paddleResult.DetectedLanguageTag, pref);
        var (wScore, wNoise) = WindowsOcrService.ScoreCandidate(winOcr.Text, winOcr.DetectedLanguageTag, pref);

        // Give PP-OCRv5 a slight accuracy bonus on cursive/special symbols/degrees when confidence is decent
        double paddleTotal = pScore * (0.85 + paddleConf * 0.45) - pNoise * 6.0;
        double winTotal = wScore - wNoise * 6.0;

        return paddleTotal >= winTotal ? paddleResult : winOcr;
    }

    private async Task<OcrResult> TryVisionOcrAsync(
        CapturedScreenRegion region,
        string visionModelId,
        AiComputeBackend backend,
        CancellationToken ct)
    {
        if (_localLlmService is null)
            return new OcrResult(false, string.Empty, "und", LocalizationService.Pick("Local AI service is not initialized.", "Локальный ИИ-сервис не инициализирован."));

        try
        {
            byte[] jpeg = ScreenCaptureService.EncodeToJpegBytes(region, maxSide: 1024, quality: 90);
            var (ok, text, err) = await _localLlmService.ExtractTextFromImageAsync(jpeg, backend, visionModelId, ct);
            if (!ok || string.IsNullOrWhiteSpace(text))
                return new OcrResult(false, string.Empty, "und", err);

            string lang = WindowsOcrService.InferLanguageTagFromText(text, "en");
            return new OcrResult(true, text, lang);
        }
        catch (Exception ex)
        {
            LogService.Warn($"Vision OCR failed: {ex.Message}");
            return new OcrResult(false, string.Empty, "und", ex.Message);
        }
    }

    /// <summary>
    /// Detects suspicious Windows OCR outputs (isolated garbage punctuation, tiny broken fragments,
    /// unreadable handwriting/formulas) where local Vision LLM (Qwen 2.5 VL) yields far cleaner results.
    /// </summary>
    internal static bool LooksSuspiciousForVisionFallback(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;

        string trimmed = text.Trim();
        int letters = 0, digits = 0, weird = 0;
        foreach (char c in trimmed)
        {
            if (char.IsWhiteSpace(c)) continue;
            if (char.IsLetter(c)) letters++;
            else if (char.IsDigit(c)) digits++;
            else if (c is not ('.' or ',' or '!' or '?' or ':' or ';' or '-' or '—' or '(' or ')' or '"' or '\'' or '/' or '%' or '°' or '+' or '='))
                weird++;
        }

        int total = letters + digits + weird;
        if (total <= 2 && letters <= 1)
            return true;
        if (total > 0 && (double)weird / total >= 0.35)
            return true;

        return false;
    }

    public static string ReflowWrappedOcrLines(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var rawLines = text.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrEmpty(l))
            .ToList();

        if (rawLines.Count <= 1)
            return rawLines.Count == 1 ? rawLines[0] : string.Empty;

        var paragraphs = new List<string>();
        var current = new System.Text.StringBuilder(rawLines[0]);

        for (int i = 1; i < rawLines.Count; i++)
        {
            string prev = rawLines[i - 1];
            string next = rawLines[i];

            bool prevEndsSentence = prev.Length > 0 && prev[^1] is '.' or '!' or '?' or ':' or ';';
            bool nextLooksLikeListOrHeader = next.StartsWith("•") || next.StartsWith("-") || next.StartsWith("—") || next.StartsWith("*");

            if (!prevEndsSentence && !nextLooksLikeListOrHeader)
            {
                // Hyphenated line wrap (e.g. "trans-\nlation") -> rejoin without space
                if (prev.EndsWith('-') && prev.Length >= 2 && char.IsLetter(prev[^2]) && next.Length > 0 && char.IsLower(next[0]))
                {
                    current.Length--;
                    current.Append(next);
                }
                else
                {
                    current.Append(' ').Append(next);
                }
            }
            else
            {
                paragraphs.Add(current.ToString());
                current.Clear();
                current.Append(next);
            }
        }

        if (current.Length > 0)
            paragraphs.Add(current.ToString());

        return string.Join(Environment.NewLine, paragraphs);
    }

    public async Task<TranslationResult> TranslateTextAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new TranslationResult(
                false,
                string.Empty,
                "und",
                _settingsService.Current.TargetLanguage,
                _settingsService.Current.TranslationMode == TranslationEngineMode.OfflineOnnx,
                LocalizationService.Pick("No text to translate.", "Нет текста для перевода."));
        }

        var settings = _settingsService.Current;
        string detectedLang = settings.AutoDetectSourceLanguage
            ? GuessLanguageByScript(text, settings.ManualSourceLanguage)
            : settings.ManualSourceLanguage;

        return await TranslateTextInternalAsync(text.Trim(), detectedLang, ct);
    }

    public async Task<TranslationResult> TranslateTextExplicitAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new TranslationResult(
                false,
                string.Empty,
                sourceLanguage,
                targetLanguage,
                _settingsService.Current.TranslationMode == TranslationEngineMode.OfflineOnnx,
                LocalizationService.Pick("No text to translate.", "Нет текста для перевода."));
        }

        return await TranslateTextInternalAsync(
            text.Trim(),
            sourceLanguage,
            ct,
            explicitTargetLanguage: targetLanguage,
            bypassSameLangCheck: true);
    }

    private async Task<TranslationResult> TranslateTextInternalAsync(
        string text,
        string detectedLanguageTag,
        CancellationToken ct,
        string? explicitTargetLanguage = null,
        bool bypassSameLangCheck = false)
    {
        var settings = _settingsService.Current;
        string targetCode = string.IsNullOrWhiteSpace(explicitTargetLanguage) ? settings.TargetLanguage : explicitTargetLanguage;
        string normalizedSource = LanguageCatalog.FindOrDefault(detectedLanguageTag, settings.ManualSourceLanguage).Code;
        string normalizedTarget = LanguageCatalog.FindOrDefault(targetCode, "ru").Code;

        // If recognized text is already in the target language (e.g. Russian text while target is Russian),
        // avoid garbling it through a mismatched offline model — return the clean recognized text directly.
        if (!bypassSameLangCheck &&
            settings.AutoDetectSourceLanguage &&
            !string.Equals(detectedLanguageTag, "und", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(normalizedSource, normalizedTarget, StringComparison.OrdinalIgnoreCase))
        {
            return new TranslationResult(true, text, normalizedSource, normalizedTarget, settings.TranslationMode == TranslationEngineMode.OfflineOnnx);
        }

        if (settings.TranslationMode == TranslationEngineMode.OfflineLocalLlm)
        {
            var modelEntry = LocalLlmCatalogService.FindModel(settings.SelectedAiModelId);
            if (_localLlmService is not null && LocalLlmCatalogService.IsReady(modelEntry.ModelId))
            {
                string targetLangName = LanguageCatalog.FindOrDefault(normalizedTarget, "ru").DisplayName;
                var (ok, translatedText, llmErr) = await _localLlmService.TranslateTextAsync(
                    text,
                    targetLangName,
                    settings.AiBackend,
                    modelEntry.ModelId,
                    ct);
                if (ok && !string.IsNullOrWhiteSpace(translatedText))
                    return new TranslationResult(true, translatedText, normalizedSource, normalizedTarget, true);

                return new TranslationResult(
                    false,
                    string.Empty,
                    normalizedSource,
                    normalizedTarget,
                    true,
                    llmErr ?? LocalizationService.Pick(
                        $"Failed to translate using local model {modelEntry.DisplayName}.",
                        $"Не удалось перевести через локальную модель {modelEntry.DisplayName}."));
            }

            return new TranslationResult(
                false,
                string.Empty,
                normalizedSource,
                normalizedTarget,
                true,
                LocalizationService.Pick(
                    $"Local AI model \"{modelEntry.DisplayName}\" is not downloaded. Open Settings → \"Translator & AI\" and click \"Download\" (auto-download is disabled).",
                    $"Локальная ИИ-модель «{modelEntry.DisplayName}» не скачана. Откройте Настройки → «Переводчик и ИИ» и нажмите «Скачать» (автоскачивание отключено)."));
        }

        if (settings.TranslationMode == TranslationEngineMode.OfflineOnnx)
        {
            return await _offlineService.TranslateAsync(
                text,
                settings.SelectedOfflineModelId,
                normalizedSource,
                normalizedTarget,
                ct);
        }

        bool explicitSourceKnown = !string.IsNullOrWhiteSpace(detectedLanguageTag) &&
            !string.Equals(detectedLanguageTag, "auto", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(detectedLanguageTag, "und", StringComparison.OrdinalIgnoreCase);

        var effectiveSettings = string.IsNullOrWhiteSpace(explicitTargetLanguage)
            ? settings
            : new AppSettings
            {
                AutoDetectSourceLanguage = !explicitSourceKnown,
                ManualSourceLanguage = normalizedSource,
                TargetLanguage = normalizedTarget,
                OnlineProvider = settings.OnlineProvider,
                EncryptedDeepLApiKey = settings.EncryptedDeepLApiKey
            };

        var onlineResult = await _onlineService.TranslateAsync(text, effectiveSettings, ct, allowBuiltInDictionaryFallback: false);
        if (onlineResult.Success)
            return onlineResult;

        // If online endpoints are blocked/offline, prefer any installed ONNX neural model over the tiny word dictionary.
        if (_offlineService.IsModelInstalled(settings.SelectedOfflineModelId) ||
            _offlineService.GetInstalledModelIds().Count > 0)
        {
            LogService.Info("Online endpoints unreachable. Falling back to installed ONNX offline neural model.");
            return await _offlineService.TranslateAsync(
                text,
                settings.SelectedOfflineModelId,
                normalizedSource,
                normalizedTarget,
                ct);
        }

        string sourceLang = settings.AutoDetectSourceLanguage ? normalizedSource : settings.ManualSourceLanguage;
        LogService.Info("Online endpoints unreachable and no ONNX model installed. Using BuiltInLocalTranslator fallback.");
        return BuiltInLocalTranslator.Translate(text, sourceLang, normalizedTarget);
    }

    private static string GuessLanguageByScript(string text, string fallback)
    {
        return WindowsOcrService.InferLanguageTagFromText(text, string.IsNullOrWhiteSpace(fallback) ? "en" : fallback);
    }

    public void Dispose()
    {
        _paddleOcrService.Dispose();
        _ocrService.Dispose();
        _onlineService.Dispose();
        _offlineService.Dispose();
    }
}
