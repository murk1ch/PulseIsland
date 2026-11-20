using PulseIsland.Services;

namespace PulseIsland.Core;

public sealed record LanguageOption(
    string Code,
    string EnglishName,
    string RussianName,
    string NativeName)
{
    public string DisplayName => LocalizationService.IsRussian ? RussianName : EnglishName;
    public string FullLabel => $"{DisplayName} ({NativeName}) [{Code}]";
}

public sealed record OfflineModelFileEntry(
    string FileName,
    string DownloadUrl,
    long ApproximateBytes);

public sealed record OfflineModelCatalogEntry(
    string ModelId,
    string DisplayName,
    string SourceLang,
    string TargetLang,
    string Description,
    long TotalApproximateBytes,
    IReadOnlyList<OfflineModelFileEntry> Files);

public sealed record OfflineModelStatusDto(
    string ModelId,
    string DisplayName,
    string LanguagePair,
    string Description,
    string SizeText,
    bool IsInstalled,
    bool IsSelected,
    string LocalDirectoryPath);

public sealed record AudioDeviceOptionDto(
    string? DeviceId,
    string DisplayName);

public sealed record DiskSpaceSummaryDto(
    string DriveName,
    long AvailableFreeBytes,
    long TotalSizeBytes,
    long ModelsDirectoryBytes,
    long CacheDirectoryBytes)
{
    public string FormattedFreeSpace => FormatBytes(AvailableFreeBytes);
    public string FormattedModelsSize => FormatBytes(ModelsDirectoryBytes);
    public string FormattedCacheSize => FormatBytes(CacheDirectoryBytes);

    public string SummaryText => LocalizationService.Pick(
        $"Drive {DriveName} — free: {FormattedFreeSpace}  |  Models: {FormattedModelsSize}  |  Cache: {FormattedCacheSize}",
        $"Диск {DriveName} — свободно: {FormattedFreeSpace}  |  Модели: {FormattedModelsSize}  |  Кэш: {FormattedCacheSize}");

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return LocalizationService.Pick($"{bytes} B", $"{bytes} Б");
        double kb = bytes / 1024.0;
        if (kb < 1024) return LocalizationService.Pick($"{kb:0.#} KB", $"{kb:0.#} КБ");
        double mb = kb / 1024.0;
        if (mb < 1024) return LocalizationService.Pick($"{mb:0.#} MB", $"{mb:0.#} МБ");
        double gb = mb / 1024.0;
        return LocalizationService.Pick($"{gb:0.##} GB", $"{gb:0.##} ГБ");
    }
}

public static class LanguageCatalog
{
    public static IReadOnlyList<LanguageOption> SupportedLanguages { get; } = new List<LanguageOption>
    {
        new("en", "English", "Английский", "English"),
        new("ru", "Russian", "Русский", "Русский"),
        new("de", "German", "Немецкий", "Deutsch"),
        new("fr", "French", "Французский", "Français"),
        new("es", "Spanish", "Испанский", "Español"),
        new("it", "Italian", "Итальянский", "Italiano"),
        new("pt", "Portuguese", "Португальский", "Português"),
        new("zh", "Chinese", "Китайский", "中文"),
        new("ja", "Japanese", "Японский", "日本語"),
        new("ko", "Korean", "Корейский", "한국어"),
        new("tr", "Turkish", "Турецкий", "Türkçe"),
        new("pl", "Polish", "Польский", "Polski"),
        new("uk", "Ukrainian", "Украинский", "Українська"),
        new("kk", "Kazakh", "Казахский", "Қазақша"),
        new("ar", "Arabic", "Арабский", "العربية"),
        new("hi", "Hindi", "Хинди", "हिन्दी")
    };

    public static LanguageOption FindOrDefault(string? code, string fallbackCode = "en")
    {
        if (!string.IsNullOrWhiteSpace(code))
        {
            var normalized = code.Trim().Split('-')[0].ToLowerInvariant();
            var match = SupportedLanguages.FirstOrDefault(l =>
                string.Equals(l.Code, normalized, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }

        return SupportedLanguages.First(l => l.Code == fallbackCode);
    }
}
