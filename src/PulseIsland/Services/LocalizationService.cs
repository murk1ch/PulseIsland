using System.Globalization;
using System.Windows;
using PulseIsland.Core;

namespace PulseIsland.Services;

/// <summary>
/// Zero-NuGet dynamic localization engine supporting English (default) and Russian.
/// Publishes every Loc_* key into Application.Current.Resources so all open WPF windows
/// update live via {DynamicResource Loc_*} when the user switches UI language in Settings.
/// </summary>
public static class LocalizationService
{
    public static AppLanguage CurrentLanguage { get; private set; } = AppLanguage.English;
    public static bool IsRussian => CurrentLanguage == AppLanguage.Russian;

    public static event Action<AppLanguage>? LanguageChanged;

    private static readonly IReadOnlyDictionary<string, string> EnglishStrings = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // Common buttons & controls
        ["Loc_Copy"] = "Copy",
        ["Loc_Copied"] = "Copied ✓",
        ["Loc_Clear"] = "Clear",
        ["Loc_Close"] = "Close",
        ["Loc_CloseEsc"] = "Close (Esc)",
        ["Loc_Collapse"] = "Collapse",
        ["Loc_Show"] = "Show",
        ["Loc_Hide"] = "Hide",
        ["Loc_History"] = "History",
        ["Loc_Refresh"] = "Refresh",
        ["Loc_Select"] = "Select",
        ["Loc_Download"] = "Download",
        ["Loc_Delete"] = "Delete",
        ["Loc_Cancel"] = "Cancel",
        ["Loc_Done"] = "Done",
        ["Loc_Reset"] = "Reset",
        ["Loc_Active"] = "ACTIVE",
        ["Loc_On"] = "ON",
        ["Loc_Off"] = "OFF",
        ["Loc_Sec"] = "s",
        ["Loc_PreviewSound"] = "Preview",

        // Sound presets
        ["Loc_Sound_PulseChime"] = "Pulse Glass (soft chime)",
        ["Loc_Sound_CrystalDrop"] = "Crystal Drop (clear chime)",
        ["Loc_Sound_SoftBubble"] = "Soft Bubble (gentle pop)",
        ["Loc_Sound_CosmicPing"] = "Cosmic Ping (ambient ping)",
        ["Loc_Sound_SystemAsterisk"] = "Windows Default Sound",
        ["Loc_Sound_None"] = "No sound",

        // IslandWindow
        ["Loc_Island_PrevTrack"] = "Previous track",
        ["Loc_Island_PlayPause"] = "Play / Pause",
        ["Loc_Island_NextTrack"] = "Next track",
        ["Loc_Island_ExpandNotifTooltip"] = "Click to expand notification and history",
        ["Loc_Island_DismissNotif"] = "Dismiss notification",
        ["Loc_Island_SmartClipTooltip"] = "Smart clipboard with keyword search",
        ["Loc_Island_ClipboardBtn"] = "Clipboard",
        ["Loc_Island_Settings"] = "Settings",
        ["Loc_Island_CollapseToCompact"] = "Collapse to compact island",
        ["Loc_Island_HideToTop"] = "Hide island to top edge",
        ["Loc_Island_MusicSection"] = "MUSIC & LIVE WAVE",
        ["Loc_Island_ToggleSpeaker"] = "Mute / unmute speaker",
        ["Loc_Island_ToggleMic"] = "Mute / unmute microphone",
        ["Loc_Island_TranslatorSection"] = "TRANSLATOR & HISTORY",
        ["Loc_Island_ExpandTranslationTooltip"] = "Click to expand or collapse full text",
        ["Loc_Island_NotificationsSection"] = "NOTIFICATIONS",
        ["Loc_Island_OpenNotifTooltip"] = "Click to open notification",
        ["Loc_Island_NotifEmptyHint"] = "New system notifications will appear here instead of the screen corner",
        ["Loc_Island_NotifHistoryHint"] = "Click \"History\" to view recent notifications",
        ["Loc_Island_FileShelfSection"] = "FILE DROP SHELF (DRAG HERE)",
        ["Loc_Island_FileShelfHint"] = "Drag any files or screenshots onto the island to move them quickly between windows",

        // LassoOverlayWindow
        ["Loc_Lasso_Hint"] = "Hold Left Mouse Button and circle (or underline) text to translate   •   Esc / Right-Click to cancel",

        // SmartClipboardWindow
        ["Loc_Clip_WindowTitle"] = "PulseIsland — Smart Clipboard",
        ["Loc_Clip_Header"] = "Smart Clipboard",
        ["Loc_Clip_Pinned"] = "Pinned",
        ["Loc_Clip_AllItems"] = "All items",
        ["Loc_Clip_SearchPlaceholder"] = "Search by keyword...",
        ["Loc_Clip_PinTooltip"] = "Pin / unpin",
        ["Loc_Clip_DeleteTooltip"] = "Delete",
        ["Loc_Clip_FooterHint"] = "Automatically saves copied text",
        ["Loc_Clip_ClearUnpinned"] = "Clear unpinned",

        // AiAssistantPopupWindow
        ["Loc_Ai_WeatherBtn"] = "Weather",
        ["Loc_Ai_SummarizeClipBtn"] = "Summarize clipboard",
        ["Loc_Ai_TranslateAiBtn"] = "Translate with AI",
        ["Loc_Ai_PromptPlaceholder"] = "Ask AI (Enter to send, Esc to close)...",
        ["Loc_Ai_AskBtn"] = "Ask",
        ["Loc_Ai_StopBtn"] = "Stop",
        ["Loc_Ai_Ready"] = "Ready",

        // SettingsWindow — Header & Footer & Tabs
        ["Loc_Settings_WindowTitle"] = "PulseIsland — Settings",
        ["Loc_Settings_Header"] = "PulseIsland Settings",
        ["Loc_Settings_ResetDefaults"] = "Reset to defaults",
        ["Loc_Settings_SaveBtn"] = "Save settings",
        ["Loc_Tab_General"] = "General & Island",
        ["Loc_Tab_TranslatorAi"] = "Translator & AI",
        ["Loc_Tab_Music"] = "Music & Waves",

        // SettingsWindow — Tab 1: Language & Theme
        ["Loc_Sec_Appearance"] = "APPEARANCE, LANGUAGE & THEME",
        ["Loc_Field_UiLanguage"] = "Interface language:",
        ["Loc_Lang_English"] = "English (Default)",
        ["Loc_Lang_Russian"] = "Русский (Russian)",
        ["Loc_Field_Theme"] = "Choose interface theme (waves adapt automatically):",
        ["Loc_Theme_Classic"] = "Classic (Obsidian / Sky)",
        ["Loc_Theme_CrimsonBlack"] = "Crimson / Black",
        ["Loc_Theme_IndigoAmber"] = "Indigo / Amber",
        ["Loc_Theme_NavyMagenta"] = "Deep Navy / Magenta",
        ["Loc_Theme_GraphiteSand"] = "Graphite / Sand",
        ["Loc_Theme_TealOrange"] = "Teal / Orange",
        ["Loc_Theme_LightWhite"] = "Light Frosted Glass",
        ["Loc_Check_DwmBlur"] = "Real frosted backdrop blur (Windows DWM Blur)",
        ["Loc_Check_ProgressBar"] = "Show track progress bar and seek in expanded island",
        ["Loc_Check_AlbumGlow"] = "Adaptive album cover glow matching track color",
        ["Loc_Check_CustomAppearance"] = "Customize island corner radius and opacity manually",
        ["Loc_Field_CornerRadius"] = "Corner radius:",
        ["Loc_Field_Opacity"] = "Glass opacity:",

        // SettingsWindow — Tab 1: Smart Features
        ["Loc_Sec_SmartFeatures"] = "SMART ISLAND FEATURES",
        ["Loc_Check_SmartClipboard"] = "Smart clipboard with keyword search (\"Clipboard\" button on island)",
        ["Loc_Check_VolumeMic"] = "Volume / microphone indicator and quick mute buttons",
        ["Loc_Check_FileDrop"] = "File drop shelf (Drop Zone when dragging files onto island)",
        ["Loc_Check_SystemLoad"] = "PC telemetry and widgets on island",
        ["Loc_Check_Cpu"] = "CPU usage (CPU %)",
        ["Loc_Check_Ram"] = "Memory usage (RAM %)",
        ["Loc_Check_TimeDate"] = "Time and date (HH:MM • DD.MM)",
        ["Loc_Check_NetSpeed"] = "Network speed (↓ download / ↑ upload)",
        ["Loc_Check_PcBattery"] = "PC battery / power status",
        ["Loc_Check_Uptime"] = "System uptime",

        // SettingsWindow — Tab 1: Wireless Mouse Battery
        ["Loc_Check_MouseBattery"] = "Wireless mouse battery",
        ["Loc_Btn_MoreSettings"] = "More settings ▾",
        ["Loc_Btn_CollapseSettings"] = "Collapse ▴",
        ["Loc_Mouse_Searching"] = "Searching for wireless mouse...",
        ["Loc_Field_MouseConnMode"] = "Mouse connection type:",
        ["Loc_MouseMode_Auto"] = "Auto (Bluetooth + 2.4 GHz)",
        ["Loc_MouseMode_Bluetooth"] = "Bluetooth only",
        ["Loc_MouseMode_24Ghz"] = "2.4 GHz only (USB receiver)",
        ["Loc_Check_MouseCompact"] = "Show on compact island (when no music is playing)",
        ["Loc_Check_MouseLowSound"] = "Play sound alert on low battery",
        ["Loc_Field_MouseThreshold"] = "Low-battery threshold:",

        // SettingsWindow — Tab 1: Notifications
        ["Loc_Sec_Notifications"] = "ISLAND NOTIFICATIONS",
        ["Loc_Check_EnableNotif"] = "Show system notifications inside the island",
        ["Loc_Check_SuppressToast"] = "Suppress default Windows corner toasts (Focus Assist)",
        ["Loc_Field_NotifDismiss"] = "Auto-dismiss notification after:",
        ["Loc_Field_NotifSound"] = "Notification sound:",

        // SettingsWindow — Tab 1: System & Behavior
        ["Loc_Sec_System"] = "SYSTEM, WIN+D PROTECTION & MONITOR",
        ["Loc_Check_AutoStart"] = "Launch PulseIsland with Windows",
        ["Loc_Check_TrayIcon"] = "Show quick-control icon in system tray",
        ["Loc_Check_HideFullscreen"] = "Hide island in exclusive fullscreen 3D games",
        ["Loc_Field_Monitor"] = "Display monitor for island:",
        ["Loc_Sec_Behavior"] = "MORPHING & ISLAND BEHAVIOR",
        ["Loc_Check_PinIsland"] = "Keep island pinned at top (always visible as a capsule)",
        ["Loc_Check_ExpandHover"] = "Smoothly expand island on hover (Blur-in morphing)",
        ["Loc_Check_CollapseLeave"] = "Collapse island immediately when pointer leaves",
        ["Loc_Field_AutoHideDelay"] = "Auto-hide delay when OFF (1000–30000 ms):",
        ["Loc_Field_HoverZone"] = "Top wake trigger height (2–24 px):",

        // SettingsWindow — Tab 2: Translator & AI
        ["Loc_Sec_TranslatorEngine"] = "LANGUAGES & RECOGNITION ENGINE (WINDOWS OCR + AI)",
        ["Loc_Field_TransMode"] = "Translation mode:",
        ["Loc_TransMode_Hybrid"] = "Hybrid: Online (Google/DeepL) → ONNX offline model / dictionary fallback",
        ["Loc_TransMode_Offline"] = "Fully offline: Windows OCR + local ONNX model (no internet)",
        ["Loc_Field_TargetLang"] = "Target translation language:",
        ["Loc_Field_SourceLang"] = "Source language (when auto-detect is off):",
        ["Loc_Check_AutoDetectLang"] = "Automatically detect recognized text language",
        ["Loc_Field_OnlineProvider"] = "Online provider:",
        ["Loc_Provider_Google"] = "Google / MyMemory (No API key)",
        ["Loc_Provider_DeepL"] = "DeepL API (API key)",
        ["Loc_Field_DeepLKey"] = "DeepL API key (DPAPI encrypted):",
        ["Loc_Sec_HotkeysTrail"] = "HOTKEYS & CURSOR TRAIL",
        ["Loc_Field_LassoHotkey"] = "Screen lasso capture hotkey:",
        ["Loc_Btn_RecordHotkey"] = "Record hotkey...",
        ["Loc_Btn_RecordShort"] = "Record...",
        ["Loc_Check_ClipHotkey"] = "Quick translate selection / clipboard via hotkey (no lasso)",
        ["Loc_Field_TrailLen"] = "Trail length (ms):",
        ["Loc_Field_TrailWidth"] = "Thickness (1–10):",
        ["Loc_Field_TrailOpacity"] = "Opacity (0.1–1):",
        ["Loc_Field_PopupTimer"] = "Popup timer (s):",
        ["Loc_Sec_OfflineModels"] = "OFFLINE MODEL MANAGER",
        ["Loc_Btn_OpenFolder"] = "Open folder",
        ["Loc_Btn_RefreshList"] = "Refresh list",
        ["Loc_Sec_LocalAi"] = "LOCAL AI ASSISTANT (QWEN 2.5 3B • GPU / CPU)",
        ["Loc_Check_EnableAi"] = "Enable AI assistant module (weather, summary, and AI translation)",
        ["Loc_Ai_ModelDesc"] = "All-purpose local 3B model: summarize selected text, natural translation, and weather brief.",
        ["Loc_Field_AiBackend"] = "Compute acceleration (GPU / CPU):",
        ["Loc_AiBackend_Auto"] = "Auto (GPU Vulkan preferred → CPU fallback)",
        ["Loc_AiBackend_Gpu"] = "GPU only (Vulkan)",
        ["Loc_AiBackend_Cpu"] = "CPU only",
        ["Loc_Field_WeatherCity"] = "Weather city (empty = auto):",
        ["Loc_Check_AiHotkey"] = "Feed selected text / clipboard to AI via hotkey",
        ["Loc_AiAction_Summarize"] = "Action: Summarize briefly",
        ["Loc_AiAction_Translate"] = "Action: Translate with AI",
        ["Loc_AiAction_FillPrompt"] = "Action: Paste into prompt box",

        // SettingsWindow — Tab 3: Music & Waves
        ["Loc_Sec_MusicIntegration"] = "MEDIA INTEGRATION & ISLAND CONTROLS",
        ["Loc_Check_EnableMusic"] = "Enable WASAPI audio capture, live waves, and media buttons",
        ["Loc_Check_ShowAlbumArt"] = "Show mini album cover in compact mode",
        ["Loc_Check_Marquee"] = "Smooth marquee scrolling for long track titles",
        ["Loc_Check_HideMusicPause"] = "Hide music waves after prolonged pause",
        ["Loc_Field_PauseDelay"] = "Pause hide delay (s):",
        ["Loc_Field_AudioDevice"] = "WASAPI Loopback audio capture device:",
        ["Loc_Sec_WavePhysics"] = "AUDIO WAVE PHYSICS (LOGARITHMIC FFT + BEZIER)",
        ["Loc_Check_HideSilence"] = "Completely hide idle wave motion during silence",
        ["Loc_Field_WaveStyle"] = "Wave rendering style:",
        ["Loc_WaveStyle_Smooth"] = "Smooth Bezier curve + harmonics (SmoothCurve)",
        ["Loc_WaveStyle_Bars"] = "Symmetric bars (Bars)",
        ["Loc_Field_WaveColorMode"] = "Wave color scheme:",
        ["Loc_WaveColor_Theme"] = "Match app theme (Auto)",
        ["Loc_WaveColor_Album"] = "Adapt to album cover (AlbumAdaptive)",
        ["Loc_WaveColor_Gradient"] = "Two-color gradient",
        ["Loc_WaveColor_Static"] = "Fixed static color",
        ["Loc_Field_WaveBars"] = "Spectrum detail (6–32):",
        ["Loc_Field_WaveSens"] = "Bass sensitivity (0.3–5.0):",
        ["Loc_Field_WaveSmooth"] = "Decay inertia (0.05–0.92):",
        ["Loc_Field_PrimaryColor"] = "Primary wave color (click circle for palette):",
        ["Loc_Field_SecondaryColor"] = "Secondary gradient color (click circle):",
        ["Loc_Tooltip_ColorPicker"] = "Open color picker palette",
        ["Loc_ColorPicker_Title"] = "Wave color palette",
    };

    private static readonly IReadOnlyDictionary<string, string> RussianStrings = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // Common buttons & controls
        ["Loc_Copy"] = "Копировать",
        ["Loc_Copied"] = "Скопировано ✓",
        ["Loc_Clear"] = "Очистить",
        ["Loc_Close"] = "Закрыть",
        ["Loc_CloseEsc"] = "Закрыть (Esc)",
        ["Loc_Collapse"] = "Свернуть",
        ["Loc_Show"] = "Показать",
        ["Loc_Hide"] = "Скрыть",
        ["Loc_History"] = "История",
        ["Loc_Refresh"] = "Обновить",
        ["Loc_Select"] = "Выбрать",
        ["Loc_Download"] = "Скачать",
        ["Loc_Delete"] = "Удалить",
        ["Loc_Cancel"] = "Отмена",
        ["Loc_Done"] = "Готово",
        ["Loc_Reset"] = "Сброс",
        ["Loc_Active"] = "АКТИВНА",
        ["Loc_On"] = "ВКЛ",
        ["Loc_Off"] = "ВЫКЛ",
        ["Loc_Sec"] = "сек",
        ["Loc_PreviewSound"] = "Прослушать",

        // Sound presets
        ["Loc_Sound_PulseChime"] = "Pulse Glass (мягкий звон)",
        ["Loc_Sound_CrystalDrop"] = "Crystal Drop (кристальная капля)",
        ["Loc_Sound_SoftBubble"] = "Soft Bubble (мягкий отклик)",
        ["Loc_Sound_CosmicPing"] = "Cosmic Ping (космический пинг)",
        ["Loc_Sound_SystemAsterisk"] = "Стандартный звук Windows",
        ["Loc_Sound_None"] = "Без звука",

        // IslandWindow
        ["Loc_Island_PrevTrack"] = "Предыдущий трек",
        ["Loc_Island_PlayPause"] = "Пауза / Воспроизведение",
        ["Loc_Island_NextTrack"] = "Следующий трек",
        ["Loc_Island_ExpandNotifTooltip"] = "Нажмите, чтобы развернуть уведомление и историю",
        ["Loc_Island_DismissNotif"] = "Закрыть уведомление",
        ["Loc_Island_SmartClipTooltip"] = "Умный буфер обмена с поиском по ключевому слову",
        ["Loc_Island_ClipboardBtn"] = "Буфер",
        ["Loc_Island_Settings"] = "Настройки",
        ["Loc_Island_CollapseToCompact"] = "Свернуть в компактный островок",
        ["Loc_Island_HideToTop"] = "Спрятать островок за верхний край экрана",
        ["Loc_Island_MusicSection"] = "МУЗЫКА И ЖИВАЯ ВОЛНА",
        ["Loc_Island_ToggleSpeaker"] = "Включить / отключить звук",
        ["Loc_Island_ToggleMic"] = "Включить / отключить микрофон",
        ["Loc_Island_TranslatorSection"] = "ПЕРЕВОДЧИК И ИСТОРИЯ",
        ["Loc_Island_ExpandTranslationTooltip"] = "Нажмите, чтобы развернуть или свернуть полный текст",
        ["Loc_Island_NotificationsSection"] = "УВЕДОМЛЕНИЯ",
        ["Loc_Island_OpenNotifTooltip"] = "Нажмите, чтобы открыть уведомление",
        ["Loc_Island_NotifEmptyHint"] = "Новые системные уведомления будут появляться здесь, а не в углу экрана",
        ["Loc_Island_NotifHistoryHint"] = "Нажмите «История», чтобы просмотреть прошлые уведомления",
        ["Loc_Island_FileShelfSection"] = "ПОЛКА ФАЙЛОВ (ПЕРЕТАЩИТЕ СЮДА)",
        ["Loc_Island_FileShelfHint"] = "Перетащите любые файлы или скриншоты на островок, чтобы быстро переносить их между окнами",

        // LassoOverlayWindow
        ["Loc_Lasso_Hint"] = "Зажмите ЛКМ и обведите (или подчеркните) текст для перевода   •   Esc / ПКМ — отмена",

        // SmartClipboardWindow
        ["Loc_Clip_WindowTitle"] = "PulseIsland — Умный буфер обмена",
        ["Loc_Clip_Header"] = "Умный буфер обмена",
        ["Loc_Clip_Pinned"] = "Закреплённые",
        ["Loc_Clip_AllItems"] = "Все записи",
        ["Loc_Clip_SearchPlaceholder"] = "Поиск по ключевому слову...",
        ["Loc_Clip_PinTooltip"] = "Закрепить / открепить",
        ["Loc_Clip_DeleteTooltip"] = "Удалить",
        ["Loc_Clip_FooterHint"] = "Автоматически запоминает скопированный текст",
        ["Loc_Clip_ClearUnpinned"] = "Очистить незакреплённые",

        // AiAssistantPopupWindow
        ["Loc_Ai_WeatherBtn"] = "Погода",
        ["Loc_Ai_SummarizeClipBtn"] = "Пересказать буфер",
        ["Loc_Ai_TranslateAiBtn"] = "Перевести ИИ",
        ["Loc_Ai_PromptPlaceholder"] = "Спросить ИИ (Enter — отправить, Esc — закрыть)...",
        ["Loc_Ai_AskBtn"] = "Спросить",
        ["Loc_Ai_StopBtn"] = "Стоп",
        ["Loc_Ai_Ready"] = "Готов к работе",

        // SettingsWindow — Header & Footer & Tabs
        ["Loc_Settings_WindowTitle"] = "PulseIsland — Настройки",
        ["Loc_Settings_Header"] = "Настройки PulseIsland",
        ["Loc_Settings_ResetDefaults"] = "Сбросить по умолчанию",
        ["Loc_Settings_SaveBtn"] = "Сохранить настройки",
        ["Loc_Tab_General"] = "Общие и островок",
        ["Loc_Tab_TranslatorAi"] = "Переводчик и ИИ",
        ["Loc_Tab_Music"] = "Музыка и волны",

        // SettingsWindow — Tab 1: Language & Theme
        ["Loc_Sec_Appearance"] = "ВНЕШНИЙ ВИД, ЯЗЫК И ТЕМА ПРОГРАММЫ",
        ["Loc_Field_UiLanguage"] = "Язык интерфейса:",
        ["Loc_Lang_English"] = "English (Английский)",
        ["Loc_Lang_Russian"] = "Русский",
        ["Loc_Field_Theme"] = "Выберите стиль оформления интерфейса (волны подстраиваются автоматически):",
        ["Loc_Theme_Classic"] = "Классический (Обсидиан / Небесный)",
        ["Loc_Theme_CrimsonBlack"] = "Алый / Чёрный",
        ["Loc_Theme_IndigoAmber"] = "Индиго / Янтарный",
        ["Loc_Theme_NavyMagenta"] = "Тёмно-синий / Малиновый",
        ["Loc_Theme_GraphiteSand"] = "Графитовый / Песочный",
        ["Loc_Theme_TealOrange"] = "Бирюзовый / Оранжевый",
        ["Loc_Theme_LightWhite"] = "Белый стиль (Светлое матовое стекло)",
        ["Loc_Check_DwmBlur"] = "Настоящее размытие фона за стеклом (Windows DWM Blur)",
        ["Loc_Check_ProgressBar"] = "Показывать прогресс-бар и перемотку трека в раскрытом островке",
        ["Loc_Check_AlbumGlow"] = "Адаптивное свечение обложки альбома под цвет трека",
        ["Loc_Check_CustomAppearance"] = "Настроить скругление и прозрачность островка вручную",
        ["Loc_Field_CornerRadius"] = "Скругление углов:",
        ["Loc_Field_Opacity"] = "Непрозрачность стекла:",

        // SettingsWindow — Tab 1: Smart Features
        ["Loc_Sec_SmartFeatures"] = "УМНЫЕ ФУНКЦИИ ОСТРОВКА",
        ["Loc_Check_SmartClipboard"] = "Умный буфер обмена с поиском по ключевому слову (кнопка «Буфер» в островке)",
        ["Loc_Check_VolumeMic"] = "Индикатор громкости / микрофона и быстрые кнопки Mute в островке",
        ["Loc_Check_FileDrop"] = "Полка перетаскивания файлов (Drop Zone при наведении файлов на островок)",
        ["Loc_Check_SystemLoad"] = "Показания ПК и виджеты на островке",
        ["Loc_Check_Cpu"] = "Загрузка процессора (CPU %)",
        ["Loc_Check_Ram"] = "Использование оперативной памяти (RAM %)",
        ["Loc_Check_TimeDate"] = "Время и дата (ЧЧ:ММ • ДД.ММ)",
        ["Loc_Check_NetSpeed"] = "Скорость сети (↓ скачивание / ↑ отдача)",
        ["Loc_Check_PcBattery"] = "Заряд батареи / питание ПК",
        ["Loc_Check_Uptime"] = "Время работы ПК (Uptime)",

        // SettingsWindow — Tab 1: Wireless Mouse Battery
        ["Loc_Check_MouseBattery"] = "Заряд беспроводной мыши",
        ["Loc_Btn_MoreSettings"] = "Доп. настройки ▾",
        ["Loc_Btn_CollapseSettings"] = "Свернуть ▴",
        ["Loc_Mouse_Searching"] = "Поиск беспроводной мыши...",
        ["Loc_Field_MouseConnMode"] = "Тип подключения мыши:",
        ["Loc_MouseMode_Auto"] = "Авто (Bluetooth + 2.4 ГГц)",
        ["Loc_MouseMode_Bluetooth"] = "Только Bluetooth",
        ["Loc_MouseMode_24Ghz"] = "Только 2.4 ГГц (USB-приёмник)",
        ["Loc_Check_MouseCompact"] = "Показывать на компактном островке (когда нет музыки)",
        ["Loc_Check_MouseLowSound"] = "Звуковой сигнал при низком заряде",
        ["Loc_Field_MouseThreshold"] = "Порог предупреждения:",

        // SettingsWindow — Tab 1: Notifications
        ["Loc_Sec_Notifications"] = "УВЕДОМЛЕНИЯ В ОСТРОВКЕ",
        ["Loc_Check_EnableNotif"] = "Показывать системные уведомления прямо в островке",
        ["Loc_Check_SuppressToast"] = "Подавлять стандартные всплывающие окна Windows (Focus Assist)",
        ["Loc_Field_NotifDismiss"] = "Автоматически скрывать уведомление через:",
        ["Loc_Field_NotifSound"] = "Звук при получении уведомления:",

        // SettingsWindow — Tab 1: System & Behavior
        ["Loc_Sec_System"] = "СИСТЕМА, ЗАЩИТА ОТ WIN+D И МОНИТОР",
        ["Loc_Check_AutoStart"] = "Запускать PulseIsland вместе с Windows",
        ["Loc_Check_TrayIcon"] = "Показывать иконку быстрого управления в системном трее",
        ["Loc_Check_HideFullscreen"] = "Скрывать островок только в эксклюзивных 3D-играх на весь экран",
        ["Loc_Field_Monitor"] = "Монитор отображения островка:",
        ["Loc_Sec_Behavior"] = "МОРФИНГ И ПОВЕДЕНИЕ ОСТРОВКА",
        ["Loc_Check_PinIsland"] = "Не скрывать островок в верх экрана (всегда виден как капсула)",
        ["Loc_Check_ExpandHover"] = "Плавно раскрывать островок при наведении курсора (Blur-in морфинг)",
        ["Loc_Check_CollapseLeave"] = "Сворачивать островок сразу при уходе курсора",
        ["Loc_Field_AutoHideDelay"] = "Задержка автоскрытия при ВЫКЛ (1000–30000 мс):",
        ["Loc_Field_HoverZone"] = "Зона пробуждения сверху (2–24 пикс):",

        // SettingsWindow — Tab 2: Translator & AI
        ["Loc_Sec_TranslatorEngine"] = "ЯЗЫКИ И ДВИЖОК РАСПОЗНАВАНИЯ (WINDOWS OCR + AI)",
        ["Loc_Field_TransMode"] = "Режим работы переводчика:",
        ["Loc_TransMode_Hybrid"] = "Гибридный: Онлайн (Google/DeepL) → при сбое сети нейросеть ONNX / словарь",
        ["Loc_TransMode_Offline"] = "Полностью офлайн: Системный OCR + Локальная модель (без интернета)",
        ["Loc_Field_TargetLang"] = "Целевой язык перевода:",
        ["Loc_Field_SourceLang"] = "Исходный язык (если автовыбор выкл.):",
        ["Loc_Check_AutoDetectLang"] = "Автоматически определять язык распознанного текста",
        ["Loc_Field_OnlineProvider"] = "Онлайн-провайдер:",
        ["Loc_Provider_Google"] = "Google / MyMemory (Без ключа)",
        ["Loc_Provider_DeepL"] = "DeepL API (По ключу)",
        ["Loc_Field_DeepLKey"] = "API-ключ DeepL (DPAPI шифрование):",
        ["Loc_Sec_HotkeysTrail"] = "ГОРЯЧАЯ КЛАВИША И СЛЕД КУРСОРА",
        ["Loc_Field_LassoHotkey"] = "Комбинация клавиш активации обводки:",
        ["Loc_Btn_RecordHotkey"] = "Записать нажатие...",
        ["Loc_Btn_RecordShort"] = "Записать...",
        ["Loc_Check_ClipHotkey"] = "Быстрый перевод выделенного текста / буфера по горячей клавише (без обводки)",
        ["Loc_Field_TrailLen"] = "Длина следа (мс):",
        ["Loc_Field_TrailWidth"] = "Толщина (1–10):",
        ["Loc_Field_TrailOpacity"] = "Яркость (0.1–1):",
        ["Loc_Field_PopupTimer"] = "Таймер окна (с):",
        ["Loc_Sec_OfflineModels"] = "МЕНЕДЖЕР ОФЛАЙН-МОДЕЛЕЙ",
        ["Loc_Btn_OpenFolder"] = "Открыть папку",
        ["Loc_Btn_RefreshList"] = "Обновить список",
        ["Loc_Sec_LocalAi"] = "ЛОКАЛЬНЫЙ ИИ-АССИСТЕНТ (QWEN 2.5 3B • GPU / CPU)",
        ["Loc_Check_EnableAi"] = "Показывать блок ИИ-ассистента в островке (погода, пересказ и перевод текста)",
        ["Loc_Ai_ModelDesc"] = "Универсальная локальная 3B модель: пересказ выделенного текста, живой перевод и сводка погоды.",
        ["Loc_Field_AiBackend"] = "Ускорение вычислений (GPU / CPU):",
        ["Loc_AiBackend_Auto"] = "Авто (GPU Vulkan быстрее → иначе CPU)",
        ["Loc_AiBackend_Gpu"] = "Только GPU (Vulkan)",
        ["Loc_AiBackend_Cpu"] = "Только CPU",
        ["Loc_Field_WeatherCity"] = "Город для погоды (пусто = авто):",
        ["Loc_Check_AiHotkey"] = "Кормить нейросеть выделенным текстом / буфером по горячей клавише",
        ["Loc_AiAction_Summarize"] = "Действие: Пересказать кратко",
        ["Loc_AiAction_Translate"] = "Действие: Перевести через ИИ",
        ["Loc_AiAction_FillPrompt"] = "Действие: Вставить в строку вопроса",

        // SettingsWindow — Tab 3: Music & Waves
        ["Loc_Sec_MusicIntegration"] = "ИНТЕГРАЦИЯ С ПЛЕЕРАМИ И УПРАВЛЕНИЕ НА ОСТРОВКЕ",
        ["Loc_Check_EnableMusic"] = "Включить захват звука WASAPI, звуковые волны и кнопки управления",
        ["Loc_Check_ShowAlbumArt"] = "Показывать мини-обложку альбома в компактном режиме",
        ["Loc_Check_Marquee"] = "Плавная прокрутка для длинных названий треков",
        ["Loc_Check_HideMusicPause"] = "Скрывать музыкальные волны после длительной паузы",
        ["Loc_Field_PauseDelay"] = "Задержка после паузы (с):",
        ["Loc_Field_AudioDevice"] = "Аудиоустройство захвата WASAPI Loopback:",
        ["Loc_Sec_WavePhysics"] = "ФИЗИКА ЗВУКОВЫХ ВОЛН (ЛОГАРИФМИЧЕСКИЙ FFT + БЕЗЬЕ)",
        ["Loc_Check_HideSilence"] = "Полностью гасить дыхание волны при тишине",
        ["Loc_Field_WaveStyle"] = "Стиль отрисовки волн:",
        ["Loc_WaveStyle_Smooth"] = "Плавная кривая Безье + гармоники (SmoothCurve)",
        ["Loc_WaveStyle_Bars"] = "Симметричные капсулы (Bars)",
        ["Loc_Field_WaveColorMode"] = "Цветовая схема волн:",
        ["Loc_WaveColor_Theme"] = "Под стиль программы (Авто)",
        ["Loc_WaveColor_Album"] = "Адаптивная под обложку трека (AlbumAdaptive)",
        ["Loc_WaveColor_Gradient"] = "Двухцветный градиент",
        ["Loc_WaveColor_Static"] = "Фиксированный цвет",
        ["Loc_Field_WaveBars"] = "Детализация спектра (6–32):",
        ["Loc_Field_WaveSens"] = "Отклик баса (0.3–5.0):",
        ["Loc_Field_WaveSmooth"] = "Инерция падения (0.05–0.92):",
        ["Loc_Field_PrimaryColor"] = "Основной цвет волн (нажмите на круг для палитры):",
        ["Loc_Field_SecondaryColor"] = "Второй цвет градиента (нажмите на круг):",
        ["Loc_Tooltip_ColorPicker"] = "Открыть палитру выбора цвета",
        ["Loc_ColorPicker_Title"] = "Палитра цвета волн",
    };

    public static string Get(string key)
    {
        var dict = CurrentLanguage == AppLanguage.Russian ? RussianStrings : EnglishStrings;
        if (dict.TryGetValue(key, out string? value))
            return value;
        if (EnglishStrings.TryGetValue(key, out string? fallback))
            return fallback;
        return key;
    }

    public static string Format(string key, params object[] args)
        => string.Format(CultureInfo.CurrentCulture, Get(key), args);

    public static string Pick(string english, string russian)
        => CurrentLanguage == AppLanguage.Russian ? russian : english;

    public static void ApplyLanguage(AppLanguage language)
    {
        CurrentLanguage = language;
        var dict = language == AppLanguage.Russian ? RussianStrings : EnglishStrings;

        var res = System.Windows.Application.Current?.Resources;
        if (res is not null)
        {
            foreach (var kv in dict)
            {
                res[kv.Key] = kv.Value;
            }
        }

        LanguageChanged?.Invoke(language);
    }
}
