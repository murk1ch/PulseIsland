using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PulseIsland.Core;
using PulseIsland.Services;

namespace PulseIsland.Translation;

public sealed class OnlineTranslationService : IDisposable
{
    private readonly HttpClient _httpClient;

    public OnlineTranslationService()
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(4)
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
    }

    public async Task<TranslationResult> TranslateAsync(
        string text,
        AppSettings settings,
        CancellationToken ct = default,
        bool allowBuiltInDictionaryFallback = true)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new TranslationResult(false, string.Empty, "und", settings.TargetLanguage, false, "Empty text.");

        string sourceLang = settings.AutoDetectSourceLanguage ? "auto" : settings.ManualSourceLanguage;
        string targetLang = string.IsNullOrWhiteSpace(settings.TargetLanguage) ? "ru" : settings.TargetLanguage;

        if (settings.OnlineProvider == OnlineTranslationProvider.DeepL)
        {
            string apiKey = SecurityHelper.UnprotectString(settings.EncryptedDeepLApiKey);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                var deepLResult = await TranslateViaDeepLAsync(text, sourceLang, targetLang, apiKey, ct);
                if (deepLResult.Success)
                    return deepLResult;
            }
            LogService.Warn("DeepL provider unavailable or API key empty. Falling back to Google/MyMemory/Local.");
        }

        var googleResult = await TranslateViaGoogleFreeAsync(text, sourceLang, targetLang, ct);
        if (googleResult.Success)
            return googleResult;

        var myMemoryResult = await TranslateViaMyMemoryAsync(text, sourceLang, targetLang, ct);
        if (myMemoryResult.Success)
            return myMemoryResult;

        if (!allowBuiltInDictionaryFallback)
        {
            return new TranslationResult(
                false,
                string.Empty,
                sourceLang,
                targetLang,
                false,
                googleResult.ErrorMessage ?? LocalizationService.Pick("Online translator is unavailable.", "Онлайн-переводчик недоступен."));
        }

        // If outbound sockets are blocked by firewall/sandbox or user is offline,
        // automatically fall back to the built-in local dictionary/phrase engine so translation still works!
        LogService.Info("Online endpoints unreachable (offline or socket restricted). Using BuiltInLocalTranslator fallback.");
        return BuiltInLocalTranslator.Translate(text, sourceLang, targetLang);
    }

    private async Task<TranslationResult> TranslateViaGoogleFreeAsync(
        string text,
        string sourceLang,
        string targetLang,
        CancellationToken ct)
    {
        string[] endpoints =
        {
            "https://translate.googleapis.com/translate_a/single?client=gtx&dt=t",
            "https://clients5.google.com/translate_a/single?client=at&dt=t"
        };

        foreach (var endpoint in endpoints)
        {
            try
            {
                using var content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["sl"] = sourceLang,
                    ["tl"] = targetLang,
                    ["q"] = text
                });
                using var response = await _httpClient.PostAsync(endpoint, content, ct);
                if (!response.IsSuccessStatusCode)
                    continue;

                string json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var sb = new StringBuilder();
                if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 && root[0].ValueKind == JsonValueKind.Array)
                {
                    foreach (var segment in root[0].EnumerateArray())
                    {
                        if (segment.ValueKind == JsonValueKind.Array && segment.GetArrayLength() > 0)
                        {
                            sb.Append(segment[0].GetString());
                        }
                    }
                }

                string detectedLang = sourceLang;
                if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 2 && root[2].ValueKind == JsonValueKind.String)
                {
                    detectedLang = root[2].GetString() ?? sourceLang;
                }

                var translated = sb.ToString().Trim();
                if (!string.IsNullOrEmpty(translated))
                    return new TranslationResult(true, translated, detectedLang, targetLang, false);
            }
            catch (Exception ex)
            {
                LogService.Warn($"Google translation endpoint {endpoint} failed: {ex.Message}");
            }
        }

        return new TranslationResult(false, string.Empty, sourceLang, targetLang, false, "Google endpoints unreachable.");
    }

    private async Task<TranslationResult> TranslateViaMyMemoryAsync(
        string text,
        string sourceLang,
        string targetLang,
        CancellationToken ct)
    {
        try
        {
            string src = string.Equals(sourceLang, "auto", StringComparison.OrdinalIgnoreCase)
                ? (text.Any(c => c is >= 'А' and <= 'я') ? "ru" : "en")
                : sourceLang;

            if (string.Equals(src, targetLang, StringComparison.OrdinalIgnoreCase))
                return new TranslationResult(true, text, src, targetLang, false);

            string url = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(text)}&langpair={Uri.EscapeDataString(src)}|{Uri.EscapeDataString(targetLang)}";
            using var response = await _httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                return new TranslationResult(false, string.Empty, src, targetLang, false);

            string json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("responseData", out var respData) &&
                respData.TryGetProperty("translatedText", out var transProp))
            {
                string? translated = transProp.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(translated))
                    return new TranslationResult(true, translated, src, targetLang, false);
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"MyMemory translation fallback failed: {ex.Message}");
        }

        return new TranslationResult(false, string.Empty, sourceLang, targetLang, false);
    }

    private async Task<TranslationResult> TranslateViaDeepLAsync(
        string text,
        string sourceLang,
        string targetLang,
        string apiKey,
        CancellationToken ct)
    {
        try
        {
            bool isFreeKey = apiKey.EndsWith(":fx", StringComparison.OrdinalIgnoreCase);
            string endpoint = isFreeKey
                ? "https://api-free.deepl.com/v2/translate"
                : "https://api.deepl.com/v2/translate";

            var payload = new Dictionary<string, object>
            {
                ["text"] = new[] { text },
                ["target_lang"] = targetLang.ToUpperInvariant()
            };
            if (!string.Equals(sourceLang, "auto", StringComparison.OrdinalIgnoreCase))
            {
                payload["source_lang"] = sourceLang.ToUpperInvariant();
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", apiKey);

            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var first = doc.RootElement.GetProperty("translations")[0];
            string translated = first.GetProperty("text").GetString() ?? string.Empty;
            string detected = first.TryGetProperty("detected_source_language", out var d) ? (d.GetString() ?? sourceLang) : sourceLang;

            return new TranslationResult(true, translated.Trim(), detected.ToLowerInvariant(), targetLang, false);
        }
        catch (Exception ex)
        {
            LogService.Error("DeepL translation failed.", ex);
            return new TranslationResult(false, string.Empty, sourceLang, targetLang, false, ex.Message);
        }
    }

    public void Dispose() => _httpClient.Dispose();
}
