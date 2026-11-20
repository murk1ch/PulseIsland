namespace PulseIsland.Core;

public sealed class AppSettings
{
    // --- General & Interface ---
    public AppLanguage UiLanguage { get; set; } = AppLanguage.English;
    public AppThemeStyle ThemeStyle { get; set; } = AppThemeStyle.Classic;
    public bool AutoStartWithWindows { get; set; } = false;
    public int MonitorIndex { get; set; } = 0; // 0 = Primary monitor
    public bool AutoHideIsland { get; set; } = false; // Stay visible by default so island never vanishes unexpectedly
    public int AutoHideDelayMs { get; set; } = 5000;
    public int TopHoverTriggerHeightPx { get; set; } = 8;
    public bool ExpandOnHover { get; set; } = false; // false = expand on click
    public bool CollapseOnMouseLeave { get; set; } = false; // only active when ExpandOnHover is true
    public bool ShowTrayIcon { get; set; } = true;
    public bool HideInFullscreenApps { get; set; } = false;

    // --- Toggleable Design & Animation Features ---
    public bool EnableDwmBackdropBlur { get; set; } = false;
    public bool ShowTrackProgressBar { get; set; } = true;
    public bool EnableAlbumArtGlow { get; set; } = true;
    public bool UseCustomIslandAppearance { get; set; } = false;
    public double IslandCornerRadius { get; set; } = 24.0;
    public double IslandBackgroundOpacity { get; set; } = 0.96;

    // --- Smart Productivity Features ---
    public bool EnableSmartClipboard { get; set; } = true;
    public bool EnableClipboardTranslateHotkey { get; set; } = true;
    public string ClipboardTranslateHotkeyGesture { get; set; } = "Ctrl+Alt+C";
    public bool EnableVolumeAndMicIndicator { get; set; } = true;
    public bool EnableFileDropZone { get; set; } = true;
    public bool EnableSystemLoadMonitor { get; set; } = true;
    public bool ShowCpuInIsland { get; set; } = true;
    public bool ShowRamInIsland { get; set; } = true;
    public bool ShowTimeDateInIsland { get; set; } = true;
    public bool ShowNetworkSpeedInIsland { get; set; } = false;
    public bool ShowBatteryInIsland { get; set; } = false;
    public bool ShowUptimeInIsland { get; set; } = false;

    // --- Wireless Mouse Battery Indicator ---
    public bool EnableMouseBatteryIndicator { get; set; } = false;
    public MouseConnectionMode MouseConnectionMode { get; set; } = MouseConnectionMode.Auto;
    public bool ShowMouseBatteryInCompact { get; set; } = true;
    public bool EnableMouseLowBatterySound { get; set; } = true;
    public int MouseLowBatteryThresholdPercent { get; set; } = 15;
    public NotificationSoundPreset MouseLowBatterySound { get; set; } = NotificationSoundPreset.SoftBubble;

    // --- Island Notifications ---
    public bool EnableIslandNotifications { get; set; } = true;
    public bool SuppressWindowsToastWhenIslandActive { get; set; } = true;
    public double NotificationAutoDismissSeconds { get; set; } = 5.0;
    public NotificationSoundPreset NotificationSound { get; set; } = NotificationSoundPreset.PulseChime;

    // --- Local AI Assistant (Qwen 2.5 3B GGUF) ---
    public bool EnableLocalAiAssistant { get; set; } = true;
    public AiComputeBackend AiBackend { get; set; } = AiComputeBackend.Auto;
    public string AiWeatherCity { get; set; } = string.Empty;
    public string SelectedAiModelId { get; set; } = "qwen2.5-3b-instruct";
    public bool EnableAiFeedHotkey { get; set; } = true;
    public string AiFeedHotkeyGesture { get; set; } = "Ctrl+Alt+A";
    public AiFeedHotkeyAction AiFeedAction { get; set; } = AiFeedHotkeyAction.Summarize;

    // --- Translator ---
    public TranslationEngineMode TranslationMode { get; set; } = TranslationEngineMode.OnlineWindowsOcr;
    public OnlineTranslationProvider OnlineProvider { get; set; } = OnlineTranslationProvider.GoogleFree;
    public string TargetLanguage { get; set; } = "en";
    public bool AutoDetectSourceLanguage { get; set; } = true;
    public string ManualSourceLanguage { get; set; } = "ru";
    public string HotkeyGesture { get; set; } = "Alt+Q";
    public string SelectedOfflineModelId { get; set; } = "opus-mt-en-ru";
    public string EncryptedDeepLApiKey { get; set; } = string.Empty;

    // Cursor trail & popup settings
    public int CursorTrailLengthMs { get; set; } = 360;
    public double CursorTrailThickness { get; set; } = 3.5;
    public double CursorTrailOpacity { get; set; } = 0.90;
    public int TranslationPopupAutoHideSeconds { get; set; } = 15;

    // --- Music & Waves ---
    public bool EnableMusicModule { get; set; } = true;
    public bool HideMusicOnPause { get; set; } = false;
    public int HideMusicOnPauseDelaySeconds { get; set; } = 15;
    public string? AudioDeviceId { get; set; } = null; // null = system default
    public bool ShowAlbumArtInCompact { get; set; } = true;
    public bool EnableMarqueeInCompact { get; set; } = true;

    // Visualizer settings
    public WaveVisualStyle WaveStyle { get; set; } = WaveVisualStyle.SmoothCurve;
    public WaveColorMode WaveColorMode { get; set; } = WaveColorMode.ThemeAdaptive;
    public string StaticWaveColorHex { get; set; } = "#FF7DD3FC";
    public string GradientSecondaryColorHex { get; set; } = "#FFA78BFA";
    public double WaveSensitivity { get; set; } = 1.85;
    public double WaveSmoothing { get; set; } = 0.85;
    public int WaveBarCount { get; set; } = 18;
    public bool HideWavesOnSilence { get; set; } = false;
}
