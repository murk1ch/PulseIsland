using System.IO;
using System.Text.Json;
using PulseIsland.Core;

namespace PulseIsland.Services;

/// <summary>
/// Persists recent translations strictly inside <portable-root>\config\translation_history.json.
/// </summary>
public static class TranslationHistoryService
{
    private const int MaxEntries = 30;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static IReadOnlyList<TranslationRecord> Load()
    {
        try
        {
            PortablePaths.EnsureDirectoriesExist();
            if (!File.Exists(PortablePaths.TranslationHistoryFilePath))
                return Array.Empty<TranslationRecord>();

            string json = File.ReadAllText(PortablePaths.TranslationHistoryFilePath);
            return JsonSerializer.Deserialize<List<TranslationRecord>>(json, JsonOptions) ?? new List<TranslationRecord>();
        }
        catch (Exception ex)
        {
            LogService.Warn($"Failed to load translation history: {ex.Message}");
            return Array.Empty<TranslationRecord>();
        }
    }

    public static void Save(IEnumerable<TranslationRecord> records)
    {
        try
        {
            PortablePaths.EnsureDirectoriesExist();
            var trimmed = records.Take(MaxEntries).ToList();
            string json = JsonSerializer.Serialize(trimmed, JsonOptions);
            File.WriteAllText(PortablePaths.TranslationHistoryFilePath, json);
        }
        catch (Exception ex)
        {
            LogService.Warn($"Failed to save translation history: {ex.Message}");
        }
    }
}
