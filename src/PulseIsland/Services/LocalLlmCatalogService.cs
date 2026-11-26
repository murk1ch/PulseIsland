using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.Win32;
using PulseIsland.Core;
using PulseIsland.Translation;

namespace PulseIsland.Services;

public sealed record AiModelCatalogEntry(
    string ModelId,
    string DisplayName,
    string FileName,
    string DownloadUrl,
    long ApproximateBytes,
    string Description,
    bool SupportsVision = false,
    string? MmprojFileName = null,
    string? MmprojDownloadUrl = null,
    long MmprojApproximateBytes = 0)
{
    public long TotalApproximateBytes => ApproximateBytes + (SupportsVision ? MmprojApproximateBytes : 0L);
}

public sealed record AiPackageStatus(
    string ModelId,
    string DisplayName,
    string Description,
    bool IsModelInstalled,
    bool IsRuntimeInstalled,
    long InstalledBytes,
    long PartialDownloadBytes,
    string StatusText,
    string HardwareSummary,
    bool HasVulkanCapableGpu,
    bool SupportsVision = false);

/// <summary>
/// Manages catalog, manual resumable downloading, and verification of portable GGUF LLMs & VLMs
/// (Qwen 2.5 3B/7B, Qwen 3 4B, Gemma 2 9B, Qwen 2.5 VL 3B/7B + mmproj) and the llama.cpp Vulkan/CPU server runtime
/// strictly inside PortablePaths.ModelsDirectory. Never triggers downloads automatically.
/// </summary>
public sealed class LocalLlmCatalogService
{
    public const string DefaultModelId = "qwen2.5-3b-instruct";
    public const string DefaultModelFileName = "Qwen2.5-3B-Instruct-Q4_K_M.gguf";
    public const string DefaultModelUrl =
        "https://huggingface.co/bartowski/Qwen2.5-3B-Instruct-GGUF/resolve/main/Qwen2.5-3B-Instruct-Q4_K_M.gguf";
    public const long ApproximateModelBytes = 1_929_903_264L;

    public const string DefaultVisionModelId = "qwen2.5-vl-3b-instruct";

    public static IReadOnlyList<AiModelCatalogEntry> BuiltInModels =>
    [
        new(
            ModelId: DefaultModelId,
            DisplayName: LocalizationService.Pick(
                "Qwen 2.5 3B Instruct (Q4_K_M • ~1.8 GB)",
                "Qwen 2.5 3B Instruct (Q4_K_M • ~1.8 ГБ)"),
            FileName: DefaultModelFileName,
            DownloadUrl: DefaultModelUrl,
            ApproximateBytes: ApproximateModelBytes,
            Description: LocalizationService.Pick(
                "Fast, lightweight 3B model (~1.8 GB). Great for quick summaries, weather briefs, and low-RAM PCs.",
                "Лёгкая и быстрая 3B модель (~1.8 ГБ). Подходит для базового пересказа, погоды и слабых ПК.")),
        new(
            ModelId: "qwen3-4b-instruct",
            DisplayName: LocalizationService.Pick(
                "Qwen 3 4B Instruct (Q4_K_M • ~2.5 GB)",
                "Qwen 3 4B Instruct (Q4_K_M • ~2.5 ГБ)"),
            FileName: "Qwen_Qwen3-4B-Q4_K_M.gguf",
            DownloadUrl: "https://huggingface.co/bartowski/Qwen_Qwen3-4B-GGUF/resolve/main/Qwen_Qwen3-4B-Q4_K_M.gguf",
            ApproximateBytes: 2_500_000_000L,
            Description: LocalizationService.Pick(
                "Next-gen 4B model (~2.5 GB): noticeably smarter and more accurate than 3B while staying compact.",
                "Новое поколение 4B (~2.5 ГБ): заметно умнее и точнее 3B при небольшом размере.")),
        new(
            ModelId: "qwen2.5-7b-instruct",
            DisplayName: LocalizationService.Pick(
                "Qwen 2.5 7B Instruct (Q4_K_M • ~4.4 GB)",
                "Qwen 2.5 7B Instruct (Q4_K_M • ~4.4 ГБ)"),
            FileName: "Qwen2.5-7B-Instruct-Q4_K_M.gguf",
            DownloadUrl: "https://huggingface.co/bartowski/Qwen2.5-7B-Instruct-GGUF/resolve/main/Qwen2.5-7B-Instruct-Q4_K_M.gguf",
            ApproximateBytes: 4_683_073_952L,
            Description: LocalizationService.Pick(
                "Powerful 7B model (~4.4 GB): strong reasoning, natural tone, and accurate slang translation (6 GB+ VRAM / 12 GB+ RAM).",
                "Мощная 7B модель (~4.4 ГБ): высокий интеллект, живой русский язык и точный перевод сленга (от 6 ГБ VRAM / 12 ГБ ОЗУ).")),
        new(
            ModelId: "gemma-2-9b-it",
            DisplayName: LocalizationService.Pick(
                "Gemma 2 9B Instruct (Q4_K_M • ~5.4 GB)",
                "Gemma 2 9B Instruct (Q4_K_M • ~5.4 ГБ)"),
            FileName: "gemma-2-9b-it-Q4_K_M.gguf",
            DownloadUrl: "https://huggingface.co/bartowski/gemma-2-9b-it-GGUF/resolve/main/gemma-2-9b-it-Q4_K_M.gguf",
            ApproximateBytes: 5_761_396_736L,
            Description: LocalizationService.Pick(
                "Google 9B model (~5.4 GB): very natural writing style and deep context understanding (8 GB+ VRAM / 16 GB+ RAM).",
                "Модель Google 9B (~5.4 ГБ): очень естественный русский стиль и глубокое понимание контекста (от 8 ГБ VRAM / 16 ГБ ОЗУ).")),
        new(
            ModelId: DefaultVisionModelId,
            DisplayName: LocalizationService.Pick(
                "Qwen 2.5 VL 3B • Vision (Photo + Text • ~2.6 GB)",
                "Qwen 2.5 VL 3B • Vision (Фото + Текст • ~2.6 ГБ)"),
            FileName: "Qwen2.5-VL-3B-Instruct-Q4_K_M.gguf",
            DownloadUrl: "https://huggingface.co/ggml-org/Qwen2.5-VL-3B-Instruct-GGUF/resolve/main/Qwen2.5-VL-3B-Instruct-Q4_K_M.gguf",
            ApproximateBytes: 1_929_901_056L,
            Description: LocalizationService.Pick(
                "Multimodal 3B Vision model (~1.8 GB + ~806 MB mmproj): reads photos, screenshots, diagrams, and answers image questions in Alt+Q (4 GB+ VRAM / 8 GB+ RAM).",
                "Мультимодальная 3B модель (~1.8 ГБ + ~806 МБ mmproj): «видит» фото, скриншоты, схемы и отвечает на вопросы по изображению в Alt+Q (от 4 ГБ VRAM / 8 ГБ ОЗУ)."),
            SupportsVision: true,
            MmprojFileName: "mmproj-Qwen2.5-VL-3B-Instruct-Q8_0.gguf",
            MmprojDownloadUrl: "https://huggingface.co/ggml-org/Qwen2.5-VL-3B-Instruct-GGUF/resolve/main/mmproj-Qwen2.5-VL-3B-Instruct-Q8_0.gguf",
            MmprojApproximateBytes: 844_757_728L),
        new(
            ModelId: "qwen2.5-vl-7b-instruct",
            DisplayName: LocalizationService.Pick(
                "Qwen 2.5 VL 7B • Vision (Photo + Text • ~5.2 GB)",
                "Qwen 2.5 VL 7B • Vision (Фото + Текст • ~5.2 ГБ)"),
            FileName: "Qwen2.5-VL-7B-Instruct-Q4_K_M.gguf",
            DownloadUrl: "https://huggingface.co/ggml-org/Qwen2.5-VL-7B-Instruct-GGUF/resolve/main/Qwen2.5-VL-7B-Instruct-Q4_K_M.gguf",
            ApproximateBytes: 4_683_072_032L,
            Description: LocalizationService.Pick(
                "High-accuracy 7B Vision model (~4.4 GB + ~814 MB mmproj): excels at photos, math formulas, problems, charts, and text on screenshots (6–8 GB+ VRAM / 14 GB+ RAM).",
                "Мощная Vision 7B модель (~4.4 ГБ + ~814 МБ mmproj): точное распознавание фото, формул, задач, графиков и текста на снимках (от 6–8 ГБ VRAM / 14 ГБ ОЗУ)."),
            SupportsVision: true,
            MmprojFileName: "mmproj-Qwen2.5-VL-7B-Instruct-Q8_0.gguf",
            MmprojDownloadUrl: "https://huggingface.co/ggml-org/Qwen2.5-VL-7B-Instruct-GGUF/resolve/main/mmproj-Qwen2.5-VL-7B-Instruct-Q8_0.gguf",
            MmprojApproximateBytes: 853_119_712L)
    ];

    public const string LlamaRuntimeZipName = "llama-b12441-bin-win-vulkan-x64.zip";
    public const string LlamaRuntimeUrl =
        "https://github.com/ggml-org/llama.cpp/releases/download/b12441/llama-b12441-bin-win-vulkan-x64.zip";

    public static string LlamaRuntimeDirectory => Path.Combine(PortablePaths.ModelsDirectory, "_llama");
    public static string LlamaServerExePath => Path.Combine(LlamaRuntimeDirectory, "llama-server.exe");
    public static string ModelDirectory => GetModelDirectory(DefaultModelId);
    public static string ModelFilePath => GetModelFilePath(DefaultModelId);
    public static string ModelPartialPath => GetModelPartialPath(DefaultModelId);

    public static AiModelCatalogEntry FindModel(string? modelId)
    {
        if (!string.IsNullOrWhiteSpace(modelId))
        {
            var found = BuiltInModels.FirstOrDefault(m =>
                string.Equals(m.ModelId, modelId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (found is not null)
                return found;
        }
        return BuiltInModels[0];
    }

    public static string GetModelDirectory(string? modelId)
        => Path.Combine(PortablePaths.ModelsDirectory, FindModel(modelId).ModelId);

    public static string GetModelFilePath(string? modelId)
    {
        var entry = FindModel(modelId);
        string dir = Path.Combine(PortablePaths.ModelsDirectory, entry.ModelId);
        string expected = Path.Combine(dir, entry.FileName);
        if (File.Exists(expected))
            return expected;

        // Also detect manually placed *.gguf model files (excluding mmproj projector files;
        // for Vision models require "vl" / "vision" / "gemma-3" in filename so a text-only GGUF is never paired with mmproj)
        if (Directory.Exists(dir))
        {
            var manualGguf = Directory.GetFiles(dir, "*.gguf", SearchOption.TopDirectoryOnly)
                .Where(f =>
                {
                    string name = Path.GetFileName(f);
                    if (name.StartsWith("mmproj", StringComparison.OrdinalIgnoreCase))
                        return false;
                    if (entry.SupportsVision &&
                        !name.Contains("vl", StringComparison.OrdinalIgnoreCase) &&
                        !name.Contains("vision", StringComparison.OrdinalIgnoreCase))
                        return false;
                    return true;
                })
                .OrderByDescending(f => new FileInfo(f).Length)
                .FirstOrDefault(f => new FileInfo(f).Length > 100_000_000L);
            if (!string.IsNullOrWhiteSpace(manualGguf))
                return manualGguf;
        }

        return expected;
    }

    public static string? GetMmprojFilePath(string? modelId)
    {
        var entry = FindModel(modelId);
        if (!entry.SupportsVision || string.IsNullOrWhiteSpace(entry.MmprojFileName))
            return null;

        string dir = Path.Combine(PortablePaths.ModelsDirectory, entry.ModelId);
        string expected = Path.Combine(dir, entry.MmprojFileName);
        if (File.Exists(expected))
            return expected;

        if (Directory.Exists(dir))
        {
            var manualProj = Directory.GetFiles(dir, "*mmproj*.gguf", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => new FileInfo(f).Length)
                .FirstOrDefault(f => new FileInfo(f).Length > 50_000_000L);
            if (!string.IsNullOrWhiteSpace(manualProj))
                return manualProj;
        }

        return expected;
    }

    public static string GetModelPartialPath(string? modelId)
    {
        var entry = FindModel(modelId);
        return Path.Combine(PortablePaths.DownloadsCacheDirectory, entry.ModelId, entry.FileName + ".part");
    }

    public static string? GetMmprojPartialPath(string? modelId)
    {
        var entry = FindModel(modelId);
        if (!entry.SupportsVision || string.IsNullOrWhiteSpace(entry.MmprojFileName))
            return null;
        return Path.Combine(PortablePaths.DownloadsCacheDirectory, entry.ModelId, entry.MmprojFileName + ".part");
    }

    public static bool IsMainWeightsInstalled(string? modelId = null)
    {
        string path = GetModelFilePath(modelId);
        return File.Exists(path) && new FileInfo(path).Length > 100_000_000L;
    }

    public static bool IsMmprojInstalled(string? modelId = null)
    {
        var entry = FindModel(modelId);
        if (!entry.SupportsVision)
            return true;

        string? projPath = GetMmprojFilePath(entry.ModelId);
        return !string.IsNullOrWhiteSpace(projPath) && File.Exists(projPath) && new FileInfo(projPath).Length > 50_000_000L;
    }

    public static bool IsModelInstalled(string? modelId = null)
        => IsMainWeightsInstalled(modelId) && IsMmprojInstalled(modelId);

    public static bool IsRuntimeInstalled() => File.Exists(LlamaServerExePath);
    public static bool IsReady(string? modelId = null) => IsModelInstalled(modelId) && IsRuntimeInstalled();

    /// <summary>
    /// Returns the preferred model if it supports Vision and is ready; otherwise finds the strongest
    /// installed Vision (VLM) model on disk, or null if no Vision model has been downloaded yet.
    /// </summary>
    public static string? FindBestInstalledVisionModelId(string? preferredModelId = null)
    {
        if (!string.IsNullOrWhiteSpace(preferredModelId))
        {
            var preferred = FindModel(preferredModelId);
            if (preferred.SupportsVision && IsReady(preferred.ModelId))
                return preferred.ModelId;
        }

        // Prefer larger installed Vision model (7B over 3B) if user downloaded both
        var installedVision = BuiltInModels
            .Where(m => m.SupportsVision && IsReady(m.ModelId))
            .OrderByDescending(m => m.ApproximateBytes)
            .FirstOrDefault();

        return installedVision?.ModelId;
    }

    public AiPackageStatus GetStatus(string? modelId = null)
    {
        PortablePaths.EnsureDirectoriesExist();

        var entry = FindModel(modelId);
        string modelPath = GetModelFilePath(entry.ModelId);
        string? mmprojPath = GetMmprojFilePath(entry.ModelId);
        string partialPath = GetModelPartialPath(entry.ModelId);
        string? mmprojPartialPath = GetMmprojPartialPath(entry.ModelId);

        bool mainOk = IsMainWeightsInstalled(entry.ModelId);
        bool projOk = IsMmprojInstalled(entry.ModelId);
        bool modelOk = mainOk && projOk;
        bool runtimeOk = IsRuntimeInstalled();

        long modelBytes = mainOk ? new FileInfo(modelPath).Length : 0L;
        long projBytes = (entry.SupportsVision && projOk && mmprojPath is not null) ? new FileInfo(mmprojPath).Length : 0L;
        long runtimeBytes = runtimeOk ? ComputeDirBytes(LlamaRuntimeDirectory) : 0L;

        long partialBytes = 0L;
        if (File.Exists(partialPath)) partialBytes += new FileInfo(partialPath).Length;
        if (mmprojPartialPath is not null && File.Exists(mmprojPartialPath)) partialBytes += new FileInfo(mmprojPartialPath).Length;

        var (hwSummary, hasGpu) = DetectHardware();
        string visionTag = entry.SupportsVision
            ? LocalizationService.Pick(" • Vision (Photo)", " • Vision (Фото)")
            : string.Empty;

        string status;
        if (modelOk && runtimeOk)
        {
            status = LocalizationService.Pick(
                $"Ready ({DiskSpaceSummaryDto.FormatBytes(modelBytes + projBytes + runtimeBytes)}{visionTag} • {(hasGpu ? "GPU Vulkan + CPU" : "CPU")})",
                $"Готова к работе ({DiskSpaceSummaryDto.FormatBytes(modelBytes + projBytes + runtimeBytes)}{visionTag} • {(hasGpu ? "GPU Vulkan + CPU" : "CPU")})");
        }
        else if (mainOk && !projOk)
        {
            status = LocalizationService.Pick(
                $"Main model downloaded; Vision projector mmproj (~{DiskSpaceSummaryDto.FormatBytes(entry.MmprojApproximateBytes)}) needed — click \"Resume\"",
                $"Основная модель скачана, нужно докачать визуальный модуль mmproj (~{DiskSpaceSummaryDto.FormatBytes(entry.MmprojApproximateBytes)}) — нажмите «Докачать»");
        }
        else if (partialBytes > 0)
        {
            status = LocalizationService.Pick(
                $"Downloaded {DiskSpaceSummaryDto.FormatBytes(partialBytes + modelBytes + projBytes)} of ~{DiskSpaceSummaryDto.FormatBytes(entry.TotalApproximateBytes)} — click \"Resume\"",
                $"Скачано {DiskSpaceSummaryDto.FormatBytes(partialBytes + modelBytes + projBytes)} из ~{DiskSpaceSummaryDto.FormatBytes(entry.TotalApproximateBytes)} — нажмите «Докачать»");
        }
        else if (modelOk && !runtimeOk)
        {
            status = LocalizationService.Pick(
                "Model downloaded; llama-server runtime (~36 MB) needed — click \"Download\"",
                "Модель скачана, нужно докачать движок llama-server (~36 МБ) — нажмите «Скачать»");
        }
        else
        {
            string runtimeNote = runtimeOk
                ? LocalizationService.Pick("runtime already installed", "движок уже установлен")
                : LocalizationService.Pick("+ ~36.5 MB GPU/CPU runtime", "+ ~36.5 МБ движок GPU/CPU");
            string sizeNote = entry.SupportsVision
                ? LocalizationService.Pick(
                    $"~{DiskSpaceSummaryDto.FormatBytes(entry.TotalApproximateBytes)} model + mmproj",
                    $"~{DiskSpaceSummaryDto.FormatBytes(entry.TotalApproximateBytes)} модель + mmproj")
                : LocalizationService.Pick(
                    $"~{DiskSpaceSummaryDto.FormatBytes(entry.ApproximateBytes)} model",
                    $"~{DiskSpaceSummaryDto.FormatBytes(entry.ApproximateBytes)} модель");
            status = LocalizationService.Pick(
                $"Not installed ({sizeNote}, {runtimeNote})",
                $"Не установлена ({sizeNote}, {runtimeNote})");
        }

        return new AiPackageStatus(
            ModelId: entry.ModelId,
            DisplayName: entry.DisplayName,
            Description: entry.Description,
            IsModelInstalled: modelOk,
            IsRuntimeInstalled: runtimeOk,
            InstalledBytes: modelBytes + projBytes + runtimeBytes,
            PartialDownloadBytes: partialBytes + (mainOk && !projOk ? modelBytes : 0L),
            StatusText: status,
            HardwareSummary: hwSummary,
            HasVulkanCapableGpu: hasGpu,
            SupportsVision: entry.SupportsVision);
    }

    public async Task DownloadAiPackageAsync(
        string? modelId = null,
        IProgress<DownloadProgressInfo>? progress = null,
        CancellationToken ct = default)
    {
        PortablePaths.EnsureDirectoriesExist();
        var entry = FindModel(modelId);

        // Step 1: Download & extract llama-server (Vulkan GPU + CPU fallback + mtmd.dll) if missing
        if (!IsRuntimeInstalled())
        {
            await DownloadAndExtractLlamaRuntimeAsync(progress, ct).ConfigureAwait(false);
        }

        // Step 2: Resumable download of main GGUF weights via HuggingFaceDownloader
        if (!IsMainWeightsInstalled(entry.ModelId))
        {
            using var downloader = new HuggingFaceDownloader();
            var wrapped = new Progress<DownloadProgressInfo>(info =>
            {
                progress?.Report(new DownloadProgressInfo(
                    $"{entry.ModelId}: {info.FileName}",
                    info.BytesDownloaded,
                    info.TotalBytes ?? entry.ApproximateBytes,
                    info.Percentage));
            });

            await downloader.DownloadFileAsync(entry.DownloadUrl, entry.ModelId, entry.FileName, wrapped, ct)
                .ConfigureAwait(false);
        }

        // Step 3: For Vision (VLM) models, download the multimodal projector (mmproj-*.gguf)
        if (entry.SupportsVision &&
            !IsMmprojInstalled(entry.ModelId) &&
            !string.IsNullOrWhiteSpace(entry.MmprojDownloadUrl) &&
            !string.IsNullOrWhiteSpace(entry.MmprojFileName))
        {
            using var downloader = new HuggingFaceDownloader();
            var wrappedProj = new Progress<DownloadProgressInfo>(info =>
            {
                progress?.Report(new DownloadProgressInfo(
                    $"{entry.ModelId} (Vision mmproj): {info.FileName}",
                    info.BytesDownloaded,
                    info.TotalBytes ?? entry.MmprojApproximateBytes,
                    info.Percentage));
            });

            await downloader.DownloadFileAsync(entry.MmprojDownloadUrl, entry.ModelId, entry.MmprojFileName, wrappedProj, ct)
                .ConfigureAwait(false);
        }
    }

    private static async Task DownloadAndExtractLlamaRuntimeAsync(IProgress<DownloadProgressInfo>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(LlamaRuntimeDirectory);
        string cacheDir = Path.Combine(PortablePaths.DownloadsCacheDirectory, "_llama");
        Directory.CreateDirectory(cacheDir);

        string zipPartPath = Path.Combine(cacheDir, LlamaRuntimeZipName + ".part");
        string zipFinalPath = Path.Combine(cacheDir, LlamaRuntimeZipName);

        using var handler = new HttpClientHandler { AllowAutoRedirect = true };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PulseIsland/1.0");

        long existing = File.Exists(zipPartPath) ? new FileInfo(zipPartPath).Length : 0;
        using var req = new HttpRequestMessage(HttpMethod.Get, LlamaRuntimeUrl);
        if (existing > 0)
            req.Headers.Range = new RangeHeaderValue(existing, null);

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        bool append = resp.StatusCode == HttpStatusCode.PartialContent;
        long? total = resp.Content.Headers.ContentLength;
        if (append && resp.Content.Headers.ContentRange?.Length is long fullLen)
            total = fullLen;
        else if (append && total.HasValue)
            total += existing;
        else if (!append)
        {
            resp.EnsureSuccessStatusCode();
            existing = 0;
        }

        await using (var remote = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var fs = new FileStream(zipPartPath, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            byte[] buf = new byte[81920];
            long downloaded = existing;
            long lastTick = 0;
            int read;
            while ((read = await remote.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await fs.WriteAsync(buf.AsMemory(0, read), ct).ConfigureAwait(false);
                downloaded += read;
                long now = Environment.TickCount64;
                if (now - lastTick >= 100)
                {
                    lastTick = now;
                    double pct = total is > 0 ? Math.Clamp(downloaded * 100.0 / total.Value, 0, 100) : 0;
                    progress?.Report(new DownloadProgressInfo(
                        LocalizationService.Pick("llama.cpp runtime (Vulkan + CPU)", "Движок llama.cpp (Vulkan + CPU)"),
                        downloaded, total, pct));
                }
            }
            await fs.FlushAsync(ct).ConfigureAwait(false);
        }

        File.Move(zipPartPath, zipFinalPath, overwrite: true);

        progress?.Report(new DownloadProgressInfo(
            LocalizationService.Pick("Extracting llama-server...", "Распаковка llama-server..."),
            1, 1, 100));
        using (var archive = ZipFile.OpenRead(zipFinalPath))
        {
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                    continue;
                if (entry.Name.Equals("llama-server.exe", StringComparison.OrdinalIgnoreCase) ||
                    entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    string outPath = Path.Combine(LlamaRuntimeDirectory, entry.Name);
                    entry.ExtractToFile(outPath, overwrite: true);
                }
            }
        }

        try { if (File.Exists(zipFinalPath)) File.Delete(zipFinalPath); } catch { }
        LogService.Info("LocalLlmCatalogService: llama-server runtime extracted to models\\_llama.");
    }

    public bool DeleteAiPackage(string? modelId = null)
    {
        try
        {
            var entry = FindModel(modelId);
            string dir = GetModelDirectory(entry.ModelId);
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
            string cacheModelDir = Path.Combine(PortablePaths.DownloadsCacheDirectory, entry.ModelId);
            if (Directory.Exists(cacheModelDir))
                Directory.Delete(cacheModelDir, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("Failed to delete local AI model package.", ex);
            return false;
        }
    }

    private static readonly Lazy<(string? GpuName, long MaxVram, bool HasVulkanGpu)> CachedHardware = new(DetectHardwareCore);

    public static (string summary, bool hasVulkanGpu) DetectHardware()
    {
        var (gpuName, maxVram, hasGpu) = CachedHardware.Value;
        string displayGpu = string.IsNullOrWhiteSpace(gpuName)
            ? LocalizationService.Pick("Not detected", "Не обнаружена")
            : gpuName;
        string vramLabel = maxVram > 0 ? $" ({DiskSpaceSummaryDto.FormatBytes(maxVram)} VRAM)" : string.Empty;
        string threadsLabel = LocalizationService.Pick(
            $"CPU: {Environment.ProcessorCount} threads",
            $"CPU: {Environment.ProcessorCount} потоков");
        return ($"{displayGpu}{vramLabel} • {threadsLabel}", hasGpu);
    }

    private static (string? GpuName, long MaxVram, bool HasVulkanGpu) DetectHardwareCore()
    {
        string? gpuName = null;
        long maxVram = 0;

        try
        {
            const string displayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
            using var root = Registry.LocalMachine.OpenSubKey(displayClassKey);
            if (root is not null)
            {
                foreach (string subName in root.GetSubKeyNames())
                {
                    if (!char.IsDigit(subName.FirstOrDefault()))
                        continue;

                    using var sub = root.OpenSubKey(subName);
                    if (sub is null) continue;

                    string name = sub.GetValue("DriverDesc")?.ToString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(name) || name.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase))
                        continue;

                    long vram = sub.GetValue("HardwareInformation.qwMemorySize") switch
                    {
                        long l => l,
                        int i => i,
                        byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
                        _ => 0L
                    };

                    if (vram >= maxVram)
                    {
                        maxVram = vram;
                        gpuName = name;
                    }
                }
            }
        }
        catch { }

        bool hasGpu = maxVram > 0;
        return (gpuName, maxVram, hasGpu);
    }

    private static long ComputeDirBytes(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        long total = 0;
        foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; } catch { }
        }
        return total;
    }
}
