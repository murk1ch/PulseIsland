using System.IO;
using System.Net.Http;
using System.Text.Json;
using PulseIsland.Core;

namespace PulseIsland.Services;

/// <summary>
/// Fetches lightweight current weather from wttr.in (zero API key required)
/// and caches the latest snapshot strictly inside <portable-root>\cache\weather_cache.json
/// so the local LLM can still report recent weather when offline.
/// </summary>
public sealed class WeatherService
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private static string WeatherCachePath => Path.Combine(PortablePaths.CacheDirectory, "weather_cache.json");

    public async Task<WeatherSnapshot?> GetCurrentWeatherAsync(string? city = null, CancellationToken ct = default)
    {
        PortablePaths.EnsureDirectoriesExist();
        string cleanCity = city?.Trim() ?? string.Empty;

        try
        {
            string url = string.IsNullOrEmpty(cleanCity)
                ? "https://wttr.in/?format=j1&lang=ru"
                : $"https://wttr.in/{Uri.EscapeDataString(cleanCity)}?format=j1&lang=ru";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("PulseIsland/1.0");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var parsed = ParseWttrJson(json, cleanCity);
            if (parsed is not null)
            {
                try
                {
                    await File.WriteAllTextAsync(
                        WeatherCachePath,
                        JsonSerializer.Serialize(parsed, new JsonSerializerOptions { WriteIndented = true }),
                        ct).ConfigureAwait(false);
                }
                catch { }
                return parsed;
            }
        }
        catch (Exception ex)
        {
            LogService.Error("WeatherService online fetch failed; trying portable cache.", ex);
        }

        return TryLoadCachedWeather();
    }

    public static WeatherSnapshot? TryLoadCachedWeather()
    {
        try
        {
            if (!File.Exists(WeatherCachePath))
                return null;

            string json = File.ReadAllText(WeatherCachePath);
            var cached = JsonSerializer.Deserialize<WeatherSnapshot>(json);
            return cached is not null ? cached with { IsFromCache = true } : null;
        }
        catch
        {
            return null;
        }
    }

    public static WeatherSnapshot? ParseWttrJson(string json, string fallbackCity = "")
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("current_condition", out var ccArr) || ccArr.GetArrayLength() == 0)
                return null;

            var cc = ccArr[0];
            int tempC = ParseInt(cc, "temp_C");
            int feelsC = ParseInt(cc, "FeelsLikeC", tempC);
            int windKmh = ParseInt(cc, "windspeedKmph");
            int humidity = ParseInt(cc, "humidity");

            string condition = LocalizationService.Pick("Clear", "Ясно");
            if (LocalizationService.IsRussian &&
                cc.TryGetProperty("lang_ru", out var ruArr) && ruArr.GetArrayLength() > 0 &&
                ruArr[0].TryGetProperty("value", out var ruVal))
            {
                condition = ruVal.GetString()?.Trim() ?? condition;
            }
            else if (cc.TryGetProperty("weatherDesc", out var descArr) && descArr.GetArrayLength() > 0 &&
                     descArr[0].TryGetProperty("value", out var dVal))
            {
                condition = dVal.GetString()?.Trim() ?? condition;
            }
            else if (cc.TryGetProperty("lang_ru", out var ruFallback) && ruFallback.GetArrayLength() > 0 &&
                     ruFallback[0].TryGetProperty("value", out var ruFallbackVal))
            {
                condition = ruFallbackVal.GetString()?.Trim() ?? condition;
            }

            string city = fallbackCity;
            if (string.IsNullOrWhiteSpace(city) &&
                root.TryGetProperty("nearest_area", out var areaArr) && areaArr.GetArrayLength() > 0 &&
                areaArr[0].TryGetProperty("areaName", out var nameArr) && nameArr.GetArrayLength() > 0 &&
                nameArr[0].TryGetProperty("value", out var cityEl))
            {
                city = cityEl.GetString()?.Trim() ?? LocalizationService.Pick("Your city", "Ваш город");
            }
            if (string.IsNullOrWhiteSpace(city))
                city = LocalizationService.Pick("Current location", "Текущая локация");

            return new WeatherSnapshot(city, tempC, feelsC, condition, windKmh, humidity, DateTime.Now, IsFromCache: false);
        }
        catch
        {
            return null;
        }
    }

    public static string FormatWeatherBrief(WeatherSnapshot w)
    {
        string sign = w.TempC > 0 ? "+" : "";
        string feelSign = w.FeelsLikeC > 0 ? "+" : "";
        string cacheNote = w.IsFromCache
            ? LocalizationService.Pick($" (cached at {w.FetchedAt:HH:mm})", $" (кэш от {w.FetchedAt:HH:mm})")
            : "";
        return LocalizationService.Pick(
            $"{w.City}: {sign}{w.TempC}°C (feels like {feelSign}{w.FeelsLikeC}°C), {w.Condition}, wind {w.WindKmh} km/h, humidity {w.HumidityPct}%{cacheNote}",
            $"{w.City}: {sign}{w.TempC}°C (ощущается как {feelSign}{w.FeelsLikeC}°C), {w.Condition}, ветер {w.WindKmh} км/ч, влажность {w.HumidityPct}%{cacheNote}");
    }

    private static int ParseInt(JsonElement obj, string prop, int fallback = 0)
    {
        if (obj.TryGetProperty(prop, out var el))
        {
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int num))
                return num;
            if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out int parsed))
                return parsed;
        }
        return fallback;
    }
}
