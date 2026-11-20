using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PulseIsland.Core;
using PulseIsland.Services;

namespace PulseIsland.Translation;

/// <summary>
/// Offline neural machine translation engine running MarianMT / OPUS-MT ONNX encoder-decoder models
/// with pure C# SentencePiece tokenization and native OrtApi inference strictly inside portable folders.
/// </summary>
public sealed class OnnxOfflineTranslationService : IDisposable
{
    public sealed record ModelManifest(
        string ModelId,
        string DisplayName,
        string EncoderFile,
        string DecoderFile,
        string TokenizerModelFile,
        long BosTokenId,
        long EosTokenId,
        long PadTokenId,
        int MaxLength = 128);

    private readonly SemaphoreSlim _inferenceLock = new(1, 1);
    private OrtNativeRuntime.OrtSessionHolder? _sessionHolder;
    private SentencePieceMarianTokenizer? _tokenizer;
    private string? _loadedModelDir;
    private bool _disposed;

    public bool IsModelInstalled(string modelId)
    {
        var dir = Path.Combine(PortablePaths.ModelsDirectory, modelId);
        if (!Directory.Exists(dir)) return false;

        return File.Exists(Path.Combine(dir, "encoder_model.onnx")) &&
               File.Exists(Path.Combine(dir, "decoder_model.onnx"));
    }

    public IReadOnlyList<string> GetInstalledModelIds()
    {
        if (!Directory.Exists(PortablePaths.ModelsDirectory))
            return Array.Empty<string>();

        return Directory.GetDirectories(PortablePaths.ModelsDirectory)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name) && !name!.StartsWith('_') && IsModelInstalled(name!))
            .Cast<string>()
            .ToList();
    }

    public async Task<TranslationResult> TranslateAsync(
        string text,
        string modelId,
        string sourceLangHint,
        string targetLang,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new TranslationResult(false, string.Empty, sourceLangHint, targetLang, true, LocalizationService.Pick("Empty text.", "Пустой текст."));

        string resolvedModelId = ResolveBestModelId(modelId, sourceLangHint, targetLang);
        string modelDir = Path.Combine(PortablePaths.ModelsDirectory, resolvedModelId);

        if (!IsModelInstalled(resolvedModelId))
        {
            return BuiltInLocalTranslator.Translate(text, sourceLangHint, targetLang);
        }

        await _inferenceLock.WaitAsync(ct);
        try
        {
            if (!await EnsureModelSupportFilesAsync(resolvedModelId, modelDir, ct))
            {
                return new TranslationResult(
                    false,
                    string.Empty,
                    sourceLangHint,
                    targetLang,
                    true,
                    LocalizationService.Pick(
                        "Failed to load support files (onnxruntime.dll / vocab.json / config.json) for offline model.",
                        "Не удалось загрузить вспомогательные файлы (onnxruntime.dll / vocab.json / config.json) для офлайн-модели."));
            }

            return await Task.Run(() => RunOnnxInference(text, modelDir, sourceLangHint, targetLang, ct), ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Error("ONNX offline translation failed.", ex);
            return new TranslationResult(false, string.Empty, sourceLangHint, targetLang, true, ex.Message);
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    private string ResolveBestModelId(string selectedModelId, string sourceLangHint, string targetLang)
    {
        if (!string.IsNullOrWhiteSpace(sourceLangHint) &&
            !string.Equals(sourceLangHint, "und", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(sourceLangHint, "auto", StringComparison.OrdinalIgnoreCase))
        {
            string normalizedSource = LanguageCatalog.FindOrDefault(sourceLangHint, "en").Code;
            string normalizedTarget = LanguageCatalog.FindOrDefault(targetLang, "ru").Code;
            string exactPair = $"opus-mt-{normalizedSource}-{normalizedTarget}";
            if (IsModelInstalled(exactPair))
                return exactPair;
        }

        if (!string.IsNullOrWhiteSpace(selectedModelId) && IsModelInstalled(selectedModelId))
            return selectedModelId;

        return GetInstalledModelIds().FirstOrDefault() ?? selectedModelId;
    }

    private static async Task<bool> EnsureModelSupportFilesAsync(string modelId, string modelDir, CancellationToken ct)
    {
        if (!await OrtNativeRuntime.EnsureRuntimeAvailableAsync(ct))
            return false;

        string vocabPath = Path.Combine(modelDir, "vocab.json");
        string configPath = Path.Combine(modelDir, "config.json");
        string spmPath = Path.Combine(modelDir, "source.spm");

        if (File.Exists(vocabPath) && File.Exists(configPath) && File.Exists(spmPath))
            return true;

        var catalogEntry = OfflineModelCatalogService.BuiltInCatalog.FirstOrDefault(m =>
            string.Equals(m.ModelId, modelId, StringComparison.OrdinalIgnoreCase));
        string hfRepo = $"https://huggingface.co/Xenova/{modelId}/resolve/main";

        var missing = new List<string>();
        if (!File.Exists(spmPath)) missing.Add("source.spm");
        if (!File.Exists(vocabPath)) missing.Add("vocab.json");
        if (!File.Exists(configPath)) missing.Add("config.json");

        try
        {
            using var downloader = new HuggingFaceDownloader();
            if (!File.Exists(spmPath))
            {
                string url = catalogEntry?.Files.FirstOrDefault(f => f.FileName == "source.spm")?.DownloadUrl ?? $"{hfRepo}/source.spm";
                await downloader.DownloadFileAsync(url, modelId, "source.spm", ct: ct);
            }
            if (!File.Exists(vocabPath))
            {
                string url = catalogEntry?.Files.FirstOrDefault(f => f.FileName == "vocab.json")?.DownloadUrl ?? $"{hfRepo}/vocab.json";
                await downloader.DownloadFileAsync(url, modelId, "vocab.json", ct: ct);
            }
            if (!File.Exists(configPath))
            {
                string url = catalogEntry?.Files.FirstOrDefault(f => f.FileName == "config.json")?.DownloadUrl ?? $"{hfRepo}/config.json";
                await downloader.DownloadFileAsync(url, modelId, "config.json", ct: ct);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            string missingList = string.Join(", ", missing);
            throw new InvalidOperationException(
                LocalizationService.Pick(
                    $"Model '{modelId}' is missing vocabulary files ({missingList}, ~1.7 MB), and auto-download from huggingface.co failed or was blocked: {ex.Message}. Allow network access for PulseIsland.exe and click \"Download\" next to the model in Settings — main weights won't be re-downloaded.",
                    $"Модели '{modelId}' не хватает файлов словаря ({missingList}, ~1.7 МБ), а автозагрузка с huggingface.co заблокирована или нет сети: {ex.Message}. Разрешите доступ в сеть для PulseIsland.exe и нажмите «Скачать» рядом с моделью в Настройках — основные веса заново качаться не будут."),
                ex);
        }

        return File.Exists(vocabPath) && File.Exists(spmPath);
    }

    private TranslationResult RunOnnxInference(
        string text,
        string modelDir,
        string sourceLangHint,
        string targetLang,
        CancellationToken ct)
    {
        EnsureSessionsLoaded(modelDir);

        var manifest = LoadManifest(modelDir);
        long bosId = _tokenizer!.DecoderStartTokenId;
        long eosId = _tokenizer.EosTokenId;

        // Guard against legacy broken manifests where BosTokenId == EosTokenId == 0
        if (manifest.BosTokenId != manifest.EosTokenId && File.Exists(Path.Combine(modelDir, "config.json")) == false)
        {
            bosId = manifest.BosTokenId;
            eosId = manifest.EosTokenId;
        }

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var translatedLines = new List<string>(lines.Length);

        foreach (string rawLine in lines)
        {
            ct.ThrowIfCancellationRequested();
            string trimmed = rawLine.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            var chunks = SplitIntoChunks(trimmed, maxChars: 240);
            var lineSb = new StringBuilder();

            foreach (string chunk in chunks)
            {
                ct.ThrowIfCancellationRequested();
                string pieceOut = TranslateChunk(chunk, bosId, eosId, manifest.MaxLength, ct);
                if (!string.IsNullOrWhiteSpace(pieceOut))
                {
                    if (lineSb.Length > 0) lineSb.Append(' ');
                    lineSb.Append(pieceOut);
                }
            }

            translatedLines.Add(lineSb.ToString());
        }

        string finalOutput = string.Join(Environment.NewLine, translatedLines).Trim();
        if (string.IsNullOrWhiteSpace(finalOutput))
            finalOutput = text;

        return new TranslationResult(true, finalOutput, sourceLangHint, targetLang, true);
    }

    private string TranslateChunk(string chunk, long bosId, long eosId, int maxLen, CancellationToken ct)
    {
        long[] inputIds = _tokenizer!.Encode(chunk);
        if (inputIds.Length == 0)
            return string.Empty;

        var attentionMask = new long[inputIds.Length];
        Array.Fill(attentionMask, 1L);

        int clampedMax = Math.Clamp(Math.Max(maxLen, inputIds.Length * 3 + 16), 32, 192);
        long[] outputIds = _sessionHolder!.RunGreedySeq2Seq(
            inputIds,
            attentionMask,
            bosId,
            eosId,
            clampedMax,
            ct);

        return _tokenizer.Decode(outputIds);
    }

    private void EnsureSessionsLoaded(string modelDir)
    {
        if (string.Equals(_loadedModelDir, modelDir, StringComparison.OrdinalIgnoreCase) &&
            _sessionHolder is not null &&
            _tokenizer is not null)
        {
            return;
        }

        _sessionHolder?.Dispose();
        _sessionHolder = null;
        _tokenizer = null;

        var manifest = LoadManifest(modelDir);
        string encoderPath = Path.Combine(modelDir, manifest.EncoderFile);
        string decoderPath = Path.Combine(modelDir, manifest.DecoderFile);

        _tokenizer = SentencePieceMarianTokenizer.Load(modelDir);
        _sessionHolder = new OrtNativeRuntime.OrtSessionHolder(encoderPath, decoderPath);
        _loadedModelDir = modelDir;
    }

    private static IReadOnlyList<string> SplitIntoChunks(string line, int maxChars)
    {
        if (line.Length <= maxChars)
            return [line];

        var result = new List<string>();
        var sentences = Regex.Split(line, @"(?<=[.!?])\s+");
        var current = new StringBuilder();

        foreach (string s in sentences)
        {
            if (current.Length + s.Length + 1 > maxChars && current.Length > 0)
            {
                result.Add(current.ToString().Trim());
                current.Clear();
            }

            if (s.Length > maxChars)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString().Trim());
                    current.Clear();
                }

                for (int i = 0; i < s.Length; i += maxChars)
                {
                    result.Add(s.Substring(i, Math.Min(maxChars, s.Length - i)).Trim());
                }
            }
            else
            {
                if (current.Length > 0) current.Append(' ');
                current.Append(s);
            }
        }

        if (current.Length > 0)
            result.Add(current.ToString().Trim());

        return result;
    }

    private static ModelManifest LoadManifest(string modelDir)
    {
        var manifestPath = Path.Combine(modelDir, "pulseisland_manifest.json");
        if (File.Exists(manifestPath))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<ModelManifest>(File.ReadAllText(manifestPath));
                if (parsed is not null)
                    return parsed;
            }
            catch { }
        }

        return new ModelManifest(
            ModelId: Path.GetFileName(modelDir),
            DisplayName: Path.GetFileName(modelDir),
            EncoderFile: "encoder_model.onnx",
            DecoderFile: "decoder_model.onnx",
            TokenizerModelFile: "source.spm",
            BosTokenId: 62517,
            EosTokenId: 0,
            PadTokenId: 62517);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sessionHolder?.Dispose();
        _inferenceLock.Dispose();
    }
}
