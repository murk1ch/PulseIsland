using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PulseIsland.Core;
using PulseIsland.Interop;
using PulseIsland.Services;

#if WITH_WINRT
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
#endif

namespace PulseIsland.Translation;

public sealed record OcrCandidate(
    int VariantIndex,
    string EngineLanguageTag,
    string Text);

/// <summary>
/// High-reliability, multi-pass OCR service powered by Windows.Media.Ocr.
/// Uses <see cref="OcrImagePreprocessor"/> (quiet-zone padding, auto-polarity dark-mode inversion,
/// adaptive upscaling/downscaling, chromatic distance separation, and local adaptive thresholding),
/// a persistent resident WinRT OCR worker (eliminating cold PowerShell startup latency),
/// and script/plausibility-aware candidate scoring with cross-variant consensus voting.
/// </summary>
public sealed class WindowsOcrService : IDisposable
{
#if !WITH_WINRT
    private readonly SemaphoreSlim _workerLock = new(1, 1);
    private Process? _workerProcess;
    private bool _disposed;
#endif

    public Task WarmUpAsync(CancellationToken ct = default)
    {
#if WITH_WINRT
        return Task.CompletedTask;
#else
        return Task.Run(async () =>
        {
            if (_disposed) return;
            await _workerLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!_disposed)
                    EnsureWorkerRunning();
            }
            catch (Exception ex)
            {
                LogService.Warn($"OCR worker warm-up skipped: {ex.Message}");
            }
            finally
            {
                _workerLock.Release();
            }
        }, ct);
#endif
    }

    public async Task<OcrResult> RecognizeAsync(
        CapturedScreenRegion region,
        bool autoDetectLanguage,
        string preferredLanguageTag = "en",
        CancellationToken ct = default)
    {
        if (region.Width <= 0 || region.Height <= 0 || region.Bgra32Pixels.Length < region.Width * region.Height * 4)
            return new OcrResult(false, string.Empty, "und", LocalizationService.Pick("Empty screen region.", "Пустая область экрана."));

        var variants = await Task.Run(() => OcrImagePreprocessor.BuildVariants(region), ct).ConfigureAwait(false);
        if (variants.Count == 0)
            return new OcrResult(false, string.Empty, "und", LocalizationService.Pick("Failed to prepare image for OCR.", "Не удалось подготовить изображение для OCR."));

        string langArg = autoDetectLanguage || string.IsNullOrWhiteSpace(preferredLanguageTag)
            ? "auto"
            : preferredLanguageTag.Trim();

#if WITH_WINRT
        return await RecognizeViaNativeWinRtAsync(variants, autoDetectLanguage, preferredLanguageTag, ct).ConfigureAwait(false);
#else
        return await RecognizeViaResidentBridgeAsync(variants, langArg, ct).ConfigureAwait(false);
#endif
    }

#if WITH_WINRT
    private static async Task<OcrResult> RecognizeViaNativeWinRtAsync(
        IReadOnlyList<OcrPreparedVariant> variants,
        bool autoDetectLanguage,
        string preferredLanguageTag,
        CancellationToken ct)
    {
        try
        {
            var engines = BuildNativeEngines(autoDetectLanguage, preferredLanguageTag);
            if (engines.Count == 0)
                return new OcrResult(false, string.Empty, "und", LocalizationService.Pick("No Windows OCR language packs are installed.", "В Windows не установлено ни одного языкового пакета OCR."));

            var allCandidates = new List<OcrCandidate>();

            // Stage 1: Fast passes
            for (int i = 0; i < variants.Count; i++)
            {
                if (!variants[i].IsFastPass) continue;
                ct.ThrowIfCancellationRequested();
                await RecognizeNativeVariantAsync(variants[i].PngBytes, i, engines, allCandidates).ConfigureAwait(false);
            }

            var fastBest = SelectBestCandidate(allCandidates, preferredLanguageTag: autoDetectLanguage ? null : preferredLanguageTag);
            if (IsHighConfidenceSelection(fastBest, allCandidates))
                return new OcrResult(true, fastBest.CleanText, fastBest.ResolvedLanguageTag);

            // Stage 2: Deep passes (colored-on-colored, textured/gradient, giant/tiny fonts, polarity flip)
            for (int i = 0; i < variants.Count; i++)
            {
                if (variants[i].IsFastPass) continue;
                ct.ThrowIfCancellationRequested();
                await RecognizeNativeVariantAsync(variants[i].PngBytes, i, engines, allCandidates).ConfigureAwait(false);
            }

            var finalBest = SelectBestCandidate(allCandidates, preferredLanguageTag: autoDetectLanguage ? null : preferredLanguageTag);
            if (string.IsNullOrWhiteSpace(finalBest.CleanText))
                return new OcrResult(false, string.Empty, engines[0].RecognizerLanguage.LanguageTag, LocalizationService.Pick("No text recognized in the selected region.", "Текст в выделенной области не распознан."));

            return new OcrResult(true, finalBest.CleanText, finalBest.ResolvedLanguageTag);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Error("Native WinRT OCR failed.", ex);
            return new OcrResult(false, string.Empty, "und", ex.Message);
        }
    }

    private static List<OcrEngine> BuildNativeEngines(bool autoDetectLanguage, string preferredLanguageTag)
    {
        var engines = new List<OcrEngine>();
        if (!autoDetectLanguage && !string.IsNullOrWhiteSpace(preferredLanguageTag))
        {
            try
            {
                var custom = OcrEngine.TryCreateFromLanguage(new Language(preferredLanguageTag));
                if (custom is not null) engines.Add(custom);
            }
            catch { }
        }

        foreach (var lang in OcrEngine.AvailableRecognizerLanguages
                     .OrderByDescending(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                     .ThenByDescending(l => l.LanguageTag.StartsWith("ru", StringComparison.OrdinalIgnoreCase)))
        {
            var eng = OcrEngine.TryCreateFromLanguage(lang);
            if (eng is not null && engines.All(e => !string.Equals(e.RecognizerLanguage.LanguageTag, eng.RecognizerLanguage.LanguageTag, StringComparison.OrdinalIgnoreCase)))
                engines.Add(eng);
        }

        if (engines.Count == 0)
        {
            var fallback = OcrEngine.TryCreateFromUserProfileLanguages();
            if (fallback is not null) engines.Add(fallback);
        }

        return engines;
    }

    private static async Task RecognizeNativeVariantAsync(
        byte[] pngBytes,
        int variantIdx,
        List<OcrEngine> engines,
        List<OcrCandidate> output)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(pngBytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        stream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync();

        foreach (var engine in engines)
        {
            var res = await engine.RecognizeAsync(bitmap);
            string text = FormatOcrLines(res);
            if (!string.IsNullOrWhiteSpace(text))
                output.Add(new OcrCandidate(variantIdx, engine.RecognizerLanguage.LanguageTag, text));
        }
    }

    private static string FormatOcrLines(Windows.Media.Ocr.OcrResult result)
    {
        if (result.Lines is null || result.Lines.Count == 0)
            return result.Text?.Trim() ?? string.Empty;

        return string.Join(Environment.NewLine, result.Lines.Select(l => l.Text.Trim()).Where(t => !string.IsNullOrWhiteSpace(t)));
    }
#else
    private async Task<OcrResult> RecognizeViaResidentBridgeAsync(
        IReadOnlyList<OcrPreparedVariant> variants,
        string langArg,
        CancellationToken ct)
    {
        try
        {
            var fastIndices = new List<int>();
            var deepIndices = new List<int>();
            for (int i = 0; i < variants.Count; i++)
            {
                if (variants[i].IsFastPass) fastIndices.Add(i);
                else deepIndices.Add(i);
            }

            string? preferredLang = string.Equals(langArg, "auto", StringComparison.OrdinalIgnoreCase) ? null : langArg;
            var allCandidates = new List<OcrCandidate>();

            // Stage 1: Fast Pass (norm-contrast + rgb-clean) — typically completes in 50-120 ms on resident worker
            var (fastOk, fastErr, fastCandidates) = await RunWorkerBatchAsync(variants, fastIndices, langArg, ct).ConfigureAwait(false);
            if (!fastOk && !string.IsNullOrWhiteSpace(fastErr))
                return new OcrResult(false, string.Empty, "und", fastErr);

            allCandidates.AddRange(fastCandidates);
            var fastBest = SelectBestCandidate(allCandidates, preferredLang);
            if (IsHighConfidenceSelection(fastBest, allCandidates))
            {
                return new OcrResult(true, fastBest.CleanText, fastBest.ResolvedLanguageTag);
            }

            // Stage 2: Deep Pass (chroma-dist + adaptive-bin + giant/tiny/invert specialist)
            if (deepIndices.Count > 0)
            {
                var (_, _, deepCandidates) = await RunWorkerBatchAsync(variants, deepIndices, langArg, ct).ConfigureAwait(false);
                allCandidates.AddRange(deepCandidates);
            }

            var finalBest = SelectBestCandidate(allCandidates, preferredLang);
            if (string.IsNullOrWhiteSpace(finalBest.CleanText))
            {
                return new OcrResult(
                    false,
                    string.Empty,
                    string.IsNullOrWhiteSpace(finalBest.ResolvedLanguageTag) ? "und" : finalBest.ResolvedLanguageTag,
                    LocalizationService.Pick(
                        "No text detected in the selected region. Try selecting a slightly wider area.",
                        "Текст в выделенной области не обнаружен. Попробуйте обвести текст чуть шире."));
            }

            return new OcrResult(true, finalBest.CleanText, finalBest.ResolvedLanguageTag);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Error("Resident WinRT OCR bridge failed.", ex);
            return new OcrResult(false, string.Empty, "und", ex.Message);
        }
    }

    private async Task<(bool Ok, string Error, List<OcrCandidate> Candidates)> RunWorkerBatchAsync(
        IReadOnlyList<OcrPreparedVariant> allVariants,
        IReadOnlyList<int> variantIndices,
        string langArg,
        CancellationToken ct)
    {
        if (variantIndices.Count == 0)
            return (true, string.Empty, new List<OcrCandidate>());

        await _workerLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (_disposed)
                    return (false, LocalizationService.Pick("OCR service stopped.", "Сервис OCR остановлен."), new List<OcrCandidate>());

                var proc = EnsureWorkerRunning();
                if (proc is null)
                    return (false, LocalizationService.Pick("Failed to launch text recognition process.", "Не удалось запустить процесс распознавания текста."), new List<OcrCandidate>());

                try
                {
                    var payload = new
                    {
                        lang = langArg,
                        images = variantIndices.Select(idx => new
                        {
                            v = idx,
                            b64 = Convert.ToBase64String(allVariants[idx].PngBytes)
                        }).ToArray()
                    };

                    string reqJson = JsonSerializer.Serialize(payload);
                    await proc.StandardInput.WriteLineAsync(reqJson.AsMemory(), ct).ConfigureAwait(false);
                    await proc.StandardInput.FlushAsync(ct).ConfigureAwait(false);

                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(14));

                    string? respLine = await proc.StandardOutput.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(respLine))
                    {
                        LogService.Warn($"OCR worker returned EOF/empty line on attempt {attempt + 1}; restarting worker.");
                        RestartWorkerUnlocked();
                        continue;
                    }

                    using var doc = JsonDocument.Parse(respLine.Trim());
                    var root = doc.RootElement;
                    bool ok = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
                    string err = root.TryGetProperty("err", out var errEl) ? (errEl.GetString() ?? string.Empty) : string.Empty;
                    if (string.Equals(err, "NO_OCR_LANG_PACKS", StringComparison.Ordinal))
                        err = LocalizationService.Pick("No Windows OCR language packs were found on this system.", "В системе не найдены языковые пакеты Windows OCR.");

                    var candidates = new List<OcrCandidate>();
                    if (root.TryGetProperty("candidates", out var arrEl) && arrEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in arrEl.EnumerateArray())
                        {
                            int vIdx = item.TryGetProperty("v", out var vEl) ? vEl.GetInt32() : 0;
                            string lang = item.TryGetProperty("lang", out var lEl) ? (lEl.GetString() ?? "und") : "und";
                            string text = item.TryGetProperty("text", out var tEl) ? (tEl.GetString() ?? string.Empty) : string.Empty;
                            if (!string.IsNullOrWhiteSpace(text))
                                candidates.Add(new OcrCandidate(vIdx, lang, text));
                        }
                    }

                    return (ok, err, candidates);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    LogService.Warn($"OCR worker timed out on attempt {attempt + 1}; restarting worker.");
                    RestartWorkerUnlocked();
                }
                catch (Exception ex) when (attempt == 0)
                {
                    LogService.Warn($"OCR worker communication error ({ex.Message}); restarting worker.");
                    RestartWorkerUnlocked();
                }
            }

            return (false, LocalizationService.Pick("OCR module timed out.", "Модуль OCR не ответил вовремя."), new List<OcrCandidate>());
        }
        finally
        {
            _workerLock.Release();
        }
    }

    private Process? EnsureWorkerRunning()
    {
        if (_workerProcess is not null && !_workerProcess.HasExited)
            return _workerProcess;

        RestartWorkerUnlocked();
        return _workerProcess;
    }

    private void RestartWorkerUnlocked()
    {
        if (_workerProcess is not null)
        {
            try
            {
                if (!_workerProcess.HasExited)
                    _workerProcess.Kill(entireProcessTree: true);
            }
            catch { }
            try { _workerProcess.Dispose(); } catch { }
            _workerProcess = null;
        }

        if (_disposed)
            return;

        // Persistent PowerShell 5.1 WinRT OCR worker: initializes OcrEngine instances once in memory
        // and serves JSON-line batch recognition requests with minimal latency (~40-80ms per image).
        const string script = @"
$ErrorActionPreference = 'Stop'
[Console]::InputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType=WindowsRuntime]
$null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Foundation, ContentType=WindowsRuntime]
$null = [Windows.Storage.Streams.InMemoryRandomAccessStream, Windows.Foundation, ContentType=WindowsRuntime]
$null = [Windows.Storage.Streams.DataWriter, Windows.Foundation, ContentType=WindowsRuntime]
$null = [Windows.Globalization.Language, Windows.Foundation, ContentType=WindowsRuntime]

$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
function Await($WinRtTask, $ResultType) {
    $netTask = $asTaskGeneric.MakeGenericMethod($ResultType).Invoke($null, @($WinRtTask))
    $netTask.Wait(-1) | Out-Null
    return $netTask.Result
}

$engineMap = @{}
$orderedEngines = New-Object System.Collections.Generic.List[Windows.Media.Ocr.OcrEngine]
$avail = [Windows.Media.Ocr.OcrEngine]::AvailableRecognizerLanguages

foreach ($l in $avail) {
    if ($l.LanguageTag -like 'en*') {
        $e = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($l)
        if ($null -ne $e) {
            $tag = $e.RecognizerLanguage.LanguageTag
            if (-not $engineMap.ContainsKey($tag)) {
                $engineMap[$tag] = $e
                $orderedEngines.Add($e)
            }
        }
    }
}
foreach ($l in $avail) {
    if ($l.LanguageTag -like 'ru*') {
        $e = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($l)
        if ($null -ne $e) {
            $tag = $e.RecognizerLanguage.LanguageTag
            if (-not $engineMap.ContainsKey($tag)) {
                $engineMap[$tag] = $e
                $orderedEngines.Add($e)
            }
        }
    }
}
foreach ($l in $avail) {
    if ($l.LanguageTag -notlike 'en*' -and $l.LanguageTag -notlike 'ru*') {
        $e = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($l)
        if ($null -ne $e) {
            $tag = $e.RecognizerLanguage.LanguageTag
            if (-not $engineMap.ContainsKey($tag)) {
                $engineMap[$tag] = $e
                $orderedEngines.Add($e)
            }
        }
    }
}
if ($orderedEngines.Count -eq 0) {
    $prof = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
    if ($null -ne $prof) {
        $tag = $prof.RecognizerLanguage.LanguageTag
        $engineMap[$tag] = $prof
        $orderedEngines.Add($prof)
    }
}

while ($true) {
    $line = [Console]::In.ReadLine()
    if ($null -eq $line) { break }
    if ([string]::IsNullOrWhiteSpace($line)) { continue }

    if ($orderedEngines.Count -eq 0) {
        Write-Output (@{ ok = $false; err = 'NO_OCR_LANG_PACKS'; candidates = @() } | ConvertTo-Json -Compress)
        continue
    }

    try {
        $req = $line | ConvertFrom-Json
        $langTag = [string]$req.lang
        $enginesToRun = New-Object System.Collections.Generic.List[Windows.Media.Ocr.OcrEngine]

        if (-not [string]::IsNullOrWhiteSpace($langTag) -and $langTag -ne 'auto') {
            $matched = $null
            foreach ($k in $engineMap.Keys) {
                if ($k.Equals($langTag, [System.StringComparison]::OrdinalIgnoreCase) -or $k.StartsWith($langTag + '-', [System.StringComparison]::OrdinalIgnoreCase)) {
                    $matched = $engineMap[$k]
                    break
                }
            }
            if ($null -eq $matched) {
                try {
                    $custom = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage((New-Object Windows.Globalization.Language($langTag)))
                    if ($null -ne $custom) {
                        $matched = $custom
                        $engineMap[$custom.RecognizerLanguage.LanguageTag] = $custom
                    }
                } catch {}
            }
            if ($null -ne $matched) { $enginesToRun.Add($matched) }
        }

        foreach ($eng in $orderedEngines) {
            if (-not $enginesToRun.Contains($eng)) {
                $enginesToRun.Add($eng)
            }
        }

        $results = New-Object System.Collections.Generic.List[object]
        foreach ($img in @($req.images)) {
            $vIdx = [int]$img.v
            $bytes = [Convert]::FromBase64String([string]$img.b64)

            $memStream = $null
            $bitmap = $null
            try {
                $memStream = New-Object Windows.Storage.Streams.InMemoryRandomAccessStream
                $writer = New-Object Windows.Storage.Streams.DataWriter($memStream)
                $writer.WriteBytes($bytes)
                $null = Await ($writer.StoreAsync()) ([uint32])
                $null = Await ($writer.FlushAsync()) ([bool])
                $writer.DetachStream() | Out-Null
                $writer.Dispose()
                $memStream.Seek(0)

                $decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($memStream)) ([Windows.Graphics.Imaging.BitmapDecoder])
                $bitmap = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])

                foreach ($eng in $enginesToRun) {
                    $ocr = Await ($eng.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
                    $lines = New-Object System.Collections.Generic.List[string]
                    foreach ($ocrLine in $ocr.Lines) {
                        if (-not [string]::IsNullOrWhiteSpace($ocrLine.Text)) {
                            $lines.Add($ocrLine.Text.Trim())
                        }
                    }
                    $txt = ($lines -join ""`n"").Trim()
                    if ([string]::IsNullOrWhiteSpace($txt)) { $txt = ([string]$ocr.Text).Trim() }
                    if (-not [string]::IsNullOrWhiteSpace($txt)) {
                        $results.Add(@{
                            v    = $vIdx
                            lang = $eng.RecognizerLanguage.LanguageTag
                            text = $txt
                        })
                    }
                }
            } finally {
                if ($null -ne $bitmap) { try { $bitmap.Dispose() } catch {} }
                if ($null -ne $memStream) { try { $memStream.Dispose() } catch {} }
            }
        }

        Write-Output (@{ ok = $true; err = ''; candidates = $results.ToArray() } | ConvertTo-Json -Depth 4 -Compress)
    } catch {
        Write-Output (@{ ok = $false; err = $_.Exception.Message; candidates = @() } | ConvertTo-Json -Compress)
    }
}
";

        string encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encodedCommand}",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _workerProcess = Process.Start(psi);
    }
#endif

    // =========================================================================
    // Pure C# Linguistic / Script-Aware OCR Candidate Scorer & Post-Processor
    // =========================================================================

    public readonly record struct SelectedOcrCandidate(
        string CleanText,
        string ResolvedLanguageTag,
        double Score,
        int AgreeingVariantCount,
        double NoiseRatio);

    public static SelectedOcrCandidate SelectBestCandidate(
        IReadOnlyList<OcrCandidate> candidates,
        string? preferredLanguageTag = null)
    {
        if (candidates is null || candidates.Count == 0)
            return new SelectedOcrCandidate(string.Empty, "und", 0.0, 0, 1.0);

        // Clean each candidate and count cross-variant agreement on normalized text
        var cleaned = new List<(OcrCandidate Raw, string Clean, string NormKey, double BaseScore, double NoiseRatio)>(candidates.Count);
        var variantAgreement = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in candidates)
        {
            string clean = CleanOcrText(c.Text);
            if (string.IsNullOrWhiteSpace(clean))
                continue;

            string normKey = NormalizeForConsensus(clean);
            if (!variantAgreement.TryGetValue(normKey, out var set))
            {
                set = new HashSet<int>();
                variantAgreement[normKey] = set;
            }
            set.Add(c.VariantIndex);

            var (score, noise) = ScoreCandidate(clean, c.EngineLanguageTag, preferredLanguageTag);
            cleaned.Add((c, clean, normKey, score, noise));
        }

        if (cleaned.Count == 0)
            return new SelectedOcrCandidate(string.Empty, "und", 0.0, 0, 1.0);

        // Superstring completeness promotion: if a clean candidate B strictly contains another candidate A
        // (e.g. "me in 15 yrs..." contains clipped "e in 15 yrs..."), B inherits A's supporting variants
        // so clipped partial readings can NEVER outvote a complete reading!
        var distinctNormKeys = variantAgreement.Keys.ToList();
        foreach (string longerKey in distinctNormKeys)
        {
            if (longerKey.Length < 4) continue;
            var longerSet = variantAgreement[longerKey];
            foreach (string shorterKey in distinctNormKeys)
            {
                if (shorterKey.Length >= longerKey.Length || shorterKey.Length < 3) continue;
                if (longerKey.Contains(shorterKey, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (int v in variantAgreement[shorterKey])
                        longerSet.Add(v);
                }
            }
        }

        SelectedOcrCandidate best = new(string.Empty, "und", double.NegativeInfinity, 0, 1.0);

        foreach (var item in cleaned)
        {
            int agreeingVariants = variantAgreement.TryGetValue(item.NormKey, out var set) ? set.Count : 1;
            // Consensus across multiple independent image preprocessing variants strongly confirms true text
            double consensusMultiplier = 1.0 + Math.Min(3, agreeingVariants - 1) * 0.16;
            // Windows Cyrillic OCR packs also output Latin for English text and only output clean Cyrillic
            // when genuine Cyrillic glyphs are present — boost clean Cyrillic detections so en-US lookalikes never win.
            bool isCleanCyrillic = IsCyrillicLanguage(item.Raw.EngineLanguageTag) &&
                                   item.NoiseRatio <= 0.12 &&
                                   item.Clean.Count(IsCyrillicChar) >= 3 &&
                                   item.Clean.Count(IsCyrillicChar) >= item.Clean.Count(IsLatinChar) * 2;
            double scriptConfidence = isCleanCyrillic ? 1.18 : 1.0;
            // Slight preference for fast-pass crisp variants (0, 1, 2) on exact ties
            double variantBonus = item.Raw.VariantIndex <= 2 ? 0.35 : 0.0;
            double totalScore = item.BaseScore * consensusMultiplier * scriptConfidence + variantBonus;

            if (totalScore > best.Score)
            {
                string resolvedLang = InferLanguageTagFromText(item.Clean, item.Raw.EngineLanguageTag);
                best = new SelectedOcrCandidate(item.Clean, resolvedLang, totalScore, agreeingVariants, item.NoiseRatio);
            }
        }

        return best;
    }

    internal static bool IsHighConfidenceSelection(SelectedOcrCandidate best, IReadOnlyList<OcrCandidate> candidates)
    {
        if (string.IsNullOrWhiteSpace(best.CleanText))
            return false;

        int alnumCount = best.CleanText.Count(char.IsLetterOrDigit);
        if (alnumCount < 3)
            return false;

        if (best.NoiseRatio > 0.14)
            return false;

        // If both fast variants (norm-contrast & rgb-clean) agree on clean text -> instant early exit!
        if (best.AgreeingVariantCount >= 2 && best.Score >= 12.0)
            return true;

        // Or if a single fast pass produced a long, very clean multi-word paragraph with low noise
        if (alnumCount >= 18 && best.NoiseRatio <= 0.05 && best.Score >= alnumCount * 2.6)
            return true;

        return false;
    }

    public static (double Score, double NoiseRatio) ScoreCandidate(
        string text,
        string engineLangTag,
        string? preferredLanguageTag = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (0.0, 1.0);

        bool isCyrillicEngine = IsCyrillicLanguage(engineLangTag);
        bool isLatinEngine = IsLatinLanguage(engineLangTag);

        double score = 0.0;
        int totalNonSpace = 0;
        int noiseChars = 0;

        var tokens = Regex.Split(text.Trim(), @"\s+");
        foreach (string rawToken in tokens)
        {
            if (string.IsNullOrWhiteSpace(rawToken))
                continue;

            int tokLatin = 0, tokCyr = 0, tokOtherLetter = 0, tokDigits = 0, tokGarbage = 0, tokPunct = 0;
            int caseTransitions = 0;
            int prevCase = 0; // 1 = upper, 2 = lower
            int digitLetterSwitches = 0;
            int prevKind = 0; // 1 = letter, 2 = digit
            bool hasVowel = false;

            foreach (char ch in rawToken)
            {
                totalNonSpace++;

                if (IsCyrillicChar(ch))
                {
                    tokCyr++;
                    if (IsCyrillicVowel(ch)) hasVowel = true;
                    UpdateCase(ch, ref prevCase, ref caseTransitions);
                    if (prevKind == 2) digitLetterSwitches++;
                    prevKind = 1;
                }
                else if (IsLatinChar(ch))
                {
                    tokLatin++;
                    if (IsLatinVowel(ch)) hasVowel = true;
                    UpdateCase(ch, ref prevCase, ref caseTransitions);
                    if (prevKind == 2) digitLetterSwitches++;
                    prevKind = 1;
                }
                else if (char.IsLetter(ch))
                {
                    tokOtherLetter++;
                    hasVowel = true; // CJK/Arabic/etc.
                    if (prevKind == 2) digitLetterSwitches++;
                    prevKind = 1;
                }
                else if (char.IsDigit(ch))
                {
                    tokDigits++;
                    if (prevKind == 1) digitLetterSwitches++;
                    prevKind = 2;
                    prevCase = 0;
                }
                else if (IsNormalPunctuation(ch))
                {
                    tokPunct++;
                    prevKind = 0;
                    prevCase = 0;
                }
                else
                {
                    tokGarbage++;
                    noiseChars++;
                    prevKind = 0;
                    prevCase = 0;
                }
            }

            int tokLetters = tokLatin + tokCyr + tokOtherLetter;

            // 1. Script-weighted character score
            if (isCyrillicEngine)
            {
                score += tokCyr * 3.2 + tokLatin * 2.3 + tokOtherLetter * 2.5 + tokDigits * 1.6;
            }
            else if (isLatinEngine)
            {
                score += tokLatin * 3.2 + tokCyr * 1.4 + tokOtherLetter * 2.5 + tokDigits * 1.6;
            }
            else
            {
                score += tokLetters * 3.0 + tokDigits * 1.6;
            }

            // 2. Penalize mixed Latin + Cyrillic inside the SAME word (classic OCR cross-script hallucination)
            if (tokLatin > 0 && tokCyr > 0)
            {
                score -= 8.5;
                noiseChars += Math.Min(tokLatin, tokCyr);
            }

            // 3. Penalize bizarre internal casing switches inside a word (catches en-US reading Russian "Привет" as "npuBeT"!)
            // Normal words: "hello" (0), "Hello" (1), "HELLO" (0). "npuBeT" has 3 switches (lower->upper->lower->upper)!
            if (tokLetters >= 3 && caseTransitions >= 2)
            {
                // Allow standard camelCase/PascalCase if length >= 5 and only 2 transitions (e.g. "GetText", "iPhone")
                bool looksLikeCamelCase = tokLatin == tokLetters && caseTransitions == 2 && tokLetters >= 5 && char.IsUpper(rawToken[0]);
                if (!looksLikeCamelCase)
                {
                    score -= caseTransitions * 4.5;
                    noiseChars += caseTransitions;
                }
            }
            else if (tokLetters >= 3 && caseTransitions == 1 && char.IsLower(rawToken[0]))
            {
                // e.g. "mUp" or "cTp" (lowercase start then uppercase mid-word in short words)
                bool isKnownPrefix = rawToken.StartsWith("iP", StringComparison.Ordinal) || rawToken.StartsWith("eB", StringComparison.Ordinal) || rawToken.StartsWith("macOS", StringComparison.Ordinal);
                if (!isKnownPrefix && tokLetters <= 5)
                {
                    score -= 3.5;
                    noiseChars++;
                }
            }

            // 4. Penalize digit-inside-word OCR substitutions like "He11o", "5ettings", "B00k"
            // Allow normal identifiers like "v2", "3D", "MP3", "x64", "Win11", "2.4GHz" (1 transition)
            if (tokLetters >= 2 && tokDigits >= 1 && digitLetterSwitches >= 2)
            {
                score -= digitLetterSwitches * 3.2;
                noiseChars += tokDigits;
            }

            // 5. Penalize 4+ letter pure-consonant gibberish (e.g. "npvbt", "zxcv")
            if (tokLetters >= 4 && !hasVowel && tokOtherLetter == 0)
            {
                // Keep all-caps acronyms (HTTPS, Ctrl, HTML) lightly tolerated; penalize mixed/lower soup heavily
                bool allUpperAcronym = rawToken.Where(char.IsLetter).All(char.IsUpper) && tokLetters <= 5;
                if (!allUpperAcronym)
                {
                    score -= 4.5;
                    noiseChars += 2;
                }
            }

            // 6. Phonotactic bigram naturalness & impossible-cluster check
            // Distinguishes real English ("Hello World", "Settings") from en-US hallucinating Cyrillic ("tlpueem mup", "npuBeT")!
            if (tokLetters >= 3)
            {
                string lowerLetters = ExtractLettersLower(rawToken);
                if (tokLatin == tokLetters)
                {
                    if (ContainsImpossibleLatinCluster(lowerLetters))
                    {
                        score -= 7.5;
                        noiseChars += 2;
                    }
                    int naturalHits = CountNaturalBigrams(lowerLetters, CommonLatinBigrams);
                    double ratio = lowerLetters.Length > 1 ? (double)naturalHits / (lowerLetters.Length - 1) : 0.0;
                    score += naturalHits * 0.9;
                    if (lowerLetters.Length >= 5 && ratio < 0.15)
                    {
                        score -= 4.5;
                        noiseChars++;
                    }
                }
                else if (tokCyr == tokLetters)
                {
                    int naturalHits = CountNaturalBigrams(lowerLetters, CommonCyrillicBigrams);
                    score += naturalHits * 1.15;
                }
            }

            // 7. Clean word bonus vs stray single-char garbage / clipped-letter penalty
            if (tokLetters >= 2 && tokGarbage == 0 && !(tokLatin > 0 && tokCyr > 0))
            {
                score += 1.6;
            }
            else if (tokLetters == 1 && tokDigits == 0)
            {
                // Real 1-letter words ("a", "I", "и", "в", "с", "к", "у", "о", "я", "а") are fine;
                // stray clipped fragments ("e", "m", "t", "r") get a slight penalty so complete words win!
                char onlyLetter = char.ToLowerInvariant(rawToken.First(char.IsLetter));
                bool isValidSingleLetterWord = onlyLetter is 'a' or 'i' or 'а' or 'в' or 'и' or 'к' or 'о' or 'с' or 'у' or 'я';
                if (!isValidSingleLetterWord)
                    score -= 2.2;
            }
            else if (tokLetters == 0 && tokDigits == 0 && tokPunct + tokGarbage <= 2)
            {
                score -= 2.0;
            }

            // 8. Garbage symbol penalty
            score -= tokGarbage * 3.0;
        }

        // Mild bonus if engine matches user's explicit preferred language (when manual mode is set)
        if (!string.IsNullOrWhiteSpace(preferredLanguageTag) &&
            !string.Equals(preferredLanguageTag, "auto", StringComparison.OrdinalIgnoreCase) &&
            engineLangTag.StartsWith(preferredLanguageTag.Split('-')[0], StringComparison.OrdinalIgnoreCase))
        {
            score *= 1.08;
        }

        double noiseRatio = totalNonSpace > 0 ? Math.Clamp((double)noiseChars / totalNonSpace, 0.0, 1.0) : 1.0;
        return (Math.Max(0.0, score), noiseRatio);
    }

    public static string CleanOcrText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var lines = raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var kept = new List<string>(lines.Length);

        foreach (string line in lines)
        {
            // Collapse weird multiple spaces while keeping readable structure
            string trimmed = Regex.Replace(line.Trim(), @"[ \t]{2,}", " ");
            if (string.IsNullOrEmpty(trimmed))
                continue;

            // Drop pure border-noise lines (e.g., "|", "_", "—", "~", ".") when there's zero letter/digit content
            if (!trimmed.Any(char.IsLetterOrDigit) && trimmed.Length <= 3)
                continue;

            // Strip isolated leading/trailing vertical bar noise from window borders (e.g. "| Hello world |")
            trimmed = Regex.Replace(trimmed, @"^[\|¦_~]+\s+", string.Empty);
            trimmed = Regex.Replace(trimmed, @"\s+[\|¦_~]+$", string.Empty);
            trimmed = NormalizeDegreeSymbols(trimmed);
            trimmed = trimmed.Trim();

            if (!string.IsNullOrEmpty(trimmed))
                kept.Add(trimmed);
        }

        return string.Join(Environment.NewLine, kept).Trim();
    }

    /// <summary>
    /// Repairs frequent Windows OCR confusions of the degree symbol (°) in temperatures
    /// such as "25 oC", "25ºC", "25°С", "+240C", "-150C", "00C", or "температура 220C".
    /// </summary>
    public static string NormalizeDegreeSymbols(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return line;

        string s = line;

        // 1. Direct degree-like glyph variants + Cyrillic С next to °
        s = Regex.Replace(s, @"(?<=\d)\s*[°º˚]\s*([CСcс])(?![A-Za-zА-Яа-я0-9])", "°C");
        s = Regex.Replace(s, @"(?<=\d)\s*[°º˚]\s*([Ff])(?![A-Za-zА-Яа-я0-9])", "°F");

        // 2. Letter 'o'/'O'/'о'/'О' misread for '°' right before C/F (e.g. "25 oC", "-5oC", "72oF")
        s = Regex.Replace(s, @"(?<![A-Za-zА-Яа-я0-9])([+-]?\d{1,3})\s*[oOоО]\s*([CС])(?![A-Za-zА-Яа-я0-9])", "$1°C");
        s = Regex.Replace(s, @"(?<![A-Za-zА-Яа-я0-9])([+-]?\d{1,3})\s*[oOоО]\s*F(?![A-Za-zА-Яа-я0-9])", "$1°F");

        // 3. Explicit signed temperatures where '°' was read as '0' (e.g. "+240C" -> "+24°C", "-50C" -> "-5°C", "-15 0C" -> "-15°C")
        s = Regex.Replace(s, @"(?<![A-Za-zА-Яа-я0-9])([+-]\d{1,2})\s*0\s*([CС])(?![A-Za-zА-Яа-я0-9])", "$1°C");
        s = Regex.Replace(s, @"(?<![A-Za-zА-Яа-я0-9])([+-]\d{1,2})\s*0\s*F(?![A-Za-zА-Яа-я0-9])", "$1°F");

        // 4. "00C" / "0 0C" is unambiguously "0°C"
        s = Regex.Replace(s, @"(?<![A-Za-zА-Яа-я0-9])0\s*0\s*([CС])(?![A-Za-zА-Яа-я0-9])", "0°C");

        // 5. Unsigned "250C" / "180C" when weather/temperature context is present in the line
        bool hasTempContext = Regex.IsMatch(
            s,
            @"(темп|погод|градус|ощущ|ветер|влажн|ночью|днём|днем|утром|вечером|temp|weather|feels|wind|humidity|°|[tT]\s*=)",
            RegexOptions.IgnoreCase);
        if (hasTempContext)
        {
            s = Regex.Replace(s, @"(?<![A-Za-zА-Яа-я0-9])([1-5]?\d)\s*0\s*([CС])(?![A-Za-zА-Яа-я0-9])", "$1°C");
        }

        return s;
    }

    public static string InferLanguageTagFromText(string text, string fallbackEngineTag)
    {
        int cyr = 0, lat = 0, cjk = 0, arab = 0;
        foreach (char ch in text)
        {
            if (IsCyrillicChar(ch)) cyr++;
            else if (IsLatinChar(ch)) lat++;
            else if (ch >= '\u4E00' && ch <= '\u9FFF') cjk++;
            else if (ch >= '\u0600' && ch <= '\u06FF') arab++;
        }

        if (cyr > lat && cyr >= 2)
            return IsCyrillicLanguage(fallbackEngineTag) ? fallbackEngineTag : "ru";
        if (lat > cyr && lat >= 2)
            return IsLatinLanguage(fallbackEngineTag) ? fallbackEngineTag : "en";
        if (cjk >= 2) return "zh";
        if (arab >= 2) return "ar";

        return string.IsNullOrWhiteSpace(fallbackEngineTag) ? "und" : fallbackEngineTag;
    }

    private static readonly HashSet<string> CommonLatinBigrams = new(StringComparer.Ordinal)
    {
        "th", "he", "in", "er", "an", "re", "on", "at", "en", "nd", "ti", "es", "or", "te", "of", "ed",
        "is", "it", "al", "ar", "st", "to", "nt", "ng", "se", "ha", "as", "ou", "io", "le", "ve", "co",
        "me", "de", "hi", "ri", "ro", "ic", "ne", "ea", "ra", "ce", "li", "ch", "ll", "be", "ma", "si",
        "om", "ur", "ca", "el", "ta", "la", "ns", "di", "fo", "ho", "pe", "ec", "pr", "no", "ct", "us",
        "ac", "ot", "il", "tr", "ly", "nc", "et", "ut", "ss", "so", "rs", "un", "lo", "wa", "ge", "ie",
        "wh", "ee", "wi", "em", "ad", "ol", "rt", "po", "we", "na", "ul", "ni", "ts", "mo", "ow", "pa",
        "im", "mi", "ai", "sh", "ir", "su", "id", "os", "iv", "ia", "am", "fi", "ci", "vi", "pl", "ig",
        "tu", "ev", "ld", "ry", "mp", "fe", "bl", "ab", "gh", "ty", "op", "wo", "sa", "ay", "ex", "fr",
        "do", "wn", "oa", "up", "pd", "da", "rn", "mu", "sy", "ys", "dy", "av", "gr", "cl", "br", "cr",
        "dr", "sp", "sc", "sk", "sl", "sm", "sn", "sw", "tw", "qu", "ck", "tt", "pp", "rr", "nn", "mm"
    };

    private static readonly HashSet<string> CommonCyrillicBigrams = new(StringComparer.Ordinal)
    {
        "ст", "но", "ен", "то", "на", "ов", "ни", "ра", "во", "ко", "ро", "по", "пр", "ет", "ос", "от",
        "не", "ер", "ре", "го", "ал", "ор", "ол", "ел", "ин", "ли", "ит", "ка", "та", "ан", "ом", "ем",
        "ри", "ве", "ив", "ми", "ир", "де", "ла", "ак", "ск", "ть", "ся", "ле", "об", "ва", "да", "за",
        "он", "од", "ес", "ис", "ат", "ас", "те", "ти", "ой", "ей", "ай", "ий", "ый", "ое", "ая", "ые",
        "ие", "ого", "его", "им", "ым", "их", "ых", "ам", "ям", "ах", "ях", "ми", "ом", "ем", "ой", "ей",
        "при", "пере", "про", "под", "раз", "вы", "вс", "гд", "зд", "кт", "чт", "шт", "ще", "щи", "жи",
        "ши", "ча", "ща", "чу", "щу", "мо", "со", "до", "бо", "ло", "ма", "па", "ба", "са", "га", "жа",
        "ша", "ча", "ме", "пе", "бе", "се", "ге", "же", "ше", "че", "ви", "пи", "би", "си", "ги", "ди",
        "ки", "ти", "фи", "хи", "ци", "чи", "ши", "щи", "ну", "ру", "су", "ту", "бу", "гу", "ду", "ку",
        "лу", "му", "пу", "фу", "ху", "чу", "шу", "ны", "ры", "сы", "ты", "бы", "вы", "ды", "мы", "пы",
        "нь", "рь", "сь", "дь", "ль", "мь", "пь", "вь", "зь", "чь", "шь", "щь", "тр", "кр", "гр", "др",
        "бр", "вр", "зр", "ср", "фр", "хр", "кл", "гл", "пл", "бл", "вл", "сл", "фл", "хл", "сп", "св",
        "см", "сн", "сл", "ср", "сх", "сч", "тв", "дв", "зв", "кв", "гв", "хв", "шв", "цв", "вн", "вк",
        "вт", "вп", "вд", "вз", "вм", "вс", "вх", "вч", "вш", "кт", "пт", "фт", "хт", "чт", "шт", "нт",
        "нд", "нк", "нг", "нц", "нч", "нш", "рн", "рт", "рд", "рк", "рг", "рм", "рп", "рб", "рв", "рз",
        "рс", "рч", "рш", "лн", "лт", "лд", "лк", "лг", "лм", "лп", "лб", "лв", "лз", "лс", "лч", "лш"
    };

    private static string ExtractLettersLower(string token)
    {
        var sb = new StringBuilder(token.Length);
        foreach (char ch in token)
        {
            if (char.IsLetter(ch))
                sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    private static int CountNaturalBigrams(string lowerLetters, HashSet<string> table)
    {
        int hits = 0;
        for (int i = 0; i < lowerLetters.Length - 1; i++)
        {
            if (table.Contains(lowerLetters.Substring(i, 2)))
                hits++;
        }
        return hits;
    }

    private static bool ContainsImpossibleLatinCluster(string lowerLetters)
    {
        // Common Latin-OCR hallucinations of Cyrillic cursive/italic glyphs (e.g. П->tl/ii/n, в->b/e, р->p, и->u, т->m)
        if (lowerLetters.StartsWith("tlp", StringComparison.Ordinal) ||
            lowerLetters.StartsWith("npu", StringComparison.Ordinal) ||
            lowerLetters.StartsWith("npo", StringComparison.Ordinal) ||
            lowerLetters.StartsWith("npe", StringComparison.Ordinal) ||
            lowerLetters.StartsWith("npa", StringComparison.Ordinal) ||
            lowerLetters.StartsWith("mnp", StringComparison.Ordinal) ||
            lowerLetters.StartsWith("cnp", StringComparison.Ordinal) ||
            lowerLetters.StartsWith("bnp", StringComparison.Ordinal) ||
            lowerLetters.StartsWith("zdr", StringComparison.Ordinal))
        {
            return true;
        }

        // Also allow 3-consonant word-start clusters only when standard English (str, spr, scr, spl, sch, thr, chr, phr, shr, squ)
        if (lowerLetters.Length >= 4 &&
            !IsLatinVowel(lowerLetters[0]) &&
            !IsLatinVowel(lowerLetters[1]) &&
            !IsLatinVowel(lowerLetters[2]))
        {
            string p3 = lowerLetters.Substring(0, 3);
            if (p3 is not ("str" or "spr" or "scr" or "spl" or "sch" or "thr" or "chr" or "phr" or "shr" or "squ" or "sph" or "scl"))
                return true;
        }

        // 5+ consecutive Latin consonants
        int consRun = 0;
        foreach (char ch in lowerLetters)
        {
            if (IsLatinVowel(ch))
                consRun = 0;
            else
            {
                consRun++;
                if (consRun >= 5)
                    return true;
            }
        }
        return false;
    }

    private static string NormalizeForConsensus(string text)
    {
        return Regex.Replace(text.Trim(), @"\s+", " ");
    }

    private static void UpdateCase(char ch, ref int prevCase, ref int transitions)
    {
        int curr = char.IsUpper(ch) ? 1 : (char.IsLower(ch) ? 2 : 0);
        if (curr != 0 && prevCase != 0 && curr != prevCase)
            transitions++;
        if (curr != 0)
            prevCase = curr;
    }

    private static bool IsCyrillicChar(char c) => c is >= '\u0400' and <= '\u04FF';
    private static bool IsLatinChar(char c) => (c is >= 'A' and <= 'Z') || (c is >= 'a' and <= 'z') || (c is >= '\u00C0' and <= '\u024F');
    private static bool IsLatinVowel(char c) => "aeiouyAEIOUYàáâäãåèéêëìíîïòóôöõøùúûüýÿÀÁÂÄÃÅÈÉÊËÌÍÎÏÒÓÔÖÕØÙÚÛÜÝ".IndexOf(c) >= 0;
    private static bool IsCyrillicVowel(char c) => "аеёиоуыэюяАЕЁИОУЫЭЮЯіїєІЇЄ".IndexOf(c) >= 0;

    private static bool IsNormalPunctuation(char c) =>
        c is '.' or ',' or '!' or '?' or ':' or ';' or '-' or '–' or '—' or '\'' or '"' or '«' or '»' or '“' or '”'
            or '(' or ')' or '[' or ']' or '/' or '%' or '+' or '=' or '@' or '#' or '$' or '€' or '₽' or '&' or '*' or '•';

    private static bool IsCyrillicLanguage(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return false;
        string code = tag.Split('-')[0].ToLowerInvariant();
        return code is "ru" or "uk" or "bg" or "be" or "sr" or "kk" or "mk";
    }

    private static bool IsLatinLanguage(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return false;
        string code = tag.Split('-')[0].ToLowerInvariant();
        return code is "en" or "de" or "fr" or "es" or "it" or "pt" or "nl" or "pl" or "tr" or "cs" or "sv" or "fi" or "da" or "no" or "ro" or "hu" or "vi" or "id";
    }

    public void Dispose()
    {
#if !WITH_WINRT
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_workerProcess is not null)
            {
                if (!_workerProcess.HasExited)
                    _workerProcess.Kill(entireProcessTree: true);
                _workerProcess.Dispose();
                _workerProcess = null;
            }
        }
        catch { }
        _workerLock.Dispose();
#endif
    }
}
