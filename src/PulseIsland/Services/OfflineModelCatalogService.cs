using System.IO;
using System.Text.Json;
using PulseIsland.Core;
using PulseIsland.Translation;

namespace PulseIsland.Services;

/// <summary>
/// Manages the catalog of offline ONNX translation models, calculates disk usage on G:,
/// and coordinates downloading/removing models strictly inside <portable-root>\models\.
/// </summary>
public sealed class OfflineModelCatalogService
{
    public static IReadOnlyList<OfflineModelCatalogEntry> BuiltInCatalog { get; } = new List<OfflineModelCatalogEntry>
    {
        new(
            ModelId: "opus-mt-en-ru",
            DisplayName: "Opus-MT: English → Russian (ONNX)",
            SourceLang: "en",
            TargetLang: "ru",
            Description: "Compact and fast neural translation model from English to Russian.",
            TotalApproximateBytes: 295L * 1024 * 1024,
            Files: new List<OfflineModelFileEntry>
            {
                new("encoder_model.onnx", "https://huggingface.co/Xenova/opus-mt-en-ru/resolve/main/onnx/encoder_model.onnx", 198L * 1024 * 1024),
                new("decoder_model.onnx", "https://huggingface.co/Xenova/opus-mt-en-ru/resolve/main/onnx/decoder_model.onnx", 95L * 1024 * 1024),
                new("source.spm", "https://huggingface.co/Xenova/opus-mt-en-ru/resolve/main/source.spm", 800L * 1024),
                new("vocab.json", "https://huggingface.co/Xenova/opus-mt-en-ru/resolve/main/vocab.json", 1700L * 1024),
                new("config.json", "https://huggingface.co/Xenova/opus-mt-en-ru/resolve/main/config.json", 4096),
                new("tokenizer_config.json", "https://huggingface.co/Xenova/opus-mt-en-ru/resolve/main/tokenizer_config.json", 4096)
            }),
        new(
            ModelId: "opus-mt-ru-en",
            DisplayName: "Opus-MT: Russian → English (ONNX)",
            SourceLang: "ru",
            TargetLang: "en",
            Description: "Local neural translation model from Russian to English.",
            TotalApproximateBytes: 295L * 1024 * 1024,
            Files: new List<OfflineModelFileEntry>
            {
                new("encoder_model.onnx", "https://huggingface.co/Xenova/opus-mt-ru-en/resolve/main/onnx/encoder_model.onnx", 198L * 1024 * 1024),
                new("decoder_model.onnx", "https://huggingface.co/Xenova/opus-mt-ru-en/resolve/main/onnx/decoder_model.onnx", 95L * 1024 * 1024),
                new("source.spm", "https://huggingface.co/Xenova/opus-mt-ru-en/resolve/main/source.spm", 800L * 1024),
                new("vocab.json", "https://huggingface.co/Xenova/opus-mt-ru-en/resolve/main/vocab.json", 1700L * 1024),
                new("config.json", "https://huggingface.co/Xenova/opus-mt-ru-en/resolve/main/config.json", 4096),
                new("tokenizer_config.json", "https://huggingface.co/Xenova/opus-mt-ru-en/resolve/main/tokenizer_config.json", 4096)
            }),
        new(
            ModelId: "opus-mt-de-ru",
            DisplayName: "Opus-MT: German → Russian (ONNX)",
            SourceLang: "de",
            TargetLang: "ru",
            Description: "Offline neural translation model from German to Russian.",
            TotalApproximateBytes: 290L * 1024 * 1024,
            Files: new List<OfflineModelFileEntry>
            {
                new("encoder_model.onnx", "https://huggingface.co/Xenova/opus-mt-de-ru/resolve/main/onnx/encoder_model.onnx", 195L * 1024 * 1024),
                new("decoder_model.onnx", "https://huggingface.co/Xenova/opus-mt-de-ru/resolve/main/onnx/decoder_model.onnx", 94L * 1024 * 1024),
                new("source.spm", "https://huggingface.co/Xenova/opus-mt-de-ru/resolve/main/source.spm", 800L * 1024),
                new("vocab.json", "https://huggingface.co/Xenova/opus-mt-de-ru/resolve/main/vocab.json", 1700L * 1024),
                new("config.json", "https://huggingface.co/Xenova/opus-mt-de-ru/resolve/main/config.json", 4096),
                new("tokenizer_config.json", "https://huggingface.co/Xenova/opus-mt-de-ru/resolve/main/tokenizer_config.json", 4096)
            }),
        new(
            ModelId: "opus-mt-mul-en",
            DisplayName: "Opus-MT: Multilingual → English (ONNX)",
            SourceLang: "auto",
            TargetLang: "en",
            Description: "Universal offline model translating multiple languages into English.",
            TotalApproximateBytes: 310L * 1024 * 1024,
            Files: new List<OfflineModelFileEntry>
            {
                new("encoder_model.onnx", "https://huggingface.co/Xenova/opus-mt-mul-en/resolve/main/onnx/encoder_model.onnx", 205L * 1024 * 1024),
                new("decoder_model.onnx", "https://huggingface.co/Xenova/opus-mt-mul-en/resolve/main/onnx/decoder_model.onnx", 103L * 1024 * 1024),
                new("source.spm", "https://huggingface.co/Xenova/opus-mt-mul-en/resolve/main/source.spm", 900L * 1024),
                new("vocab.json", "https://huggingface.co/Xenova/opus-mt-mul-en/resolve/main/vocab.json", 1800L * 1024),
                new("config.json", "https://huggingface.co/Xenova/opus-mt-mul-en/resolve/main/config.json", 4096),
                new("tokenizer_config.json", "https://huggingface.co/Xenova/opus-mt-mul-en/resolve/main/tokenizer_config.json", 4096)
            })
    };

    public IReadOnlyList<OfflineModelStatusDto> GetModelStatuses(string selectedModelId)
    {
        PortablePaths.EnsureDirectoriesExist();
        var result = new List<OfflineModelStatusDto>();

        foreach (var entry in BuiltInCatalog)
        {
            string localDir = Path.Combine(PortablePaths.ModelsDirectory, entry.ModelId);
            bool hasWeights = HasCoreModelWeights(localDir);
            bool installed = IsModelDirectoryComplete(localDir);
            long actualBytes = hasWeights ? ComputeDirectorySize(localDir) : entry.TotalApproximateBytes;

            string sizeText = installed
                ? $"{DiskSpaceSummaryDto.FormatBytes(actualBytes)} ({LocalizationService.Pick("Installed", "Установлена")})"
                : hasWeights
                    ? $"{DiskSpaceSummaryDto.FormatBytes(actualBytes)} ({LocalizationService.Pick("Vocabulary files missing — click Download", "Нужна докачка словаря — нажмите «Скачать»")})"
                    : $"~{DiskSpaceSummaryDto.FormatBytes(entry.TotalApproximateBytes)} ({LocalizationService.Pick("Not downloaded", "Не скачана")})";

            var (displayName, description) = GetLocalizedModelMeta(entry);
            result.Add(new OfflineModelStatusDto(
                ModelId: entry.ModelId,
                DisplayName: displayName,
                LanguagePair: $"{entry.SourceLang.ToUpperInvariant()} → {entry.TargetLang.ToUpperInvariant()}",
                Description: description,
                SizeText: sizeText,
                IsInstalled: installed,
                IsSelected: string.Equals(entry.ModelId, selectedModelId, StringComparison.OrdinalIgnoreCase),
                LocalDirectoryPath: localDir));
        }

        // Also include any custom user-placed model folders inside <portable-root>\models\
        if (Directory.Exists(PortablePaths.ModelsDirectory))
        {
            foreach (var dir in Directory.GetDirectories(PortablePaths.ModelsDirectory))
            {
                string folderName = Path.GetFileName(dir);
                if (folderName.StartsWith('_') ||
                    BuiltInCatalog.Any(c => string.Equals(c.ModelId, folderName, StringComparison.OrdinalIgnoreCase)))
                    continue;

                bool installed = IsModelDirectoryComplete(dir);
                long size = ComputeDirectorySize(dir);
                result.Add(new OfflineModelStatusDto(
                    ModelId: folderName,
                    DisplayName: LocalizationService.Pick($"Custom model ({folderName})", $"Пользовательская модель ({folderName})"),
                    LanguagePair: "CUSTOM",
                    Description: LocalizationService.Pick("Local model folder", "Локальная папка модели"),
                    SizeText: $"{DiskSpaceSummaryDto.FormatBytes(size)} {(installed ? LocalizationService.Pick("(Ready)", "(Готова)") : LocalizationService.Pick("(Incomplete)", "(Неполная)"))}",
                    IsInstalled: installed,
                    IsSelected: string.Equals(folderName, selectedModelId, StringComparison.OrdinalIgnoreCase),
                    LocalDirectoryPath: dir));
            }
        }

        return result;
    }

    private static (string DisplayName, string Description) GetLocalizedModelMeta(OfflineModelCatalogEntry entry)
    {
        if (LocalizationService.IsRussian)
            return (entry.DisplayName, entry.Description);

        return entry.ModelId switch
        {
            "opus-mt-en-ru" => ("Opus-MT: English → Russian (ONNX)", "Compact, fast offline neural translation from English to Russian."),
            "opus-mt-ru-en" => ("Opus-MT: Russian → English (ONNX)", "Local offline neural translation from Russian to English."),
            "opus-mt-de-ru" => ("Opus-MT: German → Russian (ONNX)", "Offline neural translation model from German to Russian."),
            "opus-mt-mul-en" => ("Opus-MT: Multilingual → English (ONNX)", "Universal offline model translating many languages into English."),
            _ => (entry.DisplayName, entry.Description)
        };
    }

    public DiskSpaceSummaryDto GetDiskSpaceSummary()
    {
        PortablePaths.EnsureDirectoriesExist();
        string root = Path.GetPathRoot(PortablePaths.RootDirectory) ?? Path.GetPathRoot(AppContext.BaseDirectory) ?? "C:\\";
        long free = 0;
        long total = 0;

        try
        {
            var drive = new DriveInfo(root);
            if (drive.IsReady)
            {
                free = drive.AvailableFreeSpace;
                total = drive.TotalSize;
            }
        }
        catch { }

        long modelsBytes = ComputeDirectorySize(PortablePaths.ModelsDirectory);
        long cacheBytes = ComputeDirectorySize(PortablePaths.CacheDirectory);

        return new DiskSpaceSummaryDto(root, free, total, modelsBytes, cacheBytes);
    }

    public async Task DownloadModelPackageAsync(
        string modelId,
        IProgress<DownloadProgressInfo>? progress = null,
        CancellationToken ct = default)
    {
        var entry = BuiltInCatalog.FirstOrDefault(m =>
            string.Equals(m.ModelId, modelId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(LocalizationService.Pick($"Model '{modelId}' was not found in catalog.", $"Модель '{modelId}' не найдена в каталоге."));

        using var downloader = new HuggingFaceDownloader();
        int fileCount = entry.Files.Count;

        for (int i = 0; i < fileCount; i++)
        {
            var file = entry.Files[i];
            int fileIndex = i;
            string existingFile = Path.Combine(PortablePaths.ModelsDirectory, entry.ModelId, file.FileName);
            if (File.Exists(existingFile) && new FileInfo(existingFile).Length > 0)
            {
                long existingLen = new FileInfo(existingFile).Length;
                progress?.Report(new DownloadProgressInfo(
                    $"[{fileIndex + 1}/{fileCount}] {file.FileName} ({LocalizationService.Pick("already downloaded", "уже скачан")})",
                    existingLen,
                    existingLen,
                    ((fileIndex + 1) * 100.0) / fileCount));
                continue;
            }

            var wrappedProgress = new Progress<DownloadProgressInfo>(info =>
            {
                double overallPct = ((fileIndex * 100.0) + info.Percentage) / fileCount;
                progress?.Report(new DownloadProgressInfo(
                    $"[{fileIndex + 1}/{fileCount}] {file.FileName}",
                    info.BytesDownloaded,
                    info.TotalBytes,
                    overallPct));
            });

            await downloader.DownloadFileAsync(file.DownloadUrl, entry.ModelId, file.FileName, wrappedProgress, ct);
        }

        await OrtNativeRuntime.EnsureRuntimeAvailableAsync(ct);

        long bosId = 62517;
        long eosId = 0;
        long padId = 62517;
        string cfgPath = Path.Combine(PortablePaths.ModelsDirectory, entry.ModelId, "config.json");
        if (File.Exists(cfgPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(cfgPath, ct));
                var root = doc.RootElement;
                if (root.TryGetProperty("eos_token_id", out var eosEl) && eosEl.TryGetInt64(out long e)) eosId = e;
                if (root.TryGetProperty("pad_token_id", out var padEl) && padEl.TryGetInt64(out long p)) padId = p;
                bosId = root.TryGetProperty("decoder_start_token_id", out var bosEl) && bosEl.TryGetInt64(out long b) ? b : padId;
            }
            catch { }
        }

        // Write manifest with verified MarianMT token IDs
        var manifest = new OnnxOfflineTranslationService.ModelManifest(
            ModelId: entry.ModelId,
            DisplayName: entry.DisplayName,
            EncoderFile: "encoder_model.onnx",
            DecoderFile: "decoder_model.onnx",
            TokenizerModelFile: "source.spm",
            BosTokenId: bosId,
            EosTokenId: eosId,
            PadTokenId: padId);

        string manifestPath = Path.Combine(PortablePaths.ModelsDirectory, entry.ModelId, "pulseisland_manifest.json");
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
            ct);
    }

    public bool DeleteModelPackage(string modelId)
    {
        try
        {
            var modelDir = Path.Combine(PortablePaths.ModelsDirectory, modelId);
            if (Directory.Exists(modelDir))
                Directory.Delete(modelDir, recursive: true);

            var cacheDir = Path.Combine(PortablePaths.DownloadsCacheDirectory, modelId);
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);

            return true;
        }
        catch (Exception ex)
        {
            LogService.Error($"Failed to delete model '{modelId}'.", ex);
            return false;
        }
    }

    public static bool HasCoreModelWeights(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            return false;

        return File.Exists(Path.Combine(directoryPath, "encoder_model.onnx")) &&
               File.Exists(Path.Combine(directoryPath, "decoder_model.onnx"));
    }

    public static bool IsModelDirectoryComplete(string directoryPath)
    {
        if (!HasCoreModelWeights(directoryPath))
            return false;

        return File.Exists(Path.Combine(directoryPath, "source.spm")) &&
               File.Exists(Path.Combine(directoryPath, "vocab.json")) &&
               File.Exists(Path.Combine(directoryPath, "config.json"));
    }

    public static long ComputeDirectorySize(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            return 0;

        try
        {
            long sum = 0;
            foreach (var file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
            {
                try { sum += new FileInfo(file).Length; } catch { }
            }
            return sum;
        }
        catch
        {
            return 0;
        }
    }
}
