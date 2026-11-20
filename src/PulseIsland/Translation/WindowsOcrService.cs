using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PulseIsland.Core;
using PulseIsland.Interop;
using PulseIsland.Services;

#if WITH_WINRT
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
#endif

namespace PulseIsland.Translation;

/// <summary>
/// Performs high-accuracy OCR on captured screen regions using Windows.Media.Ocr.
/// Automatically upscales small text regions, adds border padding, and evaluates all installed
/// Windows OCR language packs (prioritizing English/Latin and Cyrillic) so multi-language text is recognized reliably.
/// </summary>
public sealed class WindowsOcrService
{
    public async Task<OcrResult> RecognizeAsync(
        CapturedScreenRegion region,
        bool autoDetectLanguage,
        string preferredLanguageTag = "en",
        CancellationToken ct = default)
    {
#if WITH_WINRT
        return await RecognizeViaNativeWinRtAsync(region, autoDetectLanguage, preferredLanguageTag);
#else
        return await RecognizeViaInMemoryWinRtBridgeAsync(region, autoDetectLanguage, preferredLanguageTag, ct);
#endif
    }

#if WITH_WINRT
    private static async Task<OcrResult> RecognizeViaNativeWinRtAsync(
        CapturedScreenRegion region,
        bool autoDetectLanguage,
        string preferredLanguageTag)
    {
        try
        {
            var engines = new List<OcrEngine>();
            if (!autoDetectLanguage && !string.IsNullOrWhiteSpace(preferredLanguageTag))
            {
                var custom = OcrEngine.TryCreateFromLanguage(new Language(preferredLanguageTag));
                if (custom is not null) engines.Add(custom);
            }

            foreach (var lang in OcrEngine.AvailableRecognizerLanguages
                         .OrderByDescending(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                         .ThenByDescending(l => l.LanguageTag.StartsWith("ru", StringComparison.OrdinalIgnoreCase)))
            {
                var eng = OcrEngine.TryCreateFromLanguage(lang);
                if (eng is not null && engines.All(e => e.RecognizerLanguage.LanguageTag != eng.RecognizerLanguage.LanguageTag))
                    engines.Add(eng);
            }

            if (engines.Count == 0)
            {
                var fallback = OcrEngine.TryCreateFromUserProfileLanguages();
                if (fallback is not null) engines.Add(fallback);
            }

            if (engines.Count == 0)
                return new OcrResult(false, string.Empty, "und", LocalizationService.Pick("No Windows OCR language packs are installed.", "В Windows не установлено ни одного языкового пакета OCR."));

            using var softwareBitmap = new SoftwareBitmap(
                BitmapPixelFormat.Bgra8,
                region.Width,
                region.Height,
                BitmapAlphaMode.Premultiplied);
            softwareBitmap.CopyFromBuffer(region.Bgra32Pixels.AsBuffer());

            string bestText = string.Empty;
            string bestLang = engines[0].RecognizerLanguage.LanguageTag;
            int bestScore = -1;

            foreach (var engine in engines)
            {
                var ocrResult = await engine.RecognizeAsync(softwareBitmap);
                var text = FormatOcrLines(ocrResult);
                int score = ScoreRecognizedText(text);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestText = text;
                    bestLang = engine.RecognizerLanguage.LanguageTag;
                }
            }

            if (string.IsNullOrWhiteSpace(bestText))
                return new OcrResult(false, string.Empty, bestLang, LocalizationService.Pick("No text recognized in the selected area.", "Текст в выделенной области не распознан."));

            return new OcrResult(true, bestText, bestLang);
        }
        catch (Exception ex)
        {
            LogService.Error("Native WinRT OCR failed.", ex);
            return new OcrResult(false, string.Empty, "und", ex.Message);
        }
    }

    private static string FormatOcrLines(Windows.Media.Ocr.OcrResult result)
    {
        if (result.Lines is null || result.Lines.Count == 0)
            return result.Text?.Trim() ?? string.Empty;

        return string.Join(Environment.NewLine, result.Lines.Select(l => l.Text.Trim()).Where(t => !string.IsNullOrWhiteSpace(t)));
    }
#else
    private static async Task<OcrResult> RecognizeViaInMemoryWinRtBridgeAsync(
        CapturedScreenRegion region,
        bool autoDetectLanguage,
        string preferredLanguageTag,
        CancellationToken ct)
    {
        try
        {
            string pngBase64 = EncodePreparedPngBase64(region);
            string langArg = autoDetectLanguage ? "auto" : preferredLanguageTag;

            // Evaluates all installed Windows.Media.Ocr engines (prioritizing English & Russian) completely in memory
            const string script = @"
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType=WindowsRuntime]
$null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Foundation, ContentType=WindowsRuntime]
$null = [Windows.Storage.Streams.InMemoryRandomAccessStream, Windows.Foundation, ContentType=WindowsRuntime]
$null = [Windows.Globalization.Language, Windows.Foundation, ContentType=WindowsRuntime]

$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
function Await($WinRtTask, $ResultType) {
    $netTask = $asTaskGeneric.MakeGenericMethod($ResultType).Invoke($null, @($WinRtTask))
    $netTask.Wait(-1) | Out-Null
    return $netTask.Result
}

$inputLine = [Console]::In.ReadLine()
$parts = $inputLine.Split('|', 2)
$langTag = $parts[0]
$bytes = [Convert]::FromBase64String($parts[1])

$memStream = New-Object Windows.Storage.Streams.InMemoryRandomAccessStream
$writer = New-Object Windows.Storage.Streams.DataWriter($memStream)
$writer.WriteBytes($bytes)
$null = Await ($writer.StoreAsync()) ([uint32])
$null = Await ($writer.FlushAsync()) ([bool])
$writer.DetachStream() | Out-Null
$memStream.Seek(0)

$decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($memStream)) ([Windows.Graphics.Imaging.BitmapDecoder])
$bitmap = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])

$engines = New-Object System.Collections.Generic.List[Windows.Media.Ocr.OcrEngine]
if ($langTag -ne 'auto') {
    try {
        $custom = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage((New-Object Windows.Globalization.Language($langTag)))
        if ($null -ne $custom) { $engines.Add($custom) }
    } catch {}
}

$avail = [Windows.Media.Ocr.OcrEngine]::AvailableRecognizerLanguages
foreach ($l in $avail) {
    if ($l.LanguageTag -like 'en*') {
        $e = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($l)
        if ($null -ne $e) { $engines.Add($e) }
    }
}
foreach ($l in $avail) {
    if ($l.LanguageTag -notlike 'en*') {
        $e = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($l)
        if ($null -ne $e) { $engines.Add($e) }
    }
}
if ($engines.Count -eq 0) {
    $prof = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
    if ($null -ne $prof) { $engines.Add($prof) }
}
if ($engines.Count -eq 0) {
    Write-Output (@{ ok = $false; text = ''; lang = 'und'; err = 'NO_OCR_LANG_PACKS' } | ConvertTo-Json -Compress)
    exit 0
}

$bestText = ''
$bestLang = $engines[0].RecognizerLanguage.LanguageTag
$bestScore = -1

foreach ($eng in $engines) {
    $ocr = Await ($eng.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
    $lines = @()
    foreach ($line in $ocr.Lines) {
        if (-not [string]::IsNullOrWhiteSpace($line.Text)) {
            $lines += $line.Text.Trim()
        }
    }
    $txt = ($lines -join ""`n"").Trim()
    if ([string]::IsNullOrWhiteSpace($txt)) { $txt = [string]$ocr.Text }

    $letters = 0
    foreach ($ch in $txt.ToCharArray()) {
        if ([char]::IsLetterOrDigit($ch)) { $letters++ }
    }
    if ($letters -gt $bestScore) {
        $bestScore = $letters
        $bestText = $txt
        $bestLang = $eng.RecognizerLanguage.LanguageTag
    }
}

$ok = -not [string]::IsNullOrWhiteSpace($bestText)
Write-Output (@{ ok = $ok; text = $bestText; lang = $bestLang; err = '' } | ConvertTo-Json -Compress)
";

            string encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encodedCommand}",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return new OcrResult(false, string.Empty, "und", LocalizationService.Pick("Failed to start text recognition process.", "Не удалось запустить процесс распознавания текста."));

            await proc.StandardInput.WriteLineAsync($"{langArg}|{pngBase64}".AsMemory(), ct);
            proc.StandardInput.Close();

            string output = await proc.StandardOutput.ReadToEndAsync(ct);
            string errOutput = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            if (string.IsNullOrWhiteSpace(output))
            {
                LogService.Warn($"Windows OCR bridge empty output. Stderr: {errOutput}");
                return new OcrResult(false, string.Empty, "und", LocalizationService.Pick("OCR module returned no data.", "Модуль OCR не вернул данных."));
            }

            using var doc = JsonDocument.Parse(output.Trim());
            var root = doc.RootElement;
            bool ok = root.GetProperty("ok").GetBoolean();
            string text = root.GetProperty("text").GetString()?.Trim() ?? string.Empty;
            string lang = root.GetProperty("lang").GetString() ?? "und";
            string err = root.GetProperty("err").GetString() ?? string.Empty;

            if (err == "NO_OCR_LANG_PACKS")
                err = LocalizationService.Pick("No Windows OCR language packs found on this system.", "В системе не найдены языковые пакеты Windows OCR.");

            if (!ok || string.IsNullOrWhiteSpace(text))
                return new OcrResult(false, string.Empty, lang, string.IsNullOrWhiteSpace(err)
                    ? LocalizationService.Pick("No text detected in selected area. Try circling a bit wider.", "Текст в выделенной области не обнаружен. Попробуйте обвести текст чуть шире.")
                    : err);

            return new OcrResult(true, text, lang);
        }
        catch (Exception ex)
        {
            LogService.Error("In-memory WinRT OCR bridge failed.", ex);
            return new OcrResult(false, string.Empty, "und", ex.Message);
        }
    }

    /// <summary>
    /// Upscales small screen captures (e.g. 12pt-14pt UI fonts) by 2x-3x and adds a clean quiet zone
    /// margin around the bitmap so Windows.Media.Ocr achieves high recognition accuracy on small text.
    /// </summary>
    private static string EncodePreparedPngBase64(CapturedScreenRegion region)
    {
        var rawBmp = BitmapSource.Create(
            region.Width,
            region.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            region.Bgra32Pixels,
            region.Width * 4);
        rawBmp.Freeze();

        double scale = 1.0;
        if (region.Height < 55 || region.Width < 140)
            scale = 2.75;
        else if (region.Height < 110)
            scale = 2.0;
        else if (region.Height < 180)
            scale = 1.5;

        BitmapSource finalBmp = rawBmp;
        if (scale > 1.01)
        {
            var scaled = new TransformedBitmap(rawBmp, new ScaleTransform(scale, scale));
            scaled.Freeze();
            finalBmp = scaled;
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(finalBmp));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return Convert.ToBase64String(ms.ToArray());
    }
#endif

    private static int ScoreRecognizedText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        int score = 0;
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c)) score++;
        }
        return score;
    }
}
