using System.IO;

namespace PulseIsland.Core;

/// <summary>
/// Resolves all application paths strictly inside the portable root directory (<portable-root>).
/// Never writes to C:\Users\...\AppData, LocalAppData, Documents, or %TEMP%.
/// </summary>
public static class PortablePaths
{
    private static readonly Lazy<string> _rootDirectory = new(ResolveRootDirectory);

    public static string RootDirectory => _rootDirectory.Value;
    public static string ConfigDirectory => Path.Combine(RootDirectory, "config");
    public static string SettingsFilePath => Path.Combine(ConfigDirectory, "settings.json");
    public static string TranslationHistoryFilePath => Path.Combine(ConfigDirectory, "translation_history.json");
    public static string ClipboardHistoryFilePath => Path.Combine(ConfigDirectory, "clipboard_history.json");
    public static string ModelsDirectory => Path.Combine(RootDirectory, "models");
    public static string CacheDirectory => Path.Combine(RootDirectory, "cache");
    public static string DownloadsCacheDirectory => Path.Combine(CacheDirectory, "downloads");
    public static string LogsDirectory => Path.Combine(RootDirectory, "logs");

    public static void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(ModelsDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(DownloadsCacheDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }

    private static string ResolveRootDirectory()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // When running via `dotnet run` inside src\PulseIsland\bin\Debug\..., walk up to the solution root
        // so config/models/cache/logs stay directly in <portable-root> instead of bin\Debug\.
        var current = new DirectoryInfo(baseDir);
        for (int i = 0; i < 6 && current is not null; i++)
        {
            if (File.Exists(Path.Combine(current.FullName, "PulseIsland.sln")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        return baseDir;
    }
}
