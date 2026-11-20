# GOAL.md — Vision, Architecture & Subsystem Status for PulseIsland

## Core Vision
Build a lightweight, 100% portable ("Zero-Install" — all data stored strictly inside the app's own folder with zero clutter in `%AppData%`), visually polished Dynamic Island desktop utility for Windows 10/11 — **PulseIsland** — uniting music playback & live visualization, system notifications, instant OCR/offline translation, a local AI assistant, and hardware telemetry without bloated drivers or leaking user data to external servers.

---

## Current Subsystem Status

### 1. Dynamic Island UI & Styling (`UI/IslandWindow.xaml(.cs)`, `ThemeService.cs`, `DwmBackdropService.cs`, `LocalizationService.cs`)
- **Completed:**
  - Three states: `Hidden` (tucked into the top edge with hover trigger), `Compact` (top-center pill), `Expanded` (full dashboard with dynamic height calculation).
  - Real Windows DWM acrylic backdrop blur + 7 built-in themes + custom corner radius and opacity.
  - Smooth fluid animations (accordion transition between notifications and translator history, Blur-in / Scale materialization).
  - Full live bilingual localization (**English default**, instant switch to Russian in Settings).

### 2. Music & 60 FPS Visualizer (`Audio/`, `Media/`)
- **Completed:**
  - Native COM WASAPI loopback without NAudio + `AudioSessionMusicFilter` (reacts strictly to music playback, ignores voice calls/Discord).
  - Radix-2 FFT with logarithmic band mapping and attack/release smoothing (`SmoothCurve`, `Bars`, etc.).
  - Windows SMTC integration (`SmtcMediaService`): album art, dominant accent glow, track seek/progress bar, and playback buttons without triggering the Windows system OSD.

### 3. In-Island System Notifications (`IslandNotificationService`, `WindowsNotificationListener`, `WindowsToastSuppressor`, `CornerPopupInterceptor`)
- **Completed:**
  - Event-driven Windows notification listener (WNF + `UserNotificationListener`), Telegram media tag parsing, and click-to-open chat activation for Telegram/Discord.
  - Suppression of native Windows bottom-right toasts and interception of custom corner popups.
  - Zero-dependency in-memory WAV chime synthesizer (`NotificationSoundService`).

### 4. Screen Translator (`Translation/`, `LassoOverlayWindow`, `TranslationPopupWindow`)
- **Completed:**
  - Freehand lasso screen selection with a 60 FPS fading cursor trail + hotkey clipboard translation.
  - Local `Windows.Media.Ocr` + selectable backend: online (Google / MyMemory / DeepL with Windows DPAPI key encryption) or fully offline ONNX MarianMT (`OrtNativeRuntime` C-API + pure C# `SentencePieceMarianTokenizer`).
  - Resumable Hugging Face model downloads (`Range` requests) directly from Settings.

### 5. Local AI Assistant (`LocalLlmService`, `LocalLlmCatalogService`, `AiAssistantPopupWindow`, `WeatherService`)
- **Completed:**
  - Fully local inference using `Qwen 2.5 3B Instruct` (`Q4_K_M` GGUF) via portable `llama-server.exe` with automatic Vulkan GPU → CPU selection.
  - One-click resumable download of both the model and runtime in Settings.
  - Dedicated hotkey ("feed to AI"), text summarization, translation, live weather (`wttr.in`), floating `AiAssistantPopupWindow` with custom dark scrollbar, and priority "AI thinking" status indicator on the compact island.

### 6. Smart Island Features & Mouse Battery (`SystemMonitorService`, `MouseBatteryService`, `SmartClipboardWindow`)
- **Completed:**
  - PC telemetry (CPU, RAM, network speed, clock/date, PC battery, Uptime), File Drop Shelf, and searchable/pinnable Smart Clipboard.
  - Collapsible wireless mouse battery settings panel with live charge status: Bluetooth (`DEVPKEY_Device_ContainerId` + `DEVPKEY_Device_BatteryPlusCharging`) and 2.4 GHz (HID `0x84`/`0x85`, Logitech HID++, Razer HyperSpeed, Beken `VID_1D57&PID_FA60` Interrupt-IN + wired charging `PID_FA61` via `WM_DEVICECHANGE` and `CancelIoEx`), vector lightning icon (pulsing while charging), and configurable low-battery sound alert.

---

## Future Roadmap Ideas
1. **Expand 2.4 GHz Wireless Mouse Support:** Add new vendor/chipset protocols (e.g., Nordic / Compx / VXE / Lamzu / Pulsar) in `MouseBatteryService.cs`, preserving safe `OVERLAPPED` + `CancelIoEx` I/O and covering packet parsers with unit tests.
2. **Portable Distribution & Automated Testing:** Maintain 100% pass rate across `tests\PulseIsland.Tests`, verify zero writes outside the portable root (`file-system-audit`), and keep `dist\` publishing clean.
3. **UX Polish:** Ensure any new windows/popups support all 7 themes, live EN/RU localization, DWM blur, window dragging (`DragMove`), custom scrollbars, and crisp rendering on multi-monitor mixed-DPI setups.
