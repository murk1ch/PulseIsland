using System.Windows;
using PulseIsland.Core;
using PulseIsland.Interop;
using PulseIsland.Services;

namespace PulseIsland.Translation;

public sealed class TranslationCoordinator : IDisposable
{
    private readonly WindowsOcrService _ocrService;
    private readonly OnlineTranslationService _onlineService;
    private readonly OnnxOfflineTranslationService _offlineService;
    private readonly SettingsService _settingsService;

    public TranslationCoordinator(
        WindowsOcrService ocrService,
        OnlineTranslationService onlineService,
        OnnxOfflineTranslationService offlineService,
        SettingsService settingsService)
    {
        _ocrService = ocrService;
        _onlineService = onlineService;
        _offlineService = offlineService;
        _settingsService = settingsService;
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

        var settings = _settingsService.Current;
        var ocr = await _ocrService.RecognizeAsync(
            region,
            settings.AutoDetectSourceLanguage,
            settings.ManualSourceLanguage,
            ct);

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

        var translation = await TranslateTextInternalAsync(ocr.Text, ocr.DetectedLanguageTag, ct);
        return (ocr, translation);
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

    private async Task<TranslationResult> TranslateTextInternalAsync(string text, string detectedLanguageTag, CancellationToken ct)
    {
        var settings = _settingsService.Current;
        if (settings.TranslationMode == TranslationEngineMode.OfflineOnnx)
        {
            return await _offlineService.TranslateAsync(
                text,
                settings.SelectedOfflineModelId,
                detectedLanguageTag,
                settings.TargetLanguage,
                ct);
        }

        var onlineResult = await _onlineService.TranslateAsync(text, settings, ct, allowBuiltInDictionaryFallback: false);
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
                detectedLanguageTag,
                settings.TargetLanguage,
                ct);
        }

        string sourceLang = settings.AutoDetectSourceLanguage ? detectedLanguageTag : settings.ManualSourceLanguage;
        LogService.Info("Online endpoints unreachable and no ONNX model installed. Using BuiltInLocalTranslator fallback.");
        return BuiltInLocalTranslator.Translate(text, sourceLang, settings.TargetLanguage);
    }

    private static string GuessLanguageByScript(string text, string fallback)
    {
        int cyrillic = 0, latin = 0;
        foreach (char ch in text)
        {
            if (ch >= '\u0400' && ch <= '\u04FF') cyrillic++;
            else if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z')) latin++;
        }
        if (cyrillic > latin && cyrillic >= 2) return "ru";
        if (latin > cyrillic && latin >= 2) return "en";
        return string.IsNullOrWhiteSpace(fallback) ? "en" : fallback;
    }

    public void Dispose()
    {
        _onlineService.Dispose();
        _offlineService.Dispose();
    }
}
