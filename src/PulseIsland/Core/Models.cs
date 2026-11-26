using System.IO;
using System.Windows.Media;
using PulseIsland.Services;

namespace PulseIsland.Core;

public enum AppLanguage
{
    English,
    Russian
}

public enum IslandState
{
    Hidden,
    Compact,
    Expanded
}

public enum TranslationEngineMode
{
    OnlineWindowsOcr,
    OfflineOnnx,
    OfflineLocalLlm
}

public enum OcrEngineMode
{
    AutoSmart,
    VisionLlmAlways,
    WindowsOcrOnly
}

public enum OnlineTranslationProvider
{
    GoogleFree,
    DeepL
}

public enum WaveVisualStyle
{
    Bars,
    SmoothCurve
}

public enum WaveColorMode
{
    ThemeAdaptive,
    Static,
    Gradient,
    AlbumAdaptive
}

public enum AppThemeStyle
{
    Classic,
    CrimsonBlack,
    IndigoAmber,
    NavyMagenta,
    GraphiteSand,
    TealOrange,
    LightWhite
}

public sealed record MediaTrackInfo(
    string Title,
    string Artist,
    bool IsPlaying,
    bool HasActiveSession,
    ImageSource? Thumbnail,
    Color AccentColor,
    string SourceAppId = "",
    double PositionSeconds = 0,
    double DurationSeconds = 0,
    bool CanSeek = false);

public sealed record ClipboardEntry(
    string Id,
    string Text,
    DateTime Timestamp,
    bool IsPinned = false);

public sealed record DropShelfItem(
    string FilePath,
    string FileName,
    string SizeDisplay,
    bool IsDirectory)
{
    public bool IsImageFile => !IsDirectory && HasImageExtension(FilePath);
    public bool CanConvertToPng => IsImageFile && !string.Equals(Path.GetExtension(FilePath), ".png", StringComparison.OrdinalIgnoreCase);
    public bool CanCompressToJpg => IsImageFile;

    private static bool HasImageExtension(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string ext = Path.GetExtension(path);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".gif", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record TranslationRecord(
    string SourceText,
    string TranslatedText,
    string DetectedSourceLanguage,
    string TargetLanguage,
    DateTime Timestamp,
    bool IsOffline);

public sealed record OcrResult(
    bool Success,
    string Text,
    string DetectedLanguageTag,
    string? ErrorMessage = null);

public sealed record TranslationResult(
    bool Success,
    string TranslatedText,
    string DetectedSourceLanguage,
    string TargetLanguage,
    bool UsedOfflineModel,
    string? ErrorMessage = null);

public enum NotificationKind
{
    Default,
    Info,
    Success,
    Warning,
    Error,
    Music,
    System
}

public enum NotificationSoundPreset
{
    None,
    PulseChime,
    CrystalDrop,
    SoftBubble,
    CosmicPing,
    SystemAsterisk
}

public enum AiComputeBackend
{
    Auto,
    GpuVulkan,
    CpuOnly
}

public enum AiFeedHotkeyAction
{
    Summarize,
    Translate,
    FillPrompt
}

public enum MouseConnectionMode
{
    Auto,
    Bluetooth,
    Wireless24Ghz
}

public readonly record struct MouseBatterySnapshot(
    bool HasDevice,
    int Percent,
    bool IsCharging,
    string DeviceName,
    string ConnectionLabel);

public sealed record WeatherSnapshot(
    string City,
    int TempC,
    int FeelsLikeC,
    string Condition,
    int WindKmh,
    int HumidityPct,
    DateTime FetchedAt,
    bool IsFromCache);

public sealed record IslandNotification(
    string Id,
    string AppName,
    string Title,
    string Body,
    NotificationKind Kind,
    DateTime Timestamp,
    double AutoDismissSeconds = 5.0,
    string? ActionLabel = null,
    string? ActionTag = null,
    string AppUserModelId = "",
    string ContentTag = "",
    string SenderHint = "")
{
    public bool HasContentTag => !string.IsNullOrWhiteSpace(ContentTag);
    public bool HasBody => !string.IsNullOrWhiteSpace(Body);

    public string TimeAgoText
    {
        get
        {
            var diff = DateTime.Now - Timestamp;
            if (diff.TotalSeconds < 10) return LocalizationService.Pick("just now", "только что");
            if (diff.TotalMinutes < 1) return LocalizationService.Pick($"{(int)diff.TotalSeconds}s ago", $"{(int)diff.TotalSeconds} сек назад");
            if (diff.TotalHours < 1) return LocalizationService.Pick($"{(int)diff.TotalMinutes}m ago", $"{(int)diff.TotalMinutes} мин назад");
            if (diff.TotalDays < 1) return LocalizationService.Pick($"{(int)diff.TotalHours}h ago", $"{(int)diff.TotalHours} ч назад");
            return Timestamp.ToString("dd.MM HH:mm");
        }
    }
}

public sealed record CustomAiPromptChip(
    string Title,
    string PromptTemplate);

public sealed record AiChatTurn(
    string Role,
    string Content,
    byte[]? ImageJpegBytes = null);

public sealed record ClipboardImageEntry(
    string Id,
    int Width,
    int Height,
    DateTime Timestamp,
    byte[] Bgra32Pixels,
    ulong PixelHash,
    bool IsPinned = false);
