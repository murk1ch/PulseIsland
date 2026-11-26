# Technical Specification
**Project Name:** **PulseIsland**  
**Version:** **2.0.0**  
**Target OS:** Windows 10 (19041+) / Windows 11  
**Tech Stack:** C# (.NET 8+), WPF (Zero-NuGet default build)  
**Deployment Directory (Portable Mode):** `<portable-root>`  

---

## 1. Project Objective
Develop a lightweight, resident, 100% portable Windows desktop application (**PulseIsland**) whose UI is designed as an interactive top-edge "Dynamic Island". PulseIsland provides neural screen OCR and translation via cursor selection, live music display and 60 FPS audio wave visualization, live microphone capture & 1-click audio output switching, in-island notifications, a multimodal local AI assistant with offline voice dictation, smart clipboard & file drop shelf tools, wireless mouse battery telemetry, and flexible settings.

---

## 2. Portable Storage & Isolation Requirements
To keep the system drive clean and make the app effortlessly portable across any Windows PC, PulseIsland operates in **strict Portable Mode**:
- **Application Root Folder:** `<portable-root>` (resolved dynamically via `PortablePaths.RootDirectory`).
- **No System-Drive Writes:** No configuration files, caches, logs, temporary files, or downloaded models may be saved to `%AppData%`, `%LocalAppData%`, system `%Temp%`, or `Documents`.
- **Local Directory Structure inside `<portable-root>`:**
  - `<portable-root>\PulseIsland.exe` — application executable and libraries.
  - `<portable-root>\config\` — `settings.json`, DPAPI-encrypted API keys, translation history, and clipboard text history.
  - `<portable-root>\models\` — all user-downloaded offline models and portable runtimes:
    - `models\_runtime\` — `onnxruntime.dll` (native C-API runtime).
    - `models\_ocr\` — PaddleOCR PP-OCRv5 Mobile ONNX models (`ppocrv5_det.onnx`, `ppocrv5_eslav_rec.onnx`, `ppocrv5_en_rec.onnx`, YAML dictionaries).
    - `models\_llama\` — portable `llama-server.exe` (Vulkan GPU / CPU).
    - `models\_whisper\` — portable `whisper-cli.exe` + `ggml-base.bin`.
    - `models\<model-id>\` — ONNX MarianMT translation packs and GGUF LLM / Vision VLM (`+ mmproj`) checkpoints.
  - `<portable-root>\cache\` — resumable download staging (`cache\downloads\*.part`), `weather_cache.json`, and `cache\temp\`.
  - `<portable-root>\logs\` — daily `LogService` diagnostic logs.
- **Zero Auto-Downloads:** Optional AI, OCR, translation, and voice models are never downloaded automatically in the background; downloads occur strictly on explicit user action in Settings.

---

## 3. Island UI, Onboarding & Live Activities

The island sits centered along the top edge of the screen above all windows (`Topmost`) with three core states and smooth transition animations:

1. **Hidden State (Tucked to Top Edge):**
   - Slides past the top edge, leaving a subtle hover trigger strip.
   - Hovering at the top edge smoothly reveals the island.
2. **Compact State (Base Pill):**
   - Sleek rounded pill supporting:
     - **Idle / Telemetry mode:** subtle minimal pill or optional system telemetry / wireless mouse battery badge.
     - **Playing music:** optional mini album art, marquee track title, and live 60 FPS audio spectrum waves.
     - **Live Activities:** active microphone capture indicator pill showing which application is using the mic (e.g., Discord, OBS, Teams) with 1-click Mute, plus priority indicators for active notifications or AI thinking.
3. **Expanded State (Full Dashboard):**
   - Opens on click (or optional hover) on the compact pill.
   - Contains:
     - **Settings button** (gear icon) — **visible strictly in the Expanded state**.
     - Expanded media player widget with seek bar and controls.
     - **Live Activities controls:** active microphone capture badge + 1-click Mute and **1-click Default Audio Output Switcher** (`IPolicyConfig` + real device names via `IPropertyStore` / `PropVariant`). *(Intentionally excludes Timer / Pomodoro / Stopwatch clutter.)*
     - Notifications panel, recent translations history with **live search** (`WS_EX_NOACTIVATE` focus toggling on search input), Smart Clipboard button, and File Drop Shelf.

**Onboarding & Localization Requirements:**
- **3-Step First-Launch Onboarding (`WelcomeOnboardingWindow`):** Live English / Russian language selector on Step 1, live preview of all 7 themes + DWM blur toggle, `Alt+Q` / Vision / Voice quick guide, and active modules checklist; reopenable anytime from Settings via the **Guide** button.
- **Live Bilingual UI (EN ⇄ RU):** English by default with instant live switching to Russian via `LocalizationService` (`{DynamicResource Loc_*}` + `LanguageChanged`) and responsive `Grid`/`WrapPanel` toolbars that prevent button overlap in both languages.
- Multi-monitor and Per-Monitor V2 DPI scaling support, plus optional automatic hiding in fullscreen apps.

---

## 4. Functional Requirements

### 4.1. Neural Screen OCR & Translation Module

#### 4.1.1. Activation & Selection Overlay (`LassoOverlayWindow`)
- **Global Hotkey:** Configurable global shortcut (default `Alt+Q`, plus clipboard translate `Ctrl+Alt+C`).
- Transparent fullscreen overlay with a dual-contrast **Pulse Orb** cursor and smooth 60 FPS fading trail.
- **Shrinkable Rectangle Selection (`ComputeRectangleBox`):**
  - **LMB drag:** capture region for OCR & translation.
  - **RMB drag** or **Shift+LMB drag:** capture region and send directly to **Vision AI** (`LassoCaptureIntent.AskAiAboutImage`).
  - Pressing `Esc` cancels selection.

#### 4.1.2. OCR & Translation Engines
- **OCR Engine Modes (`OcrEngineMode`):**
  1. `AutoSmart` (default): **PP-OCRv5 Mobile ONNX** (`PaddleOcrOnnxService` for cursive/handwriting, decorative fonts, `°C`, math formulas, tiny UI text, dark/gradient backgrounds) + adaptive `OcrImagePreprocessor` + `Windows.Media.Ocr`, with optional Vision VLM fallback when installed.
  2. `VisionLlmAlways`: routes capture directly through an installed local Vision VLM (`Qwen 2.5 VL`) with local OCR fallback.
  3. `WindowsOcrOnly`: uses `OcrImagePreprocessor` + `Windows.Media.Ocr` only.
- **Translation Modes (`TranslationEngineMode`):**
  1. `OnlineWindowsOcr` (Hybrid): online translation (Google Free / MyMemory race or DeepL with DPAPI-encrypted API key) with local fallbacks.
  2. `OfflineOnnx`: fully offline ONNX MarianMT (`OrtNativeRuntime` C-API + pure C# `SentencePieceMarianTokenizer`).
  3. `OfflineLocalLlm`: local GGUF LLM translation via `LocalLlmService`.
- Supports automatic source language detection (or manual override) and 16 target languages (`LanguageCatalog`).

#### 4.1.3. Result Display
1. **Floating `TranslationPopupWindow`** near the selected region (clamped to monitor bounds, draggable) featuring **1-click `⇄` language direction swap**, **`Ask AI` handoff**, **`Copy original`**, copy translation, and optional auto-hide.
2. **Expanded Island Translation History** with **live search** and quick copy.

---

### 4.2. Local AI Assistant, Vision VLMs & Offline Voice Dictation
- **Multi-Model GGUF Catalog (`LocalLlmCatalogService`):** Qwen 2.5 3B/7B, Qwen 3 4B, Gemma 2 9B, and multimodal Vision VLMs **Qwen 2.5 VL 3B & 7B** (with `--mmproj` and `-c 8192`) served locally by portable `llama-server.exe` on `127.0.0.1`.
- **Compute Backend:** `Auto` (Vulkan GPU auto-detection with sticky CPU fallback if Vulkan fails), `GpuVulkan`, or `CpuOnly`.
- **Interactive Assistant (`AiAssistantPopupWindow`, hotkey `Ctrl+Alt+A`):**
  - Multi-turn conversation memory (`AiChatTurn`, `StreamChatHistoryAsync`, `Chat: N` badge + `New chat`).
  - Rich Markdown + LaTeX Unicode + Markdown Table rendering (`AiResponseFormatter`) and **`Table → MD/CSV`** screenshot extraction.
  - **`Ctrl+V` screenshot paste** directly into AI chat.
  - Up to **8 custom quick-prompt chips** (`CustomAiPromptChip`, editable in Settings with bilingual built-in defaults).
  - **Offline Voice Dictation (`WhisperVoiceService`):** Win32 `waveIn` 16 kHz mono FIFO capture transcribed locally via `whisper.cpp` (`ggml-base.bin` in `models\_whisper\`).
  - Key-free live weather briefing (`WeatherService` via `wttr.in`).

---

### 4.3. Music & Audio Wave Visualization Module
- **SMTC Integration (`SmtcMediaService`):** Reads track title, artist, album art, dominant accent color, and seekable progress bar; controls `Play / Pause`, `Next`, `Previous` without triggering the Windows media OSD.
- **Live 60 FPS Audio Waves:** Native COM **WASAPI Loopback** (`WasapiAudioCaptureService`) + `AudioSessionMusicFilter` (ignores Discord/voice calls) + Radix-2 FFT (`FftSpectrumProcessor`).

---

### 4.4. Smart Clipboard, Drop Shelf & Hardware Telemetry
- **Smart Clipboard (`SmartClipboardService`, `SmartClipboardWindow`):** Persistent searchable/pinnable text history (`config\clipboard_history.json`) plus up to **8 recent screenshots in RAM** (pixel-hash deduplication) with 1-click `Ask AI` (Vision) and `Copy`.
- **File Drop Shelf:** Drag-and-drop file staging inside Expanded Island; image files expose instant quick actions (**`Ask AI`**, convert to **`PNG`**, compress to **`JPG 82%`**).
- **PC Telemetry & Wireless Mouse Battery (`SystemMonitorService`, `MouseBatteryService`):**
  - CPU, RAM, network speed, clock/date, PC battery, Uptime.
  - Wireless mouse battery monitoring over Bluetooth LE/Classic (`DEVPKEY_Device_ContainerId`), HID Power/Battery Pages `0x84`/`0x85`, Beken/Rongyuan `0x1D57` (Attack Shark X3/X6/X11/R1 + wired `PID_FA61`), Logitech HID++ 2.0 (with `0x8F`/`0xFF` error guard), Razer HyperSpeed, Compx/VXE/ATK/VGN/Darmoshark/Zaopin (`0x3554`/`0x25A7`/`0x373B`), and Lamzu/Pulsar/Nordic 4K/8K (`0x3710`/`0x3367`/`0x373E`) via heap-allocated Win32 `OVERLAPPED` + `CancelIoEx`.

---

## 5. Settings Module (`SettingsWindow`)
Accessible strictly from the **Expanded** island state (collapses the island when opened) with non-blocking async initialization (`OnWindowLoaded`), a **Guide** button to reopen `WelcomeOnboardingWindow`, and three tabs:
1. **General & Interface:** Live EN/RU language selector, 7 themes with live preview, DWM blur, custom corner radius/opacity, Smart Clipboard, Live Audio/Mic indicator, File Drop Shelf, system telemetry toggles, collapsible wireless mouse battery settings, notifications & chimes, autostart (`HKCU\...\Run`), target monitor, tray icon, and fullscreen behavior.
2. **Translator & AI:** Translation mode (`OnlineWindowsOcr` / `OfflineOnnx` / `OfflineLocalLlm`), OCR engine mode (`AutoSmart` / `VisionLlmAlways` / `WindowsOcrOnly`), source/target languages, hotkey recorders, PP-OCRv5 & ONNX MarianMT managers, Local AI GGUF/Vision catalog & Whisper voice manager, up to 8 editable custom AI prompt chips, encrypted DeepL key, cursor trail, and popup auto-hide.
3. **Music & Waves:** Enable/disable music module, auto-collapse delay on pause, audio output device selector, visualizer style, sensitivity, smoothing, bar count, wave colors, and compact album art / marquee toggles.

---

## 6. Non-Functional Requirements
- **Performance:** Idle CPU usage under 1–2%; smooth 60 FPS visualization; non-blocking UI startup and settings loading; zero memory leaks over extended sessions.
- **Security & Paths:** All paths resolved strictly via `PortablePaths`; API keys encrypted via Windows DPAPI (`CryptProtectData`/`CryptUnprotectData`).
- **Verification:** Covered by **36 portable automated unit & integration tests** (`tests\PulseIsland.Tests\PulseIslandTests.cs`) runnable offline without external NuGet restores.
