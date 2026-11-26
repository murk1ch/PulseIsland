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
/// Manages local LLM & Vision (VLM) GGUF inference via portable llama-server.exe (Vulkan GPU or CPU).
/// Automatically passes --mmproj for multimodal Vision models, falls back from Vulkan GPU to CPU
/// if Vulkan initialization fails, streams tokens in real time via SSE, and reports active backend + speed.
/// </summary>
public sealed class LocalLlmService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(3) };
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    private Process? _serverProcess;
    private int _serverPort;
    private bool _activeUseGpu;
    private bool _isServerHealthy;
    private AiComputeBackend _startedBackend;
    private string _startedModelId = LocalLlmCatalogService.DefaultModelId;
    private string? _gpuFallbackModelId;
    private readonly Queue<string> _recentServerLogs = new(40);
    private bool _disposed;

    private string? _activeBackendLabel;
    public string ActiveBackendLabel
    {
        get => _activeBackendLabel ?? LocalizationService.Pick("Idle", "Ожидание");
        private set => _activeBackendLabel = value;
    }

    public async Task<(bool Ready, string BackendLabel, string? Error)> EnsureStartedAsync(
        AiComputeBackend backend,
        string? modelId = null,
        CancellationToken ct = default)
    {
        var entry = LocalLlmCatalogService.FindModel(modelId);
        if (!LocalLlmCatalogService.IsRuntimeInstalled() || !LocalLlmCatalogService.IsModelInstalled(entry.ModelId))
        {
            return (false,
                LocalizationService.Pick("Not downloaded", "Не скачана"),
                LocalizationService.Pick(
                    $"Model {entry.DisplayName} is not downloaded. Open Settings → \"Translator & AI\" and click \"Download\".",
                    $"Модель {entry.DisplayName} не скачана. Откройте Настройки → «Переводчик и ИИ» и нажмите «Скачать»."));
        }

        string modelFilePath = LocalLlmCatalogService.GetModelFilePath(entry.ModelId);
        string? mmprojFilePath = entry.SupportsVision ? LocalLlmCatalogService.GetMmprojFilePath(entry.ModelId) : null;

        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            bool matchesBackend = _startedBackend == backend ||
                (backend == AiComputeBackend.Auto &&
                 _startedBackend == AiComputeBackend.CpuOnly &&
                 string.Equals(_gpuFallbackModelId, entry.ModelId, StringComparison.OrdinalIgnoreCase));

            if (_isServerHealthy &&
                _serverProcess is not null &&
                !_serverProcess.HasExited &&
                matchesBackend &&
                string.Equals(_startedModelId, entry.ModelId, StringComparison.OrdinalIgnoreCase))
            {
                return (true, ActiveBackendLabel, null);
            }

            StopServerLocked();

            var (_, hasGpu) = LocalLlmCatalogService.DetectHardware();
            bool skipGpuForFallback = backend == AiComputeBackend.Auto &&
                string.Equals(_gpuFallbackModelId, entry.ModelId, StringComparison.OrdinalIgnoreCase);

            bool tryGpuFirst = backend switch
            {
                AiComputeBackend.GpuVulkan => true,
                AiComputeBackend.CpuOnly => false,
                _ => hasGpu && !skipGpuForFallback
            };

            if (tryGpuFirst)
            {
                if (await TryLaunchAndWaitHealthAsync(modelFilePath, mmprojFilePath, useGpu: true, ct).ConfigureAwait(false))
                {
                    _isServerHealthy = true;
                    _activeUseGpu = true;
                    _startedBackend = backend;
                    _startedModelId = entry.ModelId;
                    ActiveBackendLabel = entry.SupportsVision ? "GPU • Vulkan • Vision" : "GPU • Vulkan";
                    return (true, ActiveBackendLabel, null);
                }

                // Auto fallback to CPU if GPU Vulkan failed to initialize
                if (backend == AiComputeBackend.Auto)
                {
                    LogService.Info("LocalLlmService: GPU Vulkan init failed; falling back automatically to CPU.");
                    _gpuFallbackModelId = entry.ModelId;
                    StopServerLocked();
                }
                else
                {
                    return (false,
                        LocalizationService.Pick("GPU error", "Ошибка GPU"),
                        LocalizationService.Pick(
                            $"Failed to start {entry.ModelId} on Vulkan GPU. Try \"Auto\" or \"CPU only\" in Settings.",
                            $"Не удалось запустить {entry.ModelId} на GPU Vulkan. Попробуйте режим «Авто» или «Только CPU» в Настройках."));
                }
            }

            if (await TryLaunchAndWaitHealthAsync(modelFilePath, mmprojFilePath, useGpu: false, ct).ConfigureAwait(false))
            {
                _isServerHealthy = true;
                _activeUseGpu = false;
                _startedBackend = backend == AiComputeBackend.Auto ? AiComputeBackend.CpuOnly : backend;
                _startedModelId = entry.ModelId;
                int threads = Math.Max(2, Environment.ProcessorCount - 1);
                ActiveBackendLabel = entry.SupportsVision ? $"CPU • {threads}T • Vision" : $"CPU • {threads}T";
                return (true, ActiveBackendLabel, null);
            }

            return (false,
                LocalizationService.Pick("Error", "Ошибка"),
                LocalizationService.Pick(
                    $"Failed to start local server for {entry.ModelId}.",
                    $"Не удалось запустить локальный сервер {entry.ModelId}."));
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task<bool> TryLaunchAndWaitHealthAsync(string modelFilePath, string? mmprojFilePath, bool useGpu, CancellationToken ct)
    {
        int port = FindFreePort();
        int threads = Math.Max(2, Math.Min(Environment.ProcessorCount, Environment.ProcessorCount - 1));
        int gpuLayers = useGpu ? 99 : 0;

        bool hasMmproj = !string.IsNullOrWhiteSpace(mmprojFilePath) && File.Exists(mmprojFilePath);
        int ctxSize = hasMmproj ? 8192 : 4096;
        string mmprojArg = hasMmproj ? $" --mmproj \"{mmprojFilePath}\"" : string.Empty;

        string args = useGpu
            ? $"-m \"{modelFilePath}\"{mmprojArg} -c {ctxSize} -ngl {gpuLayers} --host 127.0.0.1 --port {port} --no-webui"
            : $"-m \"{modelFilePath}\"{mmprojArg} -c {ctxSize} -ngl 0 -t {threads} --device none --host 127.0.0.1 --port {port} --no-webui";

        string tempDir = Path.Combine(PortablePaths.CacheDirectory, "temp");
        Directory.CreateDirectory(tempDir);

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
        psi.Environment["TEMP"] = tempDir;
        psi.Environment["TMP"] = tempDir;

        var proc = Process.Start(psi);
        if (proc is null) return false;

        _serverProcess = proc;
        _serverPort = port;
        lock (_recentServerLogs) { _recentServerLogs.Clear(); }

        // Drain stdout/stderr asynchronously so buffers never block llama-server, keeping last lines for diagnostics
        _ = Task.Run(async () => { try { await proc.StandardOutput.ReadToEndAsync(); } catch { } });
        _ = Task.Run(async () =>
        {
            try
            {
                string? errLine;
                while ((errLine = await proc.StandardError.ReadLineAsync()) is not null)
                {
                    lock (_recentServerLogs)
                    {
                        if (_recentServerLogs.Count >= 35) _recentServerLogs.Dequeue();
                        _recentServerLogs.Enqueue(errLine);
                    }
                }
            }
            catch { }
        });

        string healthUrl = $"http://127.0.0.1:{port}/health";
        var deadline = DateTime.UtcNow.AddSeconds(hasMmproj ? 65 : 45);

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

    public Task<(bool Success, string FullReply, double TokensPerSec, string? Error)> StreamChatAsync(
        AiComputeBackend backend,
        string systemPrompt,
        string userPrompt,
        Action<string> onToken,
        CancellationToken ct = default,
        string? modelId = null,
        double temperature = 0.45,
        byte[]? imageJpegBytes = null)
    {
        IReadOnlyList<AiChatTurn> singleTurn = [new AiChatTurn("user", userPrompt, imageJpegBytes)];
        return StreamChatHistoryAsync(backend, systemPrompt, singleTurn, onToken, ct, modelId, temperature);
    }

    /// <summary>
    /// Streams a multi-turn conversation (preserving session memory for follow-up questions like
    /// "Объясни проще" or "Перепиши второй пункт") while trimming older turns to stay safely within context.
    /// </summary>
    public async Task<(bool Success, string FullReply, double TokensPerSec, string? Error)> StreamChatHistoryAsync(
        AiComputeBackend backend,
        string systemPrompt,
        IReadOnlyList<AiChatTurn> history,
        Action<string> onToken,
        CancellationToken ct = default,
        string? modelId = null,
        double temperature = 0.45)
    {
        if (history is null || history.Count == 0)
            return (false, string.Empty, 0, LocalizationService.Pick("Empty prompt.", "Пустой запрос."));

        var entry = LocalLlmCatalogService.FindModel(modelId);
        bool hasAnyImage = history.Any(t => t.ImageJpegBytes is { Length: > 0 });

        if (hasAnyImage && !entry.SupportsVision)
        {
            return (false, string.Empty, 0,
                LocalizationService.Pick(
                    $"Model {entry.DisplayName} is text-only and cannot read images. Select and download Qwen 2.5 VL 3B or Qwen 2.5 VL 7B (Vision) in Settings → \"Translator & AI\".",
                    $"Модель {entry.DisplayName} — чисто текстовая и не умеет распознавать фотографии. Выберите и скачайте Qwen 2.5 VL 3B или Qwen 2.5 VL 7B (Vision) в Настройках → «Переводчик и ИИ»."));
        }

        var sw = Stopwatch.StartNew();
        int tokenCount = 0;
        var full = new StringBuilder();

        try
        {
            var (ready, _, startErr) = await EnsureStartedAsync(backend, entry.ModelId, ct).ConfigureAwait(false);
            if (!ready)
                return (false, string.Empty, 0, startErr);

            // Find latest image payload across the entire conversation so Vision image is NEVER dropped after 5+ turns
            byte[]? latestImageBytes = null;
            int latestImageIndex = -1;
            for (int i = history.Count - 1; i >= 0; i--)
            {
                if (history[i].ImageJpegBytes is { Length: > 0 })
                {
                    latestImageIndex = i;
                    latestImageBytes = history[i].ImageJpegBytes;
                    break;
                }
            }

            var trimmedTurns = new List<(AiChatTurn Turn, bool IncludeImage)>();
            int charBudget = 6200;
            for (int i = history.Count - 1; i >= 0; i--)
            {
                var t = history[i];
                int len = t.Content?.Length ?? 0;
                if (trimmedTurns.Count >= 2 && charBudget - len < 0)
                    break;

                charBudget -= len;
                trimmedTurns.Add((t, i == latestImageIndex));
                if (trimmedTurns.Count >= 10)
                    break;
            }
            trimmedTurns.Reverse();

            // Guarantee strict [system -> user -> assistant -> ...] role alternation (required by Gemma-2 & Qwen templates)
            while (trimmedTurns.Count > 1 &&
                   !string.Equals(trimmedTurns[0].Turn.Role, "user", StringComparison.OrdinalIgnoreCase))
            {
                trimmedTurns.RemoveAt(0);
            }

            // If the original Vision image turn was trimmed out of the window, attach the image to the first included user turn
            if (latestImageBytes is { Length: > 0 } && !trimmedTurns.Any(x => x.IncludeImage))
            {
                int firstUserIdx = trimmedTurns.FindIndex(x =>
                    string.Equals(x.Turn.Role, "user", StringComparison.OrdinalIgnoreCase));
                if (firstUserIdx >= 0)
                {
                    var u = trimmedTurns[firstUserIdx].Turn;
                    trimmedTurns[firstUserIdx] = (u with { ImageJpegBytes = latestImageBytes }, true);
                }
            }

            string url = $"http://127.0.0.1:{_serverPort}/v1/chat/completions";
            var msgList = new List<object>(trimmedTurns.Count + 1)
            {
                new { role = "system", content = systemPrompt }
            };

            foreach (var (turn, includeImage) in trimmedTurns)
            {
                string role = string.Equals(turn.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : "user";
                string text = turn.Content ?? string.Empty;
                if (text.Length > 3800)
                    text = text[..3800];

                if (includeImage && turn.ImageJpegBytes is { Length: > 0 } && role == "user")
                {
                    string dataUri = "data:image/jpeg;base64," + Convert.ToBase64String(turn.ImageJpegBytes);
                    msgList.Add(new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new { type = "image_url", image_url = new { url = dataUri } },
                            new { type = "text", text }
                        }
                    });
                }
                else
                {
                    msgList.Add(new { role, content = text });
                }
            }

            object[] messages = msgList.ToArray();

            var payload = new
            {
                model = entry.ModelId,
                stream = true,
                temperature,
                max_tokens = 768,
                messages
            };

            var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };

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
            string tailLogs;
            lock (_recentServerLogs)
                tailLogs = string.Join(" | ", _recentServerLogs.TakeLast(6));

            LogService.Error($"LocalLlmService StreamChatAsync error. ServerTail: {tailLogs}", ex);

            // If GPU Vulkan server crashed mid-request (e.g. VRAM OOM on 6GB GPU), auto-fallback to CPU and remember it
            if (backend == AiComputeBackend.Auto && _activeUseGpu && tokenCount == 0 && !ct.IsCancellationRequested)
            {
                LogService.Info("LocalLlmService: GPU server dropped connection; retrying request on CPU fallback.");
                _gpuFallbackModelId = entry.ModelId;
                StopServer();
                return await StreamChatHistoryAsync(
                    AiComputeBackend.CpuOnly,
                    systemPrompt,
                    history,
                    onToken,
                    ct,
                    modelId,
                    temperature).ConfigureAwait(false);
            }

            return (false, full.ToString().Trim(), 0, ex.Message);
        }
    }

    public async Task<(bool Success, string TranslatedText, string? Error)> TranslateTextAsync(
        string text,
        string targetLangName,
        AiComputeBackend backend,
        string? modelId = null,
        CancellationToken ct = default)
    {
        var (sys, usr) = BuildTranslatePrompts(text, targetLangName);
        var (ok, reply, _, err) = await StreamChatAsync(
            backend,
            sys,
            usr,
            _ => { },
            ct,
            modelId,
            temperature: 0.2).ConfigureAwait(false);

        string cleaned = AiResponseFormatter.StripThinkingTags(reply).Trim();
        if (!ok || string.IsNullOrWhiteSpace(cleaned))
            return (false, string.Empty, err ?? LocalizationService.Pick("Local AI model did not return a translation.", "Локальная ИИ-модель не вернула перевод."));

        return (true, cleaned, null);
    }

    /// <summary>
    /// High-accuracy offline OCR via local Vision LLM (Qwen 2.5 VL).
    /// Handles handwriting, cursive, math formulas (fractions, Greek letters), stylized fonts, and °C/°F symbols.
    /// </summary>
    public async Task<(bool Success, string ExtractedText, string? Error)> ExtractTextFromImageAsync(
        byte[] imageJpegBytes,
        AiComputeBackend backend,
        string visionModelId,
        CancellationToken ct = default)
    {
        if (imageJpegBytes is null || imageJpegBytes.Length == 0)
            return (false, string.Empty, LocalizationService.Pick("Image is empty.", "Изображение пустое."));

        string sys = LocalizationService.Pick(
            "You are a high-accuracy offline OCR engine. Extract all text and mathematical formulas from the image as accurately as possible (including handwriting, cursive, fractions, degree signs °C/°F, and Greek letters). Do not translate, explain, or add any commentary — output ONLY the recognized text/formula. If there is no text at all, reply with exactly: [EMPTY].",
            "Ты высокоточный оффлайн OCR-движок. Извлеки весь текст и математические формулы с изображения максимально точно (включая рукописный текст, пропись, дроби, знаки градусов °C/°F, греческие буквы). Не переводи, не объясняй и не добавляй никаких комментариев — выведи ТОЛЬКО распознанный текст/формулу. Если текста нет вообще, ответь ровно: [EMPTY].");
        string usr = LocalizationService.Pick(
            "Recognize and accurately transcribe all text and formulas from this image.",
            "Распознай и точно выпиши весь текст и формулы с этого изображения.");

        var (ok, reply, _, err) = await StreamChatAsync(
            backend,
            sys,
            usr,
            _ => { },
            ct,
            visionModelId,
            temperature: 0.05,
            imageJpegBytes: imageJpegBytes).ConfigureAwait(false);

        if (!ok || string.IsNullOrWhiteSpace(reply))
            return (false, string.Empty, err ?? LocalizationService.Pick("Vision model could not recognize the image.", "Vision-модель не смогла распознать изображение."));

        string cleaned = AiResponseFormatter.FormatToReadablePlainText(reply).Trim();
        if (string.IsNullOrWhiteSpace(cleaned) ||
            cleaned.Equals("[EMPTY]", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Equals("EMPTY", StringComparison.OrdinalIgnoreCase))
        {
            return (false, string.Empty, LocalizationService.Pick("No text found in image.", "Текст на изображении не найден."));
        }

        return (true, cleaned, null);
    }

    public static (string systemPrompt, string userPrompt) BuildWeatherPrompts(WeatherSnapshot weather, string? customQuestion = null)
    {
        string ctx = WeatherService.FormatWeatherBrief(weather);
        string sys = LocalizationService.Pick(
            "You are a friendly desktop AI assistant. Answer briefly and practically in English based on the exact weather data. If the user writes in another language, reply in that language.",
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
        string usr = LocalizationService.Pick(
            $"Briefly summarize the key points of the following text:\n\n{trimmed}",
            $"Кратко перескажи главный смысл следующего текста:\n\n{trimmed}");
        return (sys, usr);
    }

    public static (string systemPrompt, string userPrompt) BuildTranslatePrompts(string text, string targetLangName)
    {
        string trimmed = text.Length > 3500 ? text[..3500] : text;
        string sys = LocalizationService.Pick(
            $"You are a professional translator. Translate the text into {targetLangName}, preserving meaning and natural tone. Output only the translation without extra commentary.",
            $"Ты профессиональный переводчик. Переведи текст на {targetLangName}, сохраняя смысл и естественный стиль. Выведи только готовый перевод без лишних пояснений.");
        return (sys, trimmed);
    }

    public static (string systemPrompt, string userPrompt) BuildImageQuestionPrompts(string? question, string? ocrHint = null)
    {
        string sys = LocalizationService.Pick(
            "You are a smart, attentive desktop AI assistant with computer vision. Answer accurately, clearly, and concisely in English (or in the user's language if they ask in another language). Format any math formulas cleanly and clearly.",
            "Ты умный и внимательный ИИ-ассистент с компьютерным зрением. Отвечай по-русски, точно, понятно и по делу. Если в ответе есть формулы, пиши их аккуратно и понятно.");
        string q = string.IsNullOrWhiteSpace(question)
            ? LocalizationService.Pick(
                "Clearly describe what is shown in this screenshot / photo. If there is text or a problem in the image, explain it.",
                "Подробно и понятно опиши, что изображено на этом снимке экрана / фото. Если на изображении есть текст или задача, объясни их.")
            : question.Trim();

        if (!string.IsNullOrWhiteSpace(ocrHint))
        {
            string trimmedOcr = ocrHint.Length > 1500 ? ocrHint[..1500] : ocrHint;
            return (sys, LocalizationService.Pick(
                $"{q}\n\n(Additional OCR hint from image: {trimmedOcr})",
                $"{q}\n\n(Дополнительная подсказка OCR с изображения: {trimmedOcr})"));
        }

        return (sys, q);
    }

    public static (string systemPrompt, string userPrompt) BuildTableExtractPrompts(string? ocrHint = null)
    {
        string sys = LocalizationService.Pick(
            "You are an accurate table and structured data parser. Extract the table from the image (or text) into a clean Markdown table, and below it provide a compact CSV block (semicolon ';' delimiter) ready to paste into Excel or Notion. Do not add extra fluff.",
            "Ты точный парсер таблиц и структурированных данных. Извлеки таблицу с изображения (или из текста) в чистую таблицу Markdown, а ниже приведи компактный блок CSV (разделитель ';'), готовый для вставки в Excel или Notion. Не добавляй лишней воды.");
        string usr = string.IsNullOrWhiteSpace(ocrHint)
            ? LocalizationService.Pick(
                "Extract the table from this screenshot in Markdown and CSV format.",
                "Извлеки таблицу с этого скриншота в формате Markdown и CSV.")
            : LocalizationService.Pick(
                $"Extract the table from this screenshot in Markdown and CSV format.\n(OCR hint: {(ocrHint.Length > 1500 ? ocrHint[..1500] : ocrHint)})",
                $"Извлеки таблицу с этого скриншота в формате Markdown и CSV.\n(Подсказка OCR: {(ocrHint.Length > 1500 ? ocrHint[..1500] : ocrHint)})");
        return (sys, usr);
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
        _isServerHealthy = false;
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
