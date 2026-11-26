---
name: pulseisland-codebase-map
description: >-
  Architecture map and developer guide for the PulseIsland v2.0.0 codebase in <project-root>.
  Read this skill before modifying existing services, UI windows, settings, audio/FFT,
  OCR/translation pipelines, local AI/Vision/Whisper, or adding tests.
---

# PulseIsland v2.0 Codebase Map & Conventions

## 1. Build & Portable Isolation Rules
- **Solution:** `<project-root>\PulseIsland.sln`
- **Main App Project:** `<project-root>\src\PulseIsland\PulseIsland.csproj` (`Version`: `2.0.0`, `ApplicationIcon`: `..\..\assets\PulseIsland.ico`)
- **Published Portable Output:** `<project-root>\dist\PulseIsland.exe` (resolves `PortablePaths.RootDirectory` to `<project-root>` via upward `PulseIsland.sln` check in dev, or `AppContext.BaseDirectory` standalone on any Windows PC).
- **No Machine-Specific UI Labels:** Never hardcode developer paths or personal GPU names in XAML, C#, tests, or docs; resolve dynamically via `PortablePaths`.
- **Offline vs Full SDK Build:**
  - Default (`FullSdk=false`): Targets `net8.0-windows` with **zero external NuGet dependencies**. Uses native COM WASAPI (`WasapiComInterop.cs`), `AudioSessionMusicFilter.cs`, Radix-2 FFT (`FftSpectrumProcessor.cs`), Win32 P/Invoke (`Win32Api.cs`, `DwmBackdropService.cs`), DPAPI (`SecurityHelper.cs`), pure C# `OrtNativeRuntime.cs` (`onnxruntime.dll` C-API) + `SentencePieceMarianTokenizer.cs` + `PaddleOcrOnnxService.cs`, and an in-memory WinRT bridge for `Windows.Media.Ocr` / SMTC.
  - Full SDK (`-p:FullSdk=true`): Targets `net8.0-windows10.0.19041.0` and defines `WITH_WINRT;WITH_ONNX`.
- **WinForms & WPF Type Collisions:** `PulseIsland.csproj` removes implicit global usings for `System.Drawing` and `System.Windows.Forms`. Prefer WPF types (`System.Windows.Media.Color`, `Brush`, `Point`) and explicitly qualify colliding types (`System.Windows.MessageBox`, `System.Diagnostics.Process`).

## 2. Module Map (`src/PulseIsland/`)
- **`Core/`**
  - `PortablePaths.cs` — portable directories (`config`, `models`, `cache`, `cache\downloads`, `logs`).
  - `AppSettings.cs` — serializable settings (`config\settings.json`), including `UiLanguage` (`AppLanguage.English` default), `HasCompletedOnboarding`, `OcrMode`, `TranslationMode`, `SelectedAiModelId`, `CustomAiPrompts` (+ `GetDefaultCustomAiPrompts`, `IsBuiltInDefaultPromptSet`), themes, wave styles, smart clipboard, drop shelf, and mouse battery toggles.
  - `Models.cs` — core enums and records (`AppLanguage`, `IslandState`, `TranslationEngineMode` [`OnlineWindowsOcr`, `OfflineOnnx`, `OfflineLocalLlm`], `OcrEngineMode` [`AutoSmart`, `VisionLlmAlways`, `WindowsOcrOnly`], `OnlineTranslationProvider` [`GoogleFree`, `DeepL`], `DropShelfItem` with image quick-action flags, `CustomAiPromptChip`, `AiChatTurn`, `ClipboardImageEntry`, `MouseBatterySnapshot`, `IslandNotification`, etc.).
  - `CatalogModels.cs` — `LanguageCatalog` (16 languages, EN/RU display names) and disk/model DTOs (`DiskSpaceSummaryDto.FormatBytes` using `0.#` / `0.##`).
- **`Audio/` & `Media/`**
  - `WasapiComInterop.cs` — native Core Audio COM interfaces including `IMMDeviceEnumerator`, `IAudioClient`, `IAudioEndpointVolume`, `IPropertyStore`/`PropVariant`, and `IPolicyConfig` (`SetDefaultEndpoint`).
  - `AudioEndpointMonitorService.cs` — Core Audio endpoint volume/mute monitor, active microphone capture session detector (app name + 1-click Mute), real device names via `IPropertyStore`, and 1-click Default Audio Output Switcher via `IPolicyConfig`.
  - `WasapiAudioCaptureService.cs` — native COM WASAPI loopback capture into mono float ring buffer + device change listener.
  - `AudioSessionMusicFilter.cs` — Core Audio `IAudioSessionManager2`/`IAudioMeterInformation` session filter so visualizer waves react strictly to active music and ignore voice calls.
  - `FftSpectrumProcessor.cs` — Hann window + Radix-2 FFT + logarithmic band mapping + attack/release smoothing.
  - `IMediaSessionService.cs` & `SmtcMediaService.cs` — media metadata, track position/duration, and OSD-free playback controls.
- **`Services/`**
  - `LocalizationService.cs` — live zero-NuGet EN/RU localization (`AppLanguage.English` default), updating `Application.Current.Resources["Loc_*"]`, raising `LanguageChanged`, and exposing `Get` / `Format` / `Pick`.
  - `SettingsService.cs` — atomic load/save of `settings.json` + `SettingsChanged` event.
  - `ThemeService.cs` — 7 built-in themes + theme-adaptive wave colors + live preview.
  - `LocalLlmCatalogService.cs` — multi-model GGUF catalog (Qwen 2.5 3B/7B, Qwen 3 4B, Gemma 2 9B, Vision VLMs Qwen 2.5 VL 3B & 7B with `--mmproj`) + portable `llama-server.exe` in `models\_llama\`; strictly manual resumable downloads (zero auto-downloads).
  - `LocalLlmService.cs` — portable `llama-server.exe` manager on `127.0.0.1` (`--mmproj`, `-c 8192`, sticky Vulkan GPU → CPU fallback on crash) + SSE multi-turn streaming (`StreamChatHistoryAsync`).
  - `AiResponseFormatter.cs` — converts LLM LaTeX math, Markdown formatting, code fences, and Markdown Tables into clean Unicode and rich WPF `FlowDocument`.
  - `WhisperVoiceService.cs` — offline voice dictation via Win32 `waveIn` 16 kHz mono FIFO recorder + portable `whisper.cpp` (`models\_whisper\ggml-base.bin`).
  - `SmartClipboardService.cs` — persistent searchable/pinnable text history (`config\clipboard_history.json`) + bounded in-RAM screenshot history (`MaxImageEntries = 8`, pixel-hash deduplication).
  - `MouseBatteryService.cs` — native `setupapi.dll` + `hid.dll` + heap-allocated Win32 `OVERLAPPED`/`CancelIoEx` battery monitor: Bluetooth LE/Classic (`DEVPKEY_Device_ContainerId`), HID `0x84`/`0x85`, Beken `0x1D57` (`PID_FA60` Interrupt-IN + wired `PID_FA61`), Logitech HID++ 2.0 (`0x8F`/`0xFF` guard), Razer HyperSpeed, Compx/VXE/ATK/VGN/Darmoshark/Zaopin (`0x3554`/`0x25A7`/`0x373B`), and Lamzu/Pulsar/Nordic 4K/8K (`0x3710`/`0x3367`/`0x373E`).
  - `IslandNotificationService.cs`, `WindowsNotificationListener.cs`, `WindowsToastSuppressor.cs`, `CornerPopupInterceptor.cs`, `NotificationSoundService.cs`, `NotificationActivator.cs` — notification capture, suppression, chimes, and chat activation.
  - `OfflineModelCatalogService.cs`, `WeatherService.cs`, `SecurityHelper.cs`, `AlbumColorHelper.cs`, `AutoStartService.cs`, `SystemMonitorService.cs`, `TranslationHistoryService.cs`, `LogService.cs`.
- **`Interop/`**
  - `Win32Api.cs` — P/Invoke helpers (`RegisterHotKey`, `SetWindowPos`, `GetDpiForMonitor`, `GetWindowLong`/`SetWindowLong`, `BitBlt`, `GetDIBits`, `CreateRoundRectRgn`/`SetWindowRgn`).
  - `DwmBackdropService.cs` — DWM acrylic blur (`ACCENT_ENABLE_BLURBEHIND` + `SetWindowRgn`), `ApplyToCard`, and `GetParentSafe` for safe mixed `Visual` / `ContentElement` (`FlowDocument`) hit-testing.
  - `MonitorService.cs`, `HotkeyService.cs`, `ScreenCaptureService.cs`, and `LassoGeometryHelper.cs` (`ComputeBoundingBox`, shrinkable `ComputeRectangleBox`, `ExpandRectClamped`).
- **`Translation/`**
  - `PaddleOcrOnnxService.cs` — offline PP-OCRv5 Mobile ONNX neural OCR (`models\_ocr\`, handles cursive/handwriting, decorative fonts, `°C`, math, tiny text).
  - `OcrImagePreprocessor.cs` — pure C# adaptive preprocessing (polarity normalization, foreground color isolation, contrast/sharpening, quiet margins).
  - `WindowsOcrService.cs`, `OnlineTranslationService.cs` (Google/MyMemory parallel race + DeepL DPAPI), `OrtNativeRuntime.cs` (`models\_runtime\onnxruntime.dll`), `SentencePieceMarianTokenizer.cs`, `OnnxOfflineTranslationService.cs`, `BuiltInLocalTranslator.cs`, `HuggingFaceDownloader.cs`, and `TranslationCoordinator.cs` (routes `OcrEngineMode` & `TranslationEngineMode`).
- **Root Entry (`src/PulseIsland/`):**
  - `App.xaml` & `App.xaml.cs` — single-instance mutex/wake-up IPC, service wiring, global hotkeys (`Alt+Q`, `Ctrl+Alt+C`, `Ctrl+Alt+A`), WinForms system tray menu with live `LanguageChanged` rebuild, and onboarding/settings window orchestration.
- **`ViewModels/` & `UI/`**
  - `IslandViewModel.cs` — reactive island state (`IsSettingsButtonVisible`, Live Activities mic/output properties, translation search filter, notifications, drop shelf, telemetry, mouse battery; intentionally no timer/Pomodoro state).
  - `IslandWindow.xaml(.cs)` — top-edge Dynamic Island pill (`Hidden`, `Compact`, `Expanded`), active mic capture pill + 1-click Mute, 1-click audio output switcher, live translation history search with `WS_EX_NOACTIVATE` focus toggling, Drop Shelf image quick actions (`Ask AI`, `PNG`, `JPG 82%`), 60 FPS visualizer, and `WM_DEVICECHANGE` hotplug refresh.
  - `WelcomeOnboardingWindow.xaml(.cs)` — 3-step first-launch wizard (live EN/RU selector, 7 themes + DWM blur, `Alt+Q`/Vision/Voice guide, active modules checklist; reopenable via Settings → Guide).
  - `LassoOverlayWindow.xaml(.cs)` — dual-contrast Pulse Orb cursor, shrinkable rectangle (`LMB` translate, `RMB drag`/`Shift+LMB` Vision AI).
  - `TranslationPopupWindow.xaml(.cs)` — draggable result card with 1-click `⇄` direction swap, `Ask AI` handoff, and `Copy original`.
  - `AiAssistantPopupWindow.xaml(.cs)` — multi-turn AI chat (`Chat: N` + `New chat`), Markdown/LaTeX/Table `FlowDocument` viewer, `Table → MD/CSV`, `Ctrl+V` screenshot paste, 8 custom prompt chips, offline Whisper dictation, and weather.
  - `SmartClipboardWindow.xaml(.cs)` — searchable/pinnable text history + 8-screenshot RAM strip with 1-click `Ask AI` & `Copy`.
  - `SettingsWindow.xaml(.cs)` — tabbed settings UI with async non-blocking `OnWindowLoaded`, Guide button, responsive EN/RU toolbars, OCR/AI/Whisper/ONNX managers, custom prompt chip editor, and collapsible mouse battery panel.
