using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.Win32;
using PulseIsland.Core;
using PulseIsland.Translation;

namespace PulseIsland.Services;

public sealed record AiPackageStatus(
    string ModelId,
    string DisplayName,
    bool IsModelInstalled,
    bool IsRuntimeInstalled,
    long InstalledBytes,
    long PartialDownloadBytes,
    string StatusText,
    string HardwareSummary,
    bool HasVulkanCapableGpu);

/// <summary>
/// Manages downloading and verifying the portable 3B GGUF LLM (Qwen 2.5 3B Instruct Q4_K_M)
/// and the llama.cpp Vulkan/CPU server runtime strictly inside <portable-root>\models\.
/// Supports full HTTP Range resume if cancelled mid-download.
/// </summary>
public sealed class LocalLlmCatalogService
{
    public const string DefaultModelId = "qwen2.5-3b-instruct";
    public const string DefaultModelFileName = "Qwen2.5-3B-Instruct-Q4_K_M.gguf";
    public const string DefaultModelUrl =
        "https://huggingface.co/bartowski/Qwen2.5-3B-Instruct-GGUF/resolve/main/Qwen2.5-3B-Instruct-Q4_K_M.gguf";
    public const long ApproximateModelBytes = 1_929_903_264L;

    public const string LlamaRuntimeZipName = "llama-b12441-bin-win-vulkan-x64.zip";
    public const string LlamaRuntimeUrl =
        "https://github.com/ggml-org/llama.cpp/releases/download/b12441/llama-b12441-bin-win-vulkan-x64.zip";

    public static string LlamaRuntimeDirectory => Path.Combine(PortablePaths.ModelsDirectory, "_llama");
    public static string LlamaServerExePath => Path.Combine(LlamaRuntimeDirectory, "llama-server.exe");
    public static string ModelDirectory => Path.Combine(PortablePaths.ModelsDirectory, DefaultModelId);
    public static string ModelFilePath => Path.Combine(ModelDirectory, DefaultModelFileName);
    public static string ModelPartialPath => Path.Combine(PortablePaths.DownloadsCacheDirectory, DefaultModelId, DefaultModelFileName + ".part");

    public static bool IsModelInstalled() => File.Exists(ModelFilePath) && new FileInfo(ModelFilePath).Length > 100_000_000L;
    public static bool IsRuntimeInstalled() => File.Exists(LlamaServerExePath);
    public static bool IsReady() => IsModelInstalled() && IsRuntimeInstalled();

    public AiPackageStatus GetStatus()
    {
        PortablePaths.EnsureDirectoriesExist();

        bool modelOk = IsModelInstalled();
        bool runtimeOk = IsRuntimeInstalled();
        long modelBytes = modelOk ? new FileInfo(ModelFilePath).Length : 0L;
        long runtimeBytes = runtimeOk ? ComputeDirBytes(LlamaRuntimeDirectory) : 0L;
        long partialBytes = File.Exists(ModelPartialPath) ? new FileInfo(ModelPartialPath).Length : 0L;

        var (hwSummary, hasGpu) = DetectHardware();

        string status;
        if (modelOk && runtimeOk)
        {
            status = LocalizationService.Pick(
                $"Ready ({DiskSpaceSummaryDto.FormatBytes(modelBytes + runtimeBytes)} • {(hasGpu ? "GPU Vulkan + CPU" : "CPU")})",
                $"Готова к работе ({DiskSpaceSummaryDto.FormatBytes(modelBytes + runtimeBytes)} • {(hasGpu ? "GPU Vulkan + CPU" : "CPU")})");
        }
        else if (partialBytes > 0)
        {
            status = LocalizationService.Pick(
                $"Downloaded {DiskSpaceSummaryDto.FormatBytes(partialBytes)} of ~{DiskSpaceSummaryDto.FormatBytes(ApproximateModelBytes)} — click \"Resume\"",
                $"Скачано {DiskSpaceSummaryDto.FormatBytes(partialBytes)} из ~{DiskSpaceSummaryDto.FormatBytes(ApproximateModelBytes)} — нажмите «Докачать»");
        }
        else if (modelOk && !runtimeOk)
        {
            status = LocalizationService.Pick(
                "Model downloaded, llama-server runtime (~36 MB) needed — click \"Download\"",
                "Модель скачана, нужно докачать движок llama-server (~36 МБ) — нажмите «Скачать»");
        }
        else
        {
            status = LocalizationService.Pick(
                $"Not installed (~{DiskSpaceSummaryDto.FormatBytes(ApproximateModelBytes)} model + ~36.5 MB GPU/CPU runtime)",
                $"Не установлена (~{DiskSpaceSummaryDto.FormatBytes(ApproximateModelBytes)} модель + ~36.5 МБ движок GPU/CPU)");
        }

        return new AiPackageStatus(
            ModelId: DefaultModelId,
            DisplayName: "Qwen 2.5 3B Instruct (Q4_K_M • GGUF)",
            IsModelInstalled: modelOk,
            IsRuntimeInstalled: runtimeOk,
            InstalledBytes: modelBytes + runtimeBytes,
            PartialDownloadBytes: partialBytes,
            StatusText: status,
            HardwareSummary: hwSummary,
            HasVulkanCapableGpu: hasGpu);
    }

    public async Task DownloadAiPackageAsync(IProgress<DownloadProgressInfo>? progress = null, CancellationToken ct = default)
    {
        PortablePaths.EnsureDirectoriesExist();

        // Step 1: Download & extract llama-server (Vulkan GPU + CPU fallback) if missing
        if (!IsRuntimeInstalled())
        {
            await DownloadAndExtractLlamaRuntimeAsync(progress, ct).ConfigureAwait(false);
        }

        // Step 2: Resumable download of Qwen 2.5 3B Q4_K_M GGUF via HuggingFaceDownloader
        if (!IsModelInstalled())
        {
            using var downloader = new HuggingFaceDownloader();
            var wrapped = new Progress<DownloadProgressInfo>(info =>
            {
                progress?.Report(new DownloadProgressInfo(
                    $"Qwen2.5-3B Q4_K_M: {info.FileName}",
                    info.BytesDownloaded,
                    info.TotalBytes ?? ApproximateModelBytes,
                    info.Percentage));
            });

            await downloader.DownloadFileAsync(DefaultModelUrl, DefaultModelId, DefaultModelFileName, wrapped, ct)
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

    public bool DeleteAiPackage()
    {
        try
        {
            if (Directory.Exists(ModelDirectory))
                Directory.Delete(ModelDirectory, recursive: true);
            string cacheModelDir = Path.Combine(PortablePaths.DownloadsCacheDirectory, DefaultModelId);
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

    public static (string summary, bool hasVulkanGpu) DetectHardware()
    {
        string gpuName = LocalizationService.Pick("Not detected", "Не обнаружена");
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

        bool discreteGpu = maxVram >= 1_500_000_000L ||
                           gpuName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                           gpuName.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ||
                           gpuName.Contains("Arc", StringComparison.OrdinalIgnoreCase);

        int threads = Environment.ProcessorCount;
        string summary = discreteGpu
            ? LocalizationService.Pick($"GPU: {gpuName} (Vulkan) • CPU: {threads} threads", $"GPU: {gpuName} (Vulkan) • CPU: {threads} потоков")
            : LocalizationService.Pick($"CPU: {threads} threads (integrated/low-VRAM GPU — Auto recommended)", $"CPU: {threads} потоков (GPU слабый/встроенный — рекомендуется Auto)");
        return (summary, discreteGpu);
    }

    private static long ComputeDirBytes(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        long sum = 0;
        foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { sum += new FileInfo(f).Length; } catch { }
        }
        return sum;
    }
}
