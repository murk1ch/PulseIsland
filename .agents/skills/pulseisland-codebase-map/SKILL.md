---
name: pulseisland-codebase-map
description: >-
  Architecture map and developer guide for the PulseIsland codebase in <project-root>.
  Read this skill before modifying existing services, UI windows, settings, audio/FFT,
  translation pipelines, or adding tests.
---

# PulseIsland Codebase Map & Conventions

## 1. Build & Portable Isolation Rules
- **Solution:** `<project-root>\PulseIsland.sln`
- **Main App Project:** `<project-root>\src\PulseIsland\PulseIsland.csproj` (`ApplicationIcon`: `..\..\assets\PulseIsland.ico`)
- **Published Portable Output:** `<project-root>\dist\PulseIsland.exe` (framework-dependent Release publish; resolves `PortablePaths.RootDirectory` to `<project-root>` via upward `PulseIsland.sln` check in dev, or `AppContext.BaseDirectory` when distributed standalone on any PC).
- **No Machine-Specific UI Labels:** Never hardcode developer paths (e.g. `<project-root>`) in user-facing XAML or runtime status messages; always resolve dynamically via `PortablePaths`.
- **Offline vs Full SDK Build:**
  - Default (`FullSdk=false`): Targets `net8.0-windows` with **zero external NuGet dependencies**. Uses native COM WASAPI (`WasapiComInterop.cs`), `AudioSessionMusicFilter.cs`, Radix-2 FFT (`FftSpectrumProcessor.cs`), Win32 P/Invoke (`Win32Api.cs`, `DwmBackdropService.cs`), DPAPI (`SecurityHelper.cs`), pure C# `OrtNativeRuntime.cs` (`onnxruntime.dll` C-API) + `SentencePieceMarianTokenizer.cs`, and an in-memory WinRT PowerShell bridge for `Windows.Media.Ocr` / SMTC.
  - Full SDK (`-p:FullSdk=true`): Targets `net8.0-windows10.0.19041.0` and defines `WITH_WINRT;WITH_ONNX`.
- **WinForms & WPF Type Collisions:**
  - `PulseIsland.csproj` removes implicit global usings for `System.Drawing` and `System.Windows.Forms`. Always use WPF types (`System.Windows.Media.Color`, `Brush`, `System.Windows.Point`) by default, and qualify WinForms/Drawing explicitly (`System.Windows.MessageBox`, `System.Diagnostics.Process`).

## 2. Module Map (`src/PulseIsland/`)
- **`Core/`**
  - `PortablePaths.cs` — portable directory structure (`config`, `models`, `cache`, `logs`, `models\_runtime`).
  - `AppSettings.cs` — serializable settings model (`config/settings.json`), including `UiLanguage` (`AppLanguage.English` default), themes, wave styles, design/animation toggles, translation mode, smart clipboard, and file shelf toggles.
  - `Models.cs` — core enums and records (`AppLanguage`, `IslandState`, `MediaTrackInfo`, `ClipboardEntry`, `DropShelfItem`, `TranslationRecord`, `OcrResult`, `TranslationResult`, `IslandNotification`, `NotificationKind`).
  - `CatalogModels.cs` — `LanguageCatalog` (16 languages, localized EN/RU display names) and model/disk DTOs (`DiskSpaceSummaryDto.FormatBytes` using `0.#` / `0.##` format specifiers).
- **`Services/`**
  - `LocalizationService.cs` — zero-NuGet dynamic EN/RU localization engine (`AppLanguage.English` default), updating `Application.Current.Resources["Loc_*"]` live for `{DynamicResource Loc_*}` WPF bindings, firing `LanguageChanged`, and exposing `Get` / `Format` / `Pick`.
  - `SettingsService.cs` — atomic load/save of `settings.json` + `SettingsChanged` event.
  - `ThemeService.cs` — 7 built-in themes (`Classic`, `CrimsonBlack`, `IndigoAmber`, `NavyMagenta`, `GraphiteSand`, `TealOrange`, `LightWhite`) + theme-adaptive wave colors + live preview.
  - `IslandNotificationService.cs` — manages active notification banner, auto-dismiss timer, and bounded history (up to 50 items).
  - `WindowsNotificationListener.cs` — event-driven WNF (`WNF_SHEL_TOAST_PUBLISHED`) + persistent WinRT `UserNotificationListener` worker with Telegram media tag parsing (`Voice Message`, `Video Message`, `Photo`, `Video`).
  - `WindowsToastSuppressor.cs` — suppresses Windows default bottom-right toast popups while PulseIsland is active (`IQuietHoursSettings` → `Microsoft.QuietHoursProfile.AlarmsOnly` + `ShellExperienceHost` window suppression) and restores on exit.
  - `CornerPopupInterceptor.cs` — Win32 `SetWinEventHook(EVENT_OBJECT_SHOW)` interceptor that catches and hides leftover custom bottom-right desktop popups instantaneously across all monitors.
  - `NotificationSoundService.cs` — zero-dependency in-memory WAV synthesizer and preview player for notification chimes (`PulseChime`, `CrystalDrop`, `SoftBubble`, `CosmicPing`, `SystemAsterisk`, `None`).
  - `NotificationActivator.cs` — click-to-open notification handler (`IApplicationActivationManager` + direct Telegram Desktop chat search/focus activation).
  - `WeatherService.cs` — key-free `wttr.in` weather fetcher cached strictly in `cache\weather_cache.json`.
  - `LocalLlmCatalogService.cs` — manages resumable download of `Qwen2.5-3B-Instruct-Q4_K_M.gguf` into `models\qwen2.5-3b-instruct\` and `llama-server` (Vulkan GPU / CPU) into `models\_llama\`.
  - `LocalLlmService.cs` — runs portable `llama-server.exe` (Auto GPU Vulkan -> CPU fallback) and streams SSE chat responses for weather, summaries, and translation.
  - `SecurityHelper.cs` — Windows DPAPI (`CryptProtectData`/`CryptUnprotectData`) for API keys.
  - `AlbumColorHelper.cs` — dominant vivid color extraction from album art `BitmapSource`.
  - `AutoStartService.cs` — manages `HKCU\...\Run` registry entry pointing to current `PulseIsland.exe`.
  - `OfflineModelCatalogService.cs` — manages `BuiltInCatalog` (`opus-mt-en-ru`, `opus-mt-ru-en`, `opus-mt-de-ru`, `opus-mt-mul-en`), checks `HasCoreModelWeights` / `IsModelDirectoryComplete` (`encoder_model.onnx`, `decoder_model.onnx`, `source.spm`, `vocab.json`, `config.json`), skips `_`-prefixed folders (`_runtime`), and skips already-downloaded files during download.
  - `SmartClipboardService.cs` — clipboard history with search and pinning.
  - `FileDropShelfService.cs` — drag-and-drop file shelf inside expanded island.
  - `SystemMonitorService.cs` — lightweight CPU/RAM/network speed/PC battery/uptime sampler.
  - `VolumeMicMonitorService.cs` — system volume & microphone mute state indicator.
  - `AudioEndpointMonitorService.cs` — Core Audio endpoint volume/mute change monitor (`MMDeviceEnumerator`) for compact Volume/Mic HUD toasts.
  - `MouseBatteryService.cs` — native zero-NuGet `setupapi.dll` + `hid.dll` + Win32 `OVERLAPPED`/`CancelIoEx` wireless mouse battery service supporting Bluetooth (`DEVPKEY_Device_ContainerId`-grouped `DEVPKEY_Device_BatteryPlusCharging`) and 2.4 GHz USB dongles (`0x84`/`0x85` HID Power/Battery Pages, Beken `0x1D57` `PID_FA60` Interrupt-IN `0x03 ... 0x40/0x41` with Attack Shark X3 `0x4010` 1..10 step scaling + wired charging `PID_FA61`, Logitech HID++ 2.0 via Win32 overlapped I/O, Razer HyperSpeed).
  - `TranslationHistoryService.cs` — persists translation history cleanly in `config\`.
  - `LogService.cs` — thread-safe daily log writer to `logs\`.
- **`Interop/`**
  - `Win32Api.cs` — P/Invoke (`RegisterHotKey`, `SetWindowPos`, `GetDpiForMonitor`, `GetWindowLong`/`SetWindowLong`, `BitBlt`, `GetDIBits`, `GetForegroundWindow`, `CreateRoundRectRgn`/`SetWindowRgn`).
  - `DwmBackdropService.cs` — real acrylic/glass backdrop via `SetWindowCompositionAttribute(ACCENT_ENABLE_BLURBEHIND)` + `SetWindowRgn` rounded region + translucent card alpha (`ApplyToCard`).
  - `MonitorService.cs` — multi-monitor physical/DIP bounds, `PerMonitorV2` DPI scaling, fullscreen detection.
  - `HotkeyService.cs` — `WndProc` `WM_HOTKEY` listener and gesture parser (`Alt+Q`, `Ctrl+Alt+C`, etc.).
  - `LassoGeometryHelper.cs` — clamped `Int32Rect` bounding box from mouse trail points.
  - `ScreenCaptureService.cs` — screen capture into in-memory `BGRA32` pixels.
- **`Audio/` & `Media/`**
  - `WasapiComInterop.cs` & `WasapiAudioCaptureService.cs` — native COM WASAPI loopback capture into mono float ring buffer + device change listener.
  - `AudioSessionMusicFilter.cs` — Core Audio `IAudioSessionManager2`/`IAudioMeterInformation` session filter so visualizer waves react strictly to active music and ignore Discord/voice calls.
  - `FftSpectrumProcessor.cs` — Hann window + Radix-2 FFT + logarithmic band mapping + attack/release smoothing.
  - `IMediaSessionService.cs` & `SmtcMediaService.cs` — media metadata, track position/duration, and playback controls.
- **`Translation/`**
  - `WindowsOcrService.cs` — OCR from `BGRA32` pixels.
  - `OnlineTranslationService.cs` — Google Free, MyMemory & DeepL REST translation (`allowBuiltInDictionaryFallback` parameter).
  - `OrtNativeRuntime.cs` — zero-NuGet native `OrtApi` C-API (1.20.1) wrapper loading `models\_runtime\onnxruntime.dll` explicitly via `NativeLibrary.Load` + `EnsureRuntimeAvailableAsync`.
  - `SentencePieceMarianTokenizer.cs` — pure C# SentencePiece `.spm` protobuf Unigram Viterbi tokenizer + `vocab.json` + `config.json` + `PreprocessInputText`.
  - `OnnxOfflineTranslationService.cs` — seq2seq encoder/decoder ONNX inference + chunking + `EnsureModelSupportFilesAsync` diagnostic errors.
  - `BuiltInLocalTranslator.cs` — emergency EN↔RU dictionary fallback used only when online fails and no ONNX model is installed.
  - `HuggingFaceDownloader.cs` — resumable HTTP Range downloader staging `.part` files in `cache\downloads\`.
  - `TranslationCoordinator.cs` — routes `OfflineOnnx` strictly to ONNX, and `OnlineWindowsOcr` (Hybrid) in order: Online → Installed ONNX → `BuiltInLocalTranslator`.
- **`ViewModels/` & `UI/`**
  - `IslandViewModel.cs` — reactive state (`IsSettingsButtonVisible` true only when `Expanded`).
  - `IslandWindow.xaml(.cs)` — top-edge Dynamic Island pill (`Hidden`, `Compact`, `Expanded`), full-pill click-to-expand, dynamic `CalculateExpandedHeight()` via `ExpandedContent.Measure(...)`, smooth accordion transition (`AnimateAccordionSections`) between translation/notification history, 60 FPS `WaveCanvasRenderer`, marquee title, track progress, file drop shelf, smart clipboard button, mouse battery indicator (compact when music inactive + expanded), and `WM_DEVICECHANGE` (`0x0219`) hotplug refresh.
  - `LassoOverlayWindow.xaml(.cs)` — fullscreen transparent capture overlay with 60 FPS fading cursor trail and LMB lasso.
  - `TranslationPopupWindow.xaml(.cs)` — draggable (`DragMove()`) floating translation card positioned on target monitor.
  - `AiAssistantPopupWindow.xaml(.cs)` — draggable floating local AI (`Qwen 2.5 3B`) response/weather/summary widget with custom dark scrollbar.
  - `SmartClipboardWindow.xaml(.cs)` — searchable smart clipboard history popup.
  - `SettingsWindow.xaml(.cs)` — tabbed settings UI, 2-column footer preventing button overlap, interactive hotkey recorder, model & local LLM manager, collapsible mouse battery settings (`MouseBatteryDetailsPanel`), and `App.xaml.cs` auto-collapses expanded island when settings open.
