using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PulseIsland.Core;

namespace PulseIsland.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _sync = new();
    public AppSettings Current { get; private set; } = new();

    public event Action<AppSettings>? SettingsChanged;

    public AppSettings Load()
    {
        lock (_sync)
        {
            PortablePaths.EnsureDirectoriesExist();
            var path = PortablePaths.SettingsFilePath;

            if (!File.Exists(path))
            {
                Current = new AppSettings();
                SaveInternal(Current);
                return Current;
            }

            try
            {
                var json = File.ReadAllText(path);
                Current = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
            catch (Exception ex)
            {
                LogService.Error("Failed to deserialize settings.json, falling back to defaults.", ex);
                Current = new AppSettings();
            }

            return Current;
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_sync)
        {
            Current = settings;
            SaveInternal(settings);
        }

        SettingsChanged?.Invoke(settings);
    }

    private static void SaveInternal(AppSettings settings)
    {
        PortablePaths.EnsureDirectoriesExist();
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var targetPath = PortablePaths.SettingsFilePath;
        var tempPath = targetPath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, targetPath, overwrite: true);
    }
}
