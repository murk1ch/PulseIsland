# GOAL.md — Vision, Architecture & Subsystem Status for PulseIsland (v2.0.0)

## Core Vision
Build a lightweight, 100% portable ("Zero-Install" — all data stored strictly inside `<portable-root>` with zero clutter in `%AppData%`, `%LocalAppData%`, or system `%Temp%`), visually polished Dynamic Island desktop utility for Windows 10/11 — **PulseIsland** — uniting live media playback & 60 FPS audio visualization, live microphone & audio output controls, system notifications, neural screen OCR & offline translation, a multimodal local AI assistant with offline voice dictation, and wireless hardware telemetry without bloated drivers or leaking user data to external servers.

---

## Current Subsystem Status (v2.0.0)

### 1. Dynamic Island UI, Onboarding & Live Bilingual UI (`UI/IslandWindow.xaml(.cs)`, `UI/WelcomeOnboardingWindow.xaml(.cs)`, `ThemeService.cs`, `DwmBackdropService.cs`, `LocalizationService.cs`)
- **Completed:**
  - Three states: `Hidden` (tucked into the top edge with hover trigger), `Compact` (top-center pill), `Expanded` (full dashboard with dynamic height calculation).
  - Real Windows DWM acrylic backdrop blur + 7 built-in themes + custom corner radius and opacity + responsive EN/RU `Grid`/`WrapPanel` headers and toolbars.
  - Full live bilingual localization (**English default**, instant live switch to Russian via `{DynamicResource Loc_*}` + `LocalizationService.LanguageChanged` with 100% EN/RU dictionary key parity).
  - 3-step first-launch onboarding wizard (`WelcomeOnboardingWindow`: live EN/RU selector, 7 themes live preview, DWM blur toggle, `Alt+Q` + Vision + Voice guide, active modules checklist), also reopenable anytime via the **Guide** button in Settings.

### 2. Live Activities in the Island (`Audio/AudioEndpointMonitorService.cs`, `Audio/WasapiComInterop.cs`, `UI/IslandWindow.xaml(.cs)`, `ViewModels/IslandViewModel.cs`)
- **Completed:**
  - Active microphone capture indicator pill in both Compact and Expanded island states showing which application is actively capturing audio (e.g., Discord, OBS, Teams) + 1-click Mute toggle.
  - 1-click Default Audio Output Switcher via COM `IPolicyConfig` (`SetDefaultEndpoint`) with real endpoint names read via `IPropertyStore` / `PropVariant`.
  - Intentionally clean design: **no** Timer / Pomodoro / Stopwatch clutter in the island.

### 3. Music & 60 FPS Visualizer (`Audio/`, `Media/`)
- **Completed:**
  - Native COM WASAPI loopback without NAudio + `AudioSessionMusicFilter` (reacts strictly to active music playback, ignores voice calls/Discord).
  - Radix-2 FFT (`FftSpectrumProcessor`) with logarithmic band mapping and attack/release smoothing (`SmoothCurve`, `Bars`).
  - Windows SMTC integration (`SmtcMediaService`): album art, dominant accent glow, track seek/progress bar, and playback controls without triggering the Windows system OSD.

### 4. In-Island System Notifications (`IslandNotificationService`, `WindowsNotificationListener`, `WindowsToastSuppressor`, `CornerPopupInterceptor`, `NotificationActivator`, `NotificationSoundService`)
- **Completed:**
  - Event-driven Windows notification listener (WNF + `UserNotificationListener`), Telegram media tag parsing, and click-to-open chat activation for Telegram/Discord.
  - Suppression of native Windows bottom-right toasts and interception of leftover corner popups.
  - Zero-dependency in-memory WAV chime synthesizer (`NotificationSoundService`).

### 5. Neural Screen OCR & Translator (`Translation/`, `LassoOverlayWindow`, `TranslationPopupWindow`)
- **Completed:**
  - `Alt+Q` dual-contrast Pulse Orb cursor with shrinkable rectangle selection (`LassoGeometryHelper.ComputeRectangleBox`): **LMB drag** to translate, **RMB drag / Shift+LMB** to ask Vision AI about any screen region.
  - Three OCR modes (`OcrEngineMode`: `AutoSmart`, `VisionLlmAlways`, `WindowsOcrOnly`) combining **PP-OCRv5 Mobile ONNX** (`PaddleOcrOnnxService` + `OcrImagePreprocessor` for cursive/handwriting, decorative fonts, `°C`, math formulas, tiny UI text, and dark/gradient backgrounds), `Windows.Media.Ocr`, and optional Vision VLM fallback.
  - Three translation modes (`TranslationEngineMode`): `OnlineWindowsOcr` (Hybrid: Google / MyMemory / DeepL with DPAPI key encryption + local fallback), `OfflineOnnx` (MarianMT via `OrtNativeRuntime` C-API + pure C# `SentencePieceMarianTokenizer`), and `OfflineLocalLlm` (local LLM translation).
  - Floating `TranslationPopupWindow` with 1-click `⇄` language direction swap, `Ask AI` handoff, and `Copy original`, plus live search inside Expanded Island translation history (`WS_EX_NOACTIVATE` focus toggling).

### 6. Local AI Assistant, Vision VLMs & Offline Voice (`LocalLlmService`, `LocalLlmCatalogService`, `AiResponseFormatter`, `WhisperVoiceService`, `AiAssistantPopupWindow`, `WeatherService`)
- **Completed:**
  - Multi-model GGUF catalog via portable `llama-server.exe` on `127.0.0.1` (**Qwen 2.5 3B/7B**, **Qwen 3 4B**, **Gemma 2 9B**, and Vision VLMs **Qwen 2.5 VL 3B & 7B** with `--mmproj` and `-c 8192`), with automatic sticky Vulkan GPU → CPU fallback on crash and **zero automatic downloads**.
  - Multi-turn conversation memory (`AiChatTurn`, `StreamChatHistoryAsync`, `Chat: N` badge + `New chat`), rich Markdown + LaTeX Unicode + Markdown Table rendering (`AiResponseFormatter`), `Table → MD/CSV` screenshot export, `Ctrl+V` screenshot paste, and up to 8 editable custom quick-prompt chips (`CustomAiPromptChip`, bilingual built-in defaults).
  - Offline voice dictation (`WhisperVoiceService`): Win32 `waveIn` 16 kHz mono recorder + portable `whisper.cpp` (`ggml-base.bin` in `models\_whisper\`).
  - Live key-free weather (`wttr.in`) cached in `cache\weather_cache.json`.

### 7. Smart Clipboard Image History, Drop Shelf & Wireless Mouse Battery (`SmartClipboardService`, `SmartClipboardWindow`, `SystemMonitorService`, `MouseBatteryService`)
- **Completed:**
  - Searchable/pinnable Smart Clipboard text history plus up to **8 recent screenshots in RAM** (pixel-hash deduplication) with 1-click `Ask AI` (Vision) and `Copy`.
  - Expanded Island File Drop Shelf with instant image quick actions (`Ask AI`, convert to `PNG`, compress to `JPG 82%`).
  - PC telemetry badges (CPU, RAM, network speed, clock/date, PC battery, Uptime) and non-blocking async Settings UI (`OnWindowLoaded`).
  - Wireless mouse battery monitoring across Bluetooth LE/Classic (`DEVPKEY_Device_ContainerId`), HID Power/Battery Pages `0x84`/`0x85`, Beken/Rongyuan `0x1D57` (Attack Shark X3/X6/X11/R1 + wired charging `PID_FA61`), Logitech HID++ 2.0 (with `0x8F`/`0xFF` error guard), Razer HyperSpeed, **Compx / VXE / ATK / VGN / Darmoshark / Zaopin** (`0x3554`/`0x25A7`/`0x373B`), and **Lamzu / Pulsar / Nordic 4K/8K** (`0x3710`/`0x3367`/`0x373E`) — all using heap-allocated Win32 `OVERLAPPED` + `CancelIoEx`.

### 8. Automated Verification (`tests/PulseIsland.Tests/PulseIslandTests.cs`)
- **Completed:**
  - **36 portable unit & integration tests** passing cleanly (`0 failed`) both locally and on fresh CI clones with zero hardcoded drives or machine-specific paths.

---

## Future Roadmap Ideas
1. **Additional Hardware & Peripheral Support:** Extend `MouseBatteryService.cs` with community-contributed 2.4 GHz HID protocol parsers (e.g., Corsair, SteelSeries, Endgame Gear, Keychron) while keeping heap-allocated `OVERLAPPED` + `CancelIoEx` safety and unit test coverage.
2. **Additional UI Languages:** Build on `LocalizationService`'s key-parity architecture and automated parity test to support additional community translations beyond English and Russian.
3. **Offline Model Ecosystem & Performance:** Keep `LocalLlmCatalogService`, `PaddleOcrOnnxService`, and `WhisperVoiceService` updated with newer compact GGUF / ONNX checkpoints while preserving strict zero-auto-download and portable-isolation guarantees.
4. **Multi-Monitor & Mixed-DPI UX Polish:** Continue ensuring every window/popup respects all 7 themes, DWM blur, custom scrollbars, `DwmBackdropService.GetParentSafe`, responsive EN/RU layouts, and crisp Per-Monitor V2 DPI rendering.
