using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using PulseIsland.Core;
using PulseIsland.Services;

namespace PulseIsland.Translation;

public sealed record DownloadProgressInfo(
    string FileName,
    long BytesDownloaded,
    long? TotalBytes,
    double Percentage);

/// <summary>
/// Downloads model artifacts from Hugging Face Hub with HTTP Range resume support,
/// free space verification on the portable drive, and strict storage inside <portable-root>\cache\ and models\.
/// </summary>
public sealed class HuggingFaceDownloader : IDisposable
{
    private readonly HttpClient _httpClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = true
    })
    {
        Timeout = TimeSpan.FromMinutes(30)
    };

    public async Task DownloadFileAsync(
        string url,
        string modelId,
        string fileName,
        IProgress<DownloadProgressInfo>? progress = null,
        CancellationToken ct = default)
    {
        var partDir = Path.Combine(PortablePaths.DownloadsCacheDirectory, modelId);
        var targetDir = Path.Combine(PortablePaths.ModelsDirectory, modelId);
        Directory.CreateDirectory(partDir);
        Directory.CreateDirectory(targetDir);

        string partPath = Path.Combine(partDir, fileName + ".part");
        string finalPath = Path.Combine(targetDir, fileName);

        long existingBytes = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existingBytes > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingBytes, null);
        }

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        bool appendMode = false;
        long? totalBytes = response.Content.Headers.ContentLength;

        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            appendMode = true;
            if (response.Content.Headers.ContentRange?.Length is long fullLen)
                totalBytes = fullLen;
            else if (totalBytes.HasValue)
                totalBytes += existingBytes;
        }
        else if (response.IsSuccessStatusCode)
        {
            existingBytes = 0;
        }
        else
        {
            response.EnsureSuccessStatusCode();
        }

        EnsureEnoughDiskSpace(totalBytes - existingBytes);

        await using (var remoteStream = await response.Content.ReadAsStreamAsync(ct))
        await using (var fileStream = new FileStream(
            partPath,
            appendMode ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            useAsync: true))
        {
            byte[] buffer = new byte[81920];
            long downloaded = existingBytes;
            long lastReportTicks = 0;

            int read;
            while ((read = await remoteStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                downloaded += read;

                long nowTicks = Environment.TickCount64;
                if (nowTicks - lastReportTicks >= 100)
                {
                    lastReportTicks = nowTicks;
                    double pct = totalBytes is > 0 ? Math.Clamp(downloaded * 100.0 / totalBytes.Value, 0, 100) : 0;
                    progress?.Report(new DownloadProgressInfo(fileName, downloaded, totalBytes, pct));
                }
            }

            await fileStream.FlushAsync(ct);
            progress?.Report(new DownloadProgressInfo(fileName, downloaded, totalBytes, 100.0));
        }

        File.Move(partPath, finalPath, overwrite: true);
        LogService.Info($"Downloaded model file '{fileName}' to '{finalPath}'.");
    }

    private static void EnsureEnoughDiskSpace(long? requiredAdditionalBytes)
    {
        if (requiredAdditionalBytes is null or <= 0)
            return;

        var root = Path.GetPathRoot(PortablePaths.ModelsDirectory);
        if (string.IsNullOrWhiteSpace(root))
            return;

        var drive = new DriveInfo(root);
        // Require at least 100 MB safety buffer on top of file size
        if (drive.AvailableFreeSpace < requiredAdditionalBytes.Value + 100L * 1024 * 1024)
        {
            throw new IOException(LocalizationService.Pick(
                $"Not enough free disk space on drive {drive.Name} to download the model.",
                $"Недостаточно свободного места на диске {drive.Name} для загрузки модели."));
        }
    }

    public void Dispose() => _httpClient.Dispose();
}
