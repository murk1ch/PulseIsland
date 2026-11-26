using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using PulseIsland.Core;
using PulseIsland.Translation;

namespace PulseIsland.Services;

/// <summary>
/// Zero-NuGet, 100% offline voice dictation service using Win32 waveIn 16kHz mono PCM capture
/// and portable ggml-org whisper.cpp (stored strictly inside PortablePaths.ModelsDirectory\_whisper).
/// Never auto-downloads models and never writes temporary audio outside PortablePaths.CacheDirectory.
/// </summary>
public sealed class WhisperVoiceService : IDisposable
{
    public const string WhisperZipName = "whisper-bin-x64.zip";
    public const string WhisperZipUrl =
        "https://github.com/ggml-org/whisper.cpp/releases/download/b5907/whisper-bin-x64.zip";
    public const string DefaultModelFileName = "ggml-base.bin";
    public const string DefaultModelUrl =
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin";
    public const long ApproximatePackageBytes = 157_119_893L;

    public static string WhisperDirectory => Path.Combine(PortablePaths.ModelsDirectory, "_whisper");
    public static string ModelFilePath => Path.Combine(WhisperDirectory, DefaultModelFileName);

    public static string? FindWhisperExePath()
    {
        if (!Directory.Exists(WhisperDirectory))
            return null;

        string cli = Path.Combine(WhisperDirectory, "whisper-cli.exe");
        if (File.Exists(cli)) return cli;

        string main = Path.Combine(WhisperDirectory, "main.exe");
        if (File.Exists(main)) return main;

        return Directory.GetFiles(WhisperDirectory, "*.exe", SearchOption.AllDirectories)
            .FirstOrDefault(f =>
            {
                string n = Path.GetFileName(f);
                return n.Contains("whisper-cli", StringComparison.OrdinalIgnoreCase) ||
                       n.Equals("main.exe", StringComparison.OrdinalIgnoreCase);
            });
    }

    public static bool IsRuntimeInstalled() => !string.IsNullOrWhiteSpace(FindWhisperExePath());
    public static bool IsModelInstalled() => File.Exists(ModelFilePath) && new FileInfo(ModelFilePath).Length > 30_000_000L;
    public static bool IsReady() => IsRuntimeInstalled() && IsModelInstalled();

    public static string GetStatusText()
    {
        bool rt = IsRuntimeInstalled();
        bool mdl = IsModelInstalled();
        if (rt && mdl)
        {
            long bytes = new FileInfo(ModelFilePath).Length;
            return LocalizationService.Pick(
                $"Ready for offline dictation ({DiskSpaceSummaryDto.FormatBytes(bytes)} • EN/RU/Auto)",
                $"Готов к оффлайн-диктовке ({DiskSpaceSummaryDto.FormatBytes(bytes)} • RU/EN/Auto)");
        }
        if (mdl && !rt)
            return LocalizationService.Pick(
                "Model downloaded; whisper-cli runtime (~9 MB) needed",
                "Модель скачана, нужно докачать движок whisper-cli (~9 МБ)");
        return LocalizationService.Pick(
            "Not downloaded (~150 MB: whisper.cpp runtime + multilingual Base model)",
            "Не скачан (~150 МБ: движок whisper.cpp + мультиязычная модель Base)");
    }

    public async Task DownloadPackageAsync(
        IProgress<DownloadProgressInfo>? progress = null,
        CancellationToken ct = default)
    {
        PortablePaths.EnsureDirectoriesExist();
        Directory.CreateDirectory(WhisperDirectory);

        if (!IsRuntimeInstalled())
        {
            await DownloadAndExtractWhisperRuntimeAsync(progress, ct).ConfigureAwait(false);
        }

        if (!IsModelInstalled())
        {
            using var downloader = new HuggingFaceDownloader();
            var wrapped = new Progress<DownloadProgressInfo>(info =>
            {
                progress?.Report(new DownloadProgressInfo(
                    $"Whisper: {info.FileName}",
                    info.BytesDownloaded,
                    info.TotalBytes ?? 147_951_465L,
                    info.Percentage));
            });

            await downloader.DownloadFileAsync(DefaultModelUrl, "_whisper", DefaultModelFileName, wrapped, ct)
                .ConfigureAwait(false);
        }
    }

    private static async Task DownloadAndExtractWhisperRuntimeAsync(
        IProgress<DownloadProgressInfo>? progress,
        CancellationToken ct)
    {
        string cacheDir = Path.Combine(PortablePaths.DownloadsCacheDirectory, "_whisper");
        Directory.CreateDirectory(cacheDir);
        string zipPart = Path.Combine(cacheDir, WhisperZipName + ".part");
        string zipFinal = Path.Combine(cacheDir, WhisperZipName);

        using var handler = new HttpClientHandler { AllowAutoRedirect = true };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PulseIsland/1.0");

        long existing = File.Exists(zipPart) ? new FileInfo(zipPart).Length : 0;
        using var req = new HttpRequestMessage(HttpMethod.Get, WhisperZipUrl);
        if (existing > 0)
            req.Headers.Range = new RangeHeaderValue(existing, null);

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && File.Exists(zipPart) && existing > 100_000)
        {
            // Partial file already matches full remote size (e.g. cancelled during extraction)
            File.Move(zipPart, zipFinal, overwrite: true);
        }
        else
        {
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
            await using (var fs = new FileStream(zipPart, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                byte[] buf = new byte[81920];
                long downloaded = existing;
                int read;
                while ((read = await remote.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, read), ct).ConfigureAwait(false);
                    downloaded += read;
                    double pct = total is > 0 ? Math.Clamp(downloaded * 100.0 / total.Value, 0, 100) : 0;
                    progress?.Report(new DownloadProgressInfo(
                        LocalizationService.Pick("whisper.cpp runtime (x64)", "Движок whisper.cpp (x64)"),
                        downloaded, total, pct));
                }
                await fs.FlushAsync(ct).ConfigureAwait(false);
            }

            File.Move(zipPart, zipFinal, overwrite: true);
        }

        using (var archive = ZipFile.OpenRead(zipFinal))
        {
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                if (entry.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                    entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    string outPath = Path.Combine(WhisperDirectory, entry.Name);
                    entry.ExtractToFile(outPath, overwrite: true);
                }
            }
        }

        try { if (File.Exists(zipFinal)) File.Delete(zipFinal); } catch { }
    }

    public bool DeletePackage()
    {
        try
        {
            if (Directory.Exists(WhisperDirectory))
                Directory.Delete(WhisperDirectory, recursive: true);
            string cacheDir = Path.Combine(PortablePaths.DownloadsCacheDirectory, "_whisper");
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            LogService.Warn($"Failed to delete Whisper package: {ex.Message}");
            return false;
        }
    }

    // ── Win32 waveIn 16kHz 16-bit Mono PCM Recorder ──

    private const int CALLBACK_NULL = 0;
    private const ushort WAVE_FORMAT_PCM = 1;
    private const uint WHDR_DONE = 0x00000001;
    private const int SampleRate = 16000;
    private const int BufferDurationMs = 80;
    private const int BufferByteSize = SampleRate * 2 * BufferDurationMs / 1000; // 2560 bytes
    private const int BufferCount = 6;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [DllImport("winmm.dll")]
    private static extern int waveInGetNumDevs();

    [DllImport("winmm.dll")]
    private static extern int waveInOpen(out IntPtr phwi, uint uDeviceID, ref WAVEFORMATEX pwfx, IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);

    [DllImport("winmm.dll")]
    private static extern int waveInPrepareHeader(IntPtr hwi, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveInUnprepareHeader(IntPtr hwi, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveInAddBuffer(IntPtr hwi, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveInStart(IntPtr hwi);

    [DllImport("winmm.dll")]
    private static extern int waveInReset(IntPtr hwi);

    [DllImport("winmm.dll")]
    private static extern int waveInClose(IntPtr hwi);

    private readonly object _recLock = new();
    private IntPtr _hWaveIn;
    private IntPtr[]? _headerPtrs;
    private IntPtr[]? _dataPtrs;
    private int _nextBufferIndex;
    private MemoryStream? _pcmStream;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private bool _isRecording;
    private bool _disposed;

    public bool IsRecording => _isRecording;
    public event Action<double>? LevelChanged;

    public void CancelRecording()
    {
        Task? pollToWait;
        lock (_recLock)
        {
            if (!_isRecording && _hWaveIn == IntPtr.Zero)
                return;
            _isRecording = false;
            _pollCts?.Cancel();
            pollToWait = _pollTask;
        }

        if (pollToWait is not null)
        {
            try { pollToWait.Wait(300); } catch { }
        }

        lock (_recLock)
        {
            CleanupWaveInLocked();
        }
        LevelChanged?.Invoke(0.0);
    }

    public (bool Started, string? Error) StartRecording()
    {
        lock (_recLock)
        {
            if (_disposed)
                return (false, LocalizationService.Pick("Service stopped.", "Сервис остановлен."));
            if (_isRecording)
                return (true, null);

            if (waveInGetNumDevs() <= 0)
                return (false, LocalizationService.Pick("No microphone detected in the system.", "В системе не найден микрофон."));

            var fmt = new WAVEFORMATEX
            {
                wFormatTag = WAVE_FORMAT_PCM,
                nChannels = 1,
                nSamplesPerSec = SampleRate,
                wBitsPerSample = 16,
                nBlockAlign = 2,
                nAvgBytesPerSec = SampleRate * 2,
                cbSize = 0
            };

            const uint WAVE_MAPPER = unchecked((uint)-1);
            int rc = waveInOpen(out _hWaveIn, WAVE_MAPPER, ref fmt, IntPtr.Zero, IntPtr.Zero, CALLBACK_NULL);
            if (rc != 0 || _hWaveIn == IntPtr.Zero)
                return (false, LocalizationService.Pick($"Failed to open microphone (code {rc}).", $"Не удалось открыть микрофон (код {rc})."));

            _pcmStream = new MemoryStream(SampleRate * 2 * 15);
            _headerPtrs = new IntPtr[BufferCount];
            _dataPtrs = new IntPtr[BufferCount];
            uint hdrSize = (uint)Marshal.SizeOf<WAVEHDR>();

            try
            {
                _nextBufferIndex = 0;
                for (int i = 0; i < BufferCount; i++)
                {
                    _dataPtrs[i] = Marshal.AllocHGlobal(BufferByteSize);
                    _headerPtrs[i] = Marshal.AllocHGlobal((int)hdrSize);
                    var hdr = new WAVEHDR
                    {
                        lpData = _dataPtrs[i],
                        dwBufferLength = BufferByteSize
                    };
                    Marshal.StructureToPtr(hdr, _headerPtrs[i], fDeleteOld: false);
                    int prepRc = waveInPrepareHeader(_hWaveIn, _headerPtrs[i], hdrSize);
                    if (prepRc != 0) throw new InvalidOperationException($"waveInPrepareHeader failed ({prepRc})");
                    int addRc = waveInAddBuffer(_hWaveIn, _headerPtrs[i], hdrSize);
                    if (addRc != 0) throw new InvalidOperationException($"waveInAddBuffer failed ({addRc})");
                }

                int startRc = waveInStart(_hWaveIn);
                if (startRc != 0) throw new InvalidOperationException($"waveInStart failed ({startRc})");
                _isRecording = true;
                _pollCts = new CancellationTokenSource();
                var token = _pollCts.Token;
                _pollTask = Task.Run(() => PollWaveInLoop(token), token);
                return (true, null);
            }
            catch (Exception ex)
            {
                CleanupWaveInLocked();
                return (false, ex.Message);
            }
        }
    }

    private void PollWaveInLoop(CancellationToken ct)
    {
        byte[] temp = new byte[BufferByteSize];
        uint hdrSize = (uint)Marshal.SizeOf<WAVEHDR>();

        while (!ct.IsCancellationRequested)
        {
            bool anyDone = false;
            double? pendingLevel = null;

            lock (_recLock)
            {
                if (!_isRecording || _hWaveIn == IntPtr.Zero || _headerPtrs is null || _pcmStream is null)
                    break;

                // Strict FIFO ring traversal starting at _nextBufferIndex preserves exact chronological audio order
                for (int step = 0; step < _headerPtrs.Length; step++)
                {
                    int idx = _nextBufferIndex;
                    IntPtr hdrPtr = _headerPtrs[idx];
                    if (hdrPtr == IntPtr.Zero) break;

                    var hdr = Marshal.PtrToStructure<WAVEHDR>(hdrPtr);
                    if ((hdr.dwFlags & WHDR_DONE) == 0)
                        break;

                    anyDone = true;
                    _nextBufferIndex = (idx + 1) % _headerPtrs.Length;

                    int recorded = (int)Math.Min(hdr.dwBytesRecorded, (uint)temp.Length);
                    if (recorded > 0)
                    {
                        Marshal.Copy(hdr.lpData, temp, 0, recorded);
                        // Cap max voice recording at ~90 seconds (~2.8 MB RAM)
                        if (_pcmStream.Length < SampleRate * 2 * 90)
                            _pcmStream.Write(temp, 0, recorded);

                        double rms = ComputePcm16Rms(temp, recorded);
                        pendingLevel = Math.Clamp(rms * 5.5, 0.0, 1.0);
                    }

                    waveInUnprepareHeader(_hWaveIn, hdrPtr, hdrSize);
                    var cleanHdr = new WAVEHDR
                    {
                        lpData = hdr.lpData,
                        dwBufferLength = BufferByteSize
                    };
                    Marshal.StructureToPtr(cleanHdr, hdrPtr, fDeleteOld: false);
                    if (waveInPrepareHeader(_hWaveIn, hdrPtr, hdrSize) == 0)
                        waveInAddBuffer(_hWaveIn, hdrPtr, hdrSize);
                }
            }

            if (pendingLevel.HasValue)
                LevelChanged?.Invoke(pendingLevel.Value);

            if (!anyDone)
                Thread.Sleep(25);
        }
    }

    private static double ComputePcm16Rms(byte[] pcm, int byteLength)
    {
        int samples = byteLength / 2;
        if (samples <= 0) return 0.0;
        double sumSq = 0;
        for (int i = 0; i < samples * 2; i += 2)
        {
            short s = (short)(pcm[i] | (pcm[i + 1] << 8));
            double norm = s / 32768.0;
            sumSq += norm * norm;
        }
        return Math.Sqrt(sumSq / samples);
    }

    public async Task<(bool Success, string Text, string? Error)> StopAndTranscribeAsync(CancellationToken ct = default)
    {
        byte[]? pcmBytes;
        Task? pollToWait;

        lock (_recLock)
        {
            if (!_isRecording)
                return (false, string.Empty, LocalizationService.Pick("Recording was not active.", "Запись не велась."));

            _isRecording = false;
            _pollCts?.Cancel();
            pollToWait = _pollTask;
        }

        if (pollToWait is not null)
        {
            try { await pollToWait.ConfigureAwait(false); } catch { }
        }

        lock (_recLock)
        {
            // Flush any partial buffers filled right before stop in strict FIFO ring order
            if (_hWaveIn != IntPtr.Zero && _headerPtrs is not null && _pcmStream is not null)
            {
                waveInReset(_hWaveIn);
                byte[] temp = new byte[BufferByteSize];
                for (int step = 0; step < _headerPtrs.Length; step++)
                {
                    IntPtr hdrPtr = _headerPtrs[(_nextBufferIndex + step) % _headerPtrs.Length];
                    if (hdrPtr == IntPtr.Zero) continue;
                    var hdr = Marshal.PtrToStructure<WAVEHDR>(hdrPtr);
                    int recorded = (int)Math.Min(hdr.dwBytesRecorded, (uint)temp.Length);
                    if (recorded > 0)
                    {
                        Marshal.Copy(hdr.lpData, temp, 0, recorded);
                        _pcmStream.Write(temp, 0, recorded);
                    }
                }
            }

            pcmBytes = _pcmStream?.ToArray();
            CleanupWaveInLocked();
        }

        LevelChanged?.Invoke(0.0);

        if (pcmBytes is null || pcmBytes.Length < SampleRate * 2 / 4) // < 0.25s
            return (false, string.Empty, LocalizationService.Pick("Recording too short — speak a bit longer.", "Слишком короткая запись — удерживайте микрофон чуть дольше."));

        string? exePath = FindWhisperExePath();
        if (string.IsNullOrWhiteSpace(exePath) || !IsModelInstalled())
            return (false, string.Empty, LocalizationService.Pick(
                "Whisper voice dictation module is not downloaded. Open Settings → \"Translator & AI\" and click \"Download\" next to Whisper.",
                "Модуль голосовой диктовки Whisper не скачан. Откройте Настройки → «Переводчик и ИИ» и нажмите «Скачать Whisper»."));

        string tempDir = Path.Combine(PortablePaths.CacheDirectory, "temp");
        Directory.CreateDirectory(tempDir);
        string wavPath = Path.Combine(tempDir, $"voice_{Guid.NewGuid():N}.wav");

        try
        {
            WriteWav16Mono(wavPath, pcmBytes, SampleRate);

            int threads = Math.Clamp(Environment.ProcessorCount - 1, 2, 8);
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = WhisperDirectory,
                Arguments = $"-m \"{ModelFilePath}\" -f \"{wavPath}\" -l auto -t {threads} -nt -np",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.Environment["TEMP"] = tempDir;
            psi.Environment["TMP"] = tempDir;

            using var proc = Process.Start(psi);
            if (proc is null)
                return (false, string.Empty, LocalizationService.Pick("Failed to launch whisper-cli.exe.", "Не удалось запустить whisper-cli.exe."));

            var outTask = proc.StandardOutput.ReadToEndAsync(ct);
            var errTask = proc.StandardError.ReadToEndAsync(ct);
            try
            {
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); proc.WaitForExit(500); } catch { }
                throw;
            }

            string rawOut = await outTask.ConfigureAwait(false);
            _ = await errTask.ConfigureAwait(false);

            string cleaned = CleanWhisperTranscript(rawOut);
            if (string.IsNullOrWhiteSpace(cleaned))
                return (false, string.Empty, LocalizationService.Pick("No speech recognized (try speaking louder).", "Речь не распознана (попробуйте сказать громче)."));

            return (true, cleaned, null);
        }
        catch (OperationCanceledException)
        {
            return (false, string.Empty, LocalizationService.Pick("Transcription cancelled.", "Расшифровка отменена."));
        }
        catch (Exception ex)
        {
            LogService.Warn($"WhisperVoiceService transcription error: {ex.Message}");
            return (false, string.Empty, ex.Message);
        }
        finally
        {
            try { if (File.Exists(wavPath)) File.Delete(wavPath); } catch { }
        }
    }

    internal static string CleanWhisperTranscript(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var lines = raw.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        foreach (string line in lines)
        {
            string t = line.Trim();
            if (string.IsNullOrEmpty(t)) continue;
            // Filter Whisper silence hallucinations like "[BLANK_AUDIO]", "(музыка)", "[Music]"
            if ((t.StartsWith('[') && t.EndsWith(']')) || (t.StartsWith('(') && t.EndsWith(')')))
                continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(t);
        }
        return sb.ToString().Trim();
    }

    internal static void WriteWav16Mono(string path, byte[] pcmBytes, int sampleRate)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs);
        int byteRate = sampleRate * 2;
        bw.Write("RIFF"u8);
        bw.Write(36 + pcmBytes.Length);
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16);
        bw.Write((short)1); // PCM
        bw.Write((short)1); // Mono
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write((short)2); // BlockAlign
        bw.Write((short)16); // BitsPerSample
        bw.Write("data"u8);
        bw.Write(pcmBytes.Length);
        bw.Write(pcmBytes);
    }

    private void CleanupWaveInLocked()
    {
        uint hdrSize = (uint)Marshal.SizeOf<WAVEHDR>();
        if (_hWaveIn != IntPtr.Zero)
        {
            try { waveInReset(_hWaveIn); } catch { }
            if (_headerPtrs is not null)
            {
                foreach (IntPtr hdrPtr in _headerPtrs)
                {
                    if (hdrPtr != IntPtr.Zero)
                    {
                        try { waveInUnprepareHeader(_hWaveIn, hdrPtr, hdrSize); } catch { }
                    }
                }
            }
            try { waveInClose(_hWaveIn); } catch { }
            _hWaveIn = IntPtr.Zero;
        }

        if (_headerPtrs is not null)
        {
            foreach (IntPtr p in _headerPtrs)
                if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
            _headerPtrs = null;
        }

        if (_dataPtrs is not null)
        {
            foreach (IntPtr p in _dataPtrs)
                if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
            _dataPtrs = null;
        }

        _pcmStream?.Dispose();
        _pcmStream = null;
        _pollCts?.Dispose();
        _pollCts = null;
        _pollTask = null;
        _isRecording = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_recLock)
        {
            _pollCts?.Cancel();
            CleanupWaveInLocked();
        }
    }
}
