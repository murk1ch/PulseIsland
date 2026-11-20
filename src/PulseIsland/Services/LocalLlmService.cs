using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PulseIsland.Core;

namespace PulseIsland.Services;

/// <summary>
/// Manages local Qwen 2.5 3B (GGUF) inference via portable llama-server.exe (Vulkan GPU or CPU).
/// Automatically falls back from Vulkan GPU to CPU if Vulkan initialization fails,
/// streams tokens in real time via SSE, and reports active backend + generation speed.
/// </summary>
public sealed class LocalLlmService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(3) };
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    private Process? _serverProcess;
    private int _serverPort;
    private bool _activeUseGpu;
    private AiComputeBackend _startedBackend;
    private bool _disposed;

    public string ActiveBackendLabel { get; private set; } = "Idle";

    public async Task<(bool Ready, string BackendLabel, string? Error)> EnsureStartedAsync(
        AiComputeBackend backend,
        CancellationToken ct = default)
    {
        if (!LocalLlmCatalogService.IsRuntimeInstalled() || !LocalLlmCatalogService.IsModelInstalled())
        {
            return (false,
                LocalizationService.Pick("Not downloaded", "Не скачана"),
                LocalizationService.Pick("Download the Qwen 2.5 3B AI model in Settings first.", "Сначала скачайте ИИ-модель Qwen 2.5 3B в Настройках."));
        }

        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_serverProcess is not null && !_serverProcess.HasExited && _startedBackend == backend)
            {
                return (true, ActiveBackendLabel, null);
            }

            StopServerLocked();

            var (_, hasGpu) = LocalLlmCatalogService.DetectHardware();
            bool tryGpuFirst = backend switch
            {
                AiComputeBackend.GpuVulkan => true,
                AiComputeBackend.CpuOnly => false,
                _ => hasGpu
            };

            if (tryGpuFirst)
            {
                if (await TryLaunchAndWaitHealthAsync(useGpu: true, ct).ConfigureAwait(false))
                {
                    _activeUseGpu = true;
                    _startedBackend = backend;
                    ActiveBackendLabel = "GPU • Vulkan";
                    return (true, ActiveBackendLabel, null);
                }

                // Auto fallback to CPU if GPU Vulkan failed to initialize
                if (backend == AiComputeBackend.Auto)
                {
                    LogService.Info("LocalLlmService: GPU Vulkan init failed; falling back automatically to CPU.");
                    StopServerLocked();
                }
                else
                {
                    return (false,
                        LocalizationService.Pick("GPU error", "Ошибка GPU"),
                        LocalizationService.Pick(
                            "Failed to start Qwen 3B on Vulkan GPU. Try \"Auto\" or \"CPU only\" in Settings.",
                            "Не удалось запустить Qwen 3B на GPU Vulkan. Попробуйте режим «Авто» или «Только CPU» в Настройках."));
                }
            }

            if (await TryLaunchAndWaitHealthAsync(useGpu: false, ct).ConfigureAwait(false))
            {
                _activeUseGpu = false;
                _startedBackend = backend;
                ActiveBackendLabel = $"CPU • {Math.Max(2, Environment.ProcessorCount - 1)}T";
                return (true, ActiveBackendLabel, null);
            }

            return (false,
                LocalizationService.Pick("Error", "Ошибка"),
                LocalizationService.Pick("Failed to start local Qwen 2.5 3B server.", "Не удалось запустить локальный сервер Qwen 2.5 3B."));
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task<bool> TryLaunchAndWaitHealthAsync(bool useGpu, CancellationToken ct)
    {
        int port = FindFreePort();
        int threads = Math.Max(2, Math.Min(Environment.ProcessorCount, Environment.ProcessorCount - 1));
        int gpuLayers = useGpu ? 99 : 0;

        string args = useGpu
            ? $"-m \"{LocalLlmCatalogService.ModelFilePath}\" -c 4096 -ngl {gpuLayers} --host 127.0.0.1 --port {port} --no-webui"
            : $"-m \"{LocalLlmCatalogService.ModelFilePath}\" -c 4096 -ngl 0 -t {threads} --device none --host 127.0.0.1 --port {port} --no-webui";

        var psi = new ProcessStartInfo
        {
            FileName = LocalLlmCatalogService.LlamaServerExePath,
            WorkingDirectory = LocalLlmCatalogService.LlamaRuntimeDirectory,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var proc = Process.Start(psi);
        if (proc is null) return false;

        _serverProcess = proc;
        _serverPort = port;

        // Drain stdout/stderr asynchronously so buffers never block llama-server
        _ = Task.Run(async () => { try { await proc.StandardOutput.ReadToEndAsync(); } catch { } });
        _ = Task.Run(async () => { try { await proc.StandardError.ReadToEndAsync(); } catch { } });

        string healthUrl = $"http://127.0.0.1:{port}/health";
        var deadline = DateTime.UtcNow.AddSeconds(45);

        while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            if (proc.HasExited)
                return false;

            try
            {
                using var resp = await _http.GetAsync(healthUrl, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                    return true;
            }
            catch { }

            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        return false;
    }

    public async Task<(bool Success, string FullReply, double TokensPerSec, string? Error)> StreamChatAsync(
        AiComputeBackend backend,
        string systemPrompt,
        string userPrompt,
        Action<string> onToken,
        CancellationToken ct = default)
    {
        var (ready, _, startErr) = await EnsureStartedAsync(backend, ct).ConfigureAwait(false);
        if (!ready)
            return (false, string.Empty, 0, startErr);

        string url = $"http://127.0.0.1:{_serverPort}/v1/chat/completions";
        var payload = new
        {
            model = LocalLlmCatalogService.DefaultModelId,
            stream = true,
            temperature = 0.45,
            max_tokens = 420,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            }
        };

        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

        var sw = Stopwatch.StartNew();
        int tokenCount = 0;
        var full = new StringBuilder();

        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            while (!ct.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

                string data = line[5..].Trim();
                if (data == "[DONE]") break;

                try
                {
                    using var doc = JsonDocument.Parse(data);
                    if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                        choices.GetArrayLength() > 0 &&
                        choices[0].TryGetProperty("delta", out var delta) &&
                        delta.TryGetProperty("content", out var contentEl))
                    {
                        string? piece = contentEl.GetString();
                        if (!string.IsNullOrEmpty(piece))
                        {
                            full.Append(piece);
                            tokenCount++;
                            onToken(piece);
                        }
                    }
                }
                catch { }
            }

            sw.Stop();
            double tokSec = sw.Elapsed.TotalSeconds > 0.05 ? tokenCount / sw.Elapsed.TotalSeconds : 0.0;
            return (true, full.ToString().Trim(), tokSec, null);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            double tokSec = sw.Elapsed.TotalSeconds > 0.05 ? tokenCount / sw.Elapsed.TotalSeconds : 0.0;
            return (true, full.ToString().Trim(), tokSec, null);
        }
        catch (Exception ex)
        {
            LogService.Error("LocalLlmService StreamChatAsync error.", ex);
            return (false, full.ToString().Trim(), 0, ex.Message);
        }
    }

    public static (string systemPrompt, string userPrompt) BuildWeatherPrompts(WeatherSnapshot weather, string? customQuestion = null)
    {
        string ctx = WeatherService.FormatWeatherBrief(weather);
        string sys = LocalizationService.Pick(
            "You are a friendly desktop AI assistant. Answer briefly and practically in English based on the exact weather data.",
            "Ты дружелюбный ИИ-ассистент на рабочем столе. Отвечай кратко, по-русски и по делу на основе точных погодных данных.");
        string user = string.IsNullOrWhiteSpace(customQuestion)
            ? LocalizationService.Pick(
                $"Current weather: {ctx}. Summarize in 1–2 short sentences what the weather is like today and what to wear.",
                $"Актуальная погода: {ctx}. Расскажи кратко в 1–2 предложениях, какая сегодня погода и как лучше одеться.")
            : LocalizationService.Pick(
                $"Current weather: {ctx}.\nUser question: {customQuestion}",
                $"Актуальная погода: {ctx}.\nВопрос пользователя: {customQuestion}");
        return (sys, user);
    }

    public static (string systemPrompt, string userPrompt) BuildSummaryPrompts(string text)
    {
        string trimmed = text.Length > 3500 ? text[..3500] : text;
        string sys = LocalizationService.Pick(
            "You are a concise assistant. Summarize the main points in 2–4 short bullet points in English without fluff.",
            "Ты лаконичный ассистент. Перескажи смысл текста на русском языке в 2–4 коротких пунктах без воды.");
        string user = LocalizationService.Pick(
            $"Briefly summarize the key points of the following text:\n\n{trimmed}",
            $"Кратко перескажи главный смысл следующего текста:\n\n{trimmed}");
        return (sys, user);
    }

    public static (string systemPrompt, string userPrompt) BuildTranslatePrompts(string text, string targetLangName)
    {
        string trimmed = text.Length > 3500 ? text[..3500] : text;
        string sys = LocalizationService.Pick(
            $"You are a professional translator. Translate the text into {targetLangName}, preserving meaning and natural tone. Output only the translation without extra commentary.",
            $"Ты профессиональный переводчик. Переведи текст на {targetLangName}, сохраняя смысл и естественный стиль. Выведи только готовый перевод без лишних пояснений.");
        return (sys, trimmed);
    }

    public void StopServer()
    {
        _lifecycleLock.Wait();
        try
        {
            StopServerLocked();
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private void StopServerLocked()
    {
        if (_serverProcess is not null)
        {
            try
            {
                if (!_serverProcess.HasExited)
                    _serverProcess.Kill(entireProcessTree: true);
            }
            catch { }
            try { _serverProcess.Dispose(); } catch { }
            _serverProcess = null;
        }
        _serverPort = 0;
        _ = _activeUseGpu;
    }

    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopServer();
        _lifecycleLock.Dispose();
        _http.Dispose();
    }
}
