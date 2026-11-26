# AGENT.md — Agent Guide for PulseIsland (v2.0.0)

This file is the primary entry point for any AI coding agent starting work in the PulseIsland repository (`<project-root>`). Read it completely before making any edits.

---

## 1. Project Overview

**PulseIsland v2.0.0** is a portable Windows 10/11 desktop application (`.NET 8 WPF`) implementing a top-edge Dynamic Island overlay with the following subsystems:
- **Live Bilingual UI & 3-Step Onboarding:** `LocalizationService` (`AppLanguage.English` default, live EN ⇄ RU switching via `{DynamicResource Loc_*}` + `LanguageChanged`), 7 themes, DWM acrylic backdrop blur, and `WelcomeOnboardingWindow` (reopenable via Settings → Guide);
- **Live Activities:** Active microphone capture indicator pill (showing which app is recording + 1-click Mute) and 1-click Default Audio Output Switcher via COM `IPolicyConfig` (`AudioEndpointMonitorService`). *(Design rule: intentionally NO Timer / Pomodoro / Stopwatch clutter in the island.)*
- **Music & 60 FPS Visualizer:** Native COM WASAPI loopback + `AudioSessionMusicFilter` + Radix-2 FFT + Windows SMTC playback controls without triggering the system OSD;
- **In-Island System Notifications:** WNF + `UserNotificationListener` + Windows toast suppression + corner popup interception + click-to-open chat activation + in-memory WAV chimes;
- **Neural OCR & Screen Translator:** `Alt+Q` dual-contrast Pulse Orb overlay with shrinkable rectangle (`LMB` translate, `RMB drag` / `Shift+LMB` Vision AI), PP-OCRv5 Mobile ONNX (`PaddleOcrOnnxService` + `OcrImagePreprocessor`) + `Windows.Media.Ocr` + Vision AI (`OcrEngineMode`), 3 translation modes (`OnlineWindowsOcr` Hybrid, `OfflineOnnx` MarianMT, `OfflineLocalLlm`), floating card with 1-click `⇄` swap / `Ask AI` / `Copy original`, and live search in Expanded Island history;
- **Local AI Assistant, Vision & Offline Voice:** Multi-model GGUF catalog (Qwen 2.5 3B/7B, Qwen 3 4B, Gemma 2 9B, Vision VLMs Qwen 2.5 VL 3B/7B with `--mmproj` and `-c 8192`, sticky Vulkan GPU → CPU fallback, zero auto-downloads), multi-turn memory (`AiChatTurn`), Markdown + LaTeX Unicode + Table rendering (`AiResponseFormatter`) & `Table → MD/CSV`, `Ctrl+V` screenshot paste, up to 8 editable custom prompt chips, offline `whisper.cpp` voice dictation (`WhisperVoiceService`), and live weather (`wttr.in`);
- **Smart Widgets & Wireless Mouse Battery:** PC telemetry, Smart Clipboard (text + up to 8 RAM screenshots with pixel-hash dedup + Vision `Ask AI`), File Drop Shelf image quick actions (`Ask AI`, `PNG`, `JPG 82%`), and wireless mouse battery monitoring (Bluetooth LE/Classic, HID `0x84`/`0x85`, Beken/Attack Shark `0x1D57` + wired `PID_FA61`, Logitech HID++ 2.0, Razer HyperSpeed, Compx/VXE/ATK/VGN/Darmoshark/Zaopin, Lamzu/Pulsar/Nordic 4K/8K).

---

## 2. Non-Negotiable Guardrails

1. **Strict Portable Isolation (Zero-Install):**
   - All settings, models, caches, temporary files, and logs must remain strictly inside `PortablePaths.RootDirectory` (`<project-root>` during development or `AppContext.BaseDirectory` when `dist\` is moved standalone):
     - `config\` — `settings.json`, DPAPI-encrypted DeepL key, translation history, clipboard text history
     - `models\` — offline ONNX translation packs, `_runtime\onnxruntime.dll`, `_ocr\` (PP-OCRv5), `_llama\llama-server.exe`, `_whisper\` (`whisper-cli.exe` + `ggml-base.bin`), and GGUF LLM/VLM folders
     - `cache\` — resumable `.part` downloads (`downloads\`), `weather_cache.json`, `cache\temp\`
     - `logs\` — daily `LogService` logs
     - `.nuget\packages\` — local NuGet cache (pinned in `Directory.Build.props` via `$(MSBuildThisFileDirectory)`)
   - Always use `PortablePaths` in code. Never write to `SpecialFolder.ApplicationData`, `LocalApplicationData`, `Documents`, or system `Path.GetTempPath()`.
   - Never hardcode developer-specific absolute paths or machine-specific hardware names in source code, XAML, tests, docs, or skills.

2. **Zero-NuGet Build by Default (`FullSdk=false`):**
   - `PulseIsland.csproj` and `PulseIsland.Tests.csproj` build by default with `FullSdk=false`, zero external NuGet dependencies, and `--no-restore`.
   - Implicit global `Using` directives for `System.Drawing` and `System.Windows.Forms` are removed. Prefer WPF types (`System.Windows.Media.Color`, `Brush`, `Point`) and explicitly qualify overlapping types (`System.Windows.MessageBox`, `System.Windows.Controls.CheckBox`, `System.Diagnostics.Process`, `System.Windows.Shapes.Path`).

3. **Privacy, Local-First & Zero Auto-Downloads:**
   - Offline translation (ONNX MarianMT / Local LLM), neural OCR (PP-OCRv5), local AI (`llama-server.exe` on `127.0.0.1`), and offline dictation (`whisper.cpp`) run entirely on-device.
   - **Never auto-download** large model weights or runtimes in the background; all downloads must be explicitly initiated by the user in Settings.
   - Weather (`WeatherService`) only queries public forecast data from `wttr.in` and caches it in `cache\weather_cache.json`.

4. **UI & Localization Standards:**
   - Full live bilingual localization (**English default**, instant switch to Russian) via `LocalizationService` (`{DynamicResource Loc_*}`, `LocalizationService.Get`, `LocalizationService.Format`, `LocalizationService.Pick`). Keep EN and RU dictionaries at 100% key parity (verified by unit tests).
   - Use crisp vector `<Path>` icons (`Window.Resources` `Geometry`) instead of raw emoji in chrome/controls.
   - Use responsive `Grid` / `WrapPanel` layouts for headers and toolbars so buttons never clip or overlap in either English or longer Russian strings.
   - Apply custom dark `ScrollBar` styles in dark glass themes (no default white Windows scrollbars).
   - Keep the island focused — do **not** add Timer / Pomodoro / Stopwatch clutter.

---

## 3. Standard Workflow

Follow these steps from `<project-root>` on every task:

1. **Read the architecture map before editing:**
   - Read `.agents\skills\pulseisland-codebase-map\SKILL.md` and any relevant subsystem skill (Section 5).
2. **Make clean, targeted edits:**
   - Preserve existing public APIs, localize all new UI text in both EN and RU, and add unit tests for new logic.
3. **Before building/publishing — stop running instances and redirect `TEMP`/`TMP`:**
   ```powershell
   Get-Process -Name "PulseIsland","llama-server" -ErrorAction SilentlyContinue | Stop-Process -Force
   New-Item -ItemType Directory -Force -Path ".\cache\temp" | Out-Null
   $env:TEMP = (Resolve-Path ".\cache\temp").Path
   $env:TMP  = $env:TEMP
   ```
   *(If `PulseIsland.exe` or `llama-server.exe` is running, publishing to `dist\` will fail due to file locks.)*
4. **Build, run tests, and publish to `dist\`:**
   ```powershell
   # On a fresh clone, run the first build without --no-restore to generate obj\project.assets.json (Zero-NuGet)
   dotnet build PulseIsland.sln -c Debug
   dotnet run --project tests\PulseIsland.Tests\PulseIsland.Tests.csproj --no-restore -c Release
   dotnet publish src\PulseIsland\PulseIsland.csproj --no-restore -c Release -o dist
   ```
   - In `FullSdk=false` mode, `PulseIsland.Tests` runs via `TestFrameworkShim.cs` and discovers all `[Fact]` tests via reflection. All **36 tests** (or more as you add coverage) must pass with `0 failed`.
5. **Keep docs & skills in sync (`auto-skill-creator`):**
   - Whenever you add/rename services, windows, `AppSettings` properties, or tests, or uncover new Windows/WPF/HID/COM pitfalls, update `.agents\skills\pulseisland-codebase-map\SKILL.md`, `pulseisland-settings-and-testing\SKILL.md`, and the relevant subsystem skill.

---

## 4. Known Windows / WPF / HID / COM Pitfalls (Must Remember!)

- **Heap-Allocated Win32 `OVERLAPPED` + `CancelIoEx` (`MouseBatteryService`):**
  - **Never** pass a stack-allocated `ref NativeOverlapped` (or `FileStream.ReadAsync` + `Dispose()`) to asynchronous Win32 `ReadFile` / `WriteFile` on HID handles (`hidusb.sys`). If the I/O times out or cancels slightly after stack unwind, the kernel writes completion status into reclaimed stack memory (instant fatal stack corruption), and `FileStream.Dispose()` deadlocks on silent HID endpoints.
  - Always allocate both the `OVERLAPPED` struct and I/O buffer on the unmanaged heap via `Marshal.AllocHGlobal`, wait with `WaitForSingleObject`, call `CancelIoEx` + `GetOverlappedResult(..., bWait: true)` before freeing in `finally`.
- **Wireless Mouse Protocol Quirks (`MouseBatteryService`):**
  - **Beken (`VID_1D57`):** In 2.4 GHz mode (`PID_FA60`), 5-byte packets `[0x03, devId, 0x40/0x41, status, value]` arrive on `mi_02&col03` (`UsagePage 0x000A`); models with `0x4010` (e.g., Attack Shark X3) encode `1..10` (`10%..100%`), while others (`X6`/`X11`/`R1`) encode `0..100%`. Never open a competing handle to `col03`. Wired charging exposes `PID_FA61` (detect via zero-access `CreateFileW(..., 0, ...)` and `WM_DEVICECHANGE 0x0219`; never call synchronous `HidD_GetFeature` on `FA61`/`col04`).
  - **Logitech HID++ 2.0 (`0x046D`):** Guard against `0x8F` / `0xFF` HID++ error frames before interpreting battery bytes.
  - **Compx / VXE / ATK / VGN / Darmoshark / Zaopin (`0x3554`, `0x25A7`, `0x373B`) & Lamzu / Pulsar / Nordic 4K/8K (`0x3710`, `0x3367`, `0x373E`):** Restrict queries to vendor collections (`UsagePage >= 0xFF00`) using heap-allocated `OVERLAPPED` + `CancelIoEx`.
  - **Bluetooth (`DEVPKEY_Device_BatteryPlusCharging`):** Group PnP nodes by `DEVPKEY_Device_ContainerId` and accept only containers belonging to a `Mouse` class/name so Bluetooth headsets/gamepads never hijack `Auto` mode.
- **`WS_EX_NOACTIVATE` Focus Toggling Inside the Island (`IslandWindow`):**
  - The Dynamic Island normally carries `WS_EX_NOACTIVATE` so clicking media controls or notifications never steals focus from the user's active game/editor. However, WPF `TextBox` inputs (like `TranslationSearchBox`) cannot receive keyboard focus while `WS_EX_NOACTIVATE` is set. Temporarily clear `WS_EX_NOACTIVATE` (`EnableInteractiveKeyboardFocus`) when the user clicks the search box, and restore it on `LostFocus` / collapse.
- **Async Non-Blocking `SettingsWindow` Initialization (`OnWindowLoaded`):**
  - Never run COM audio device enumeration, WMI/GPU hardware detection, or recursive `models\` disk scanning synchronously in the `SettingsWindow` constructor — it freezes the WPF UI thread and makes opening Settings sluggish. Defer heavy probes to `Loaded` (`OnWindowLoaded`) on background tasks and marshal UI updates back via `Dispatcher`.
- **Responsive `Grid` / `WrapPanel` Headers & Toolbars (EN ⇄ RU):**
  - Russian UI labels are often 25–45% wider than English equivalents. Never place header titles, badges, and action buttons in fixed-width single-row `StackPanel`s where text collides with buttons. Use `Grid` with `*` + `TextTrimming="CharacterEllipsis"` or `WrapPanel` for toolbars/chips so both English and Russian layouts stay crisp.
- **Mixed `Visual` / `ContentElement` Hit-Testing (`DwmBackdropService.GetParentSafe`):**
  - Calling `VisualTreeHelper.GetParent(dep)` throws `InvalidOperationException` when `OriginalSource` is a `ContentElement` (`Run`, `Span`, `Paragraph`, `TableCell` inside a `FlowDocument` / `RichTextBox`, such as `AiAssistantPopupWindow`). Always walk parent chains via `DwmBackdropService.GetParentSafe(dep)`.
- **Core Audio COM Threading & Deadlock Prevention (`AudioEndpointMonitorService`):**
  - Initialize `IMMDeviceEnumerator` on an MTA thread pool thread and unregister `IAudioEndpointVolumeCallback` **outside** internal locks to avoid AB-BA deadlocks with `AudioSes.dll` RPC notifications.
- **WPF `Border.ClipToBounds` & Rounded Images:**
  - `ClipToBounds="True"` on a `Border` does not clip a child `<Image>` to `CornerRadius`; use `Image.Clip` with a `RectangleGeometry`.
- **.NET Custom Numeric Format Strings (`0.#` vs `0.1`):**
  - In `ToString("0.1")`, `1` is a literal character! Always use `0.#` / `0.##`.
- **Windows PowerShell 5.1 / WinRT Bridge:**
  - Built-in PowerShell 5.1 does not support C# 6+ syntax (`$"..."`) in `Add-Type`, and `AsTask<T>` on WinRT interfaces can return `System.__ComObject` (invoke `WindowsRuntimeStreamExtensions.AsStreamForRead` via reflection). Always redirect `$env:TEMP` / `$env:TMP` to `.\cache\temp` for ad-hoc PowerShell `Add-Type` scripts.
- **Media Controls Without System OSD:**
  - Never send `keybd_event(VK_MEDIA_*)`; control playback directly via SMTC so the native Windows media OSD never pops over the island.
- **Auto-Collapse When Opening Settings:**
  - Opening `SettingsWindow` from `App.xaml.cs` collapses an `Expanded` island back to `Compact` so it never overlaps the settings window.

---

## 5. Skills Map (`.agents\skills\`)

- `pulseisland-codebase-map` — full v2.0 module map of `src\PulseIsland\`.
- `pulseisland-settings-and-testing` — `AppSettings`, language/model catalogs, `SettingsWindow`, and the 36-test dual-mode suite.
- `strict-portable-isolation` & `file-system-audit` — ensuring zero writes outside `<portable-root>`.
- `dotnet-cli-msbuild`, `nuget-package-management`, `automated-unit-integration-testing` — build, publish, and testing workflows.
- `pinvoke-win32-interop`, `wpf-animation-custom-chrome`, `winrt-projection` — Win32 P/Invoke, DWM glass blur, animations, and WinRT.
- `naudio-wasapi-loopback`, `fft-signal-processing` — WASAPI audio capture and FFT spectrum processing.
- `onnx-runtime-tokenizers`, `huggingface-hub-download` — offline ONNX translation/OCR and resumable model downloads.
- `portable-distribution-and-installer`, `ui-inspection-screenshot`, `auto-skill-creator` — portable packaging, visual verification, and skill maintenance.
