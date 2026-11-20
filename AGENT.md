# AGENT.md — Agent Guide for PulseIsland

This file is the primary entry point for any AI agent starting work in the PulseIsland repository (`<project-root>`). Read it completely before making any edits.

---

## 1. Project Overview

**PulseIsland** is a portable Windows desktop application (`.NET 8 WPF`) implementing a top-edge Dynamic Island overlay with the following subsystems:
- Live 60 FPS music visualizer (native COM WASAPI loopback + active-music session filter + Radix-2 FFT) and media playback controls via Windows SMTC;
- In-island system notifications (WNF + `UserNotificationListener` + Windows toast suppression + corner popup interception + in-memory WAV chimes);
- Screen translator: hotkey-triggered lasso selection + Windows OCR + online (Google/MyMemory/DeepL) / offline ONNX MarianMT + draggable floating result card;
- Local AI assistant (`Qwen 2.5 3B` GGUF via portable `llama-server.exe` with Vulkan GPU → CPU auto-selection): summaries, translation, weather (`wttr.in`), dedicated hotkey, and floating popup widget;
- Smart widgets: PC telemetry (`CPU`, `RAM`, network speed, clock, PC battery, `Uptime`), Smart Clipboard, drag-and-drop File Shelf, and wireless mouse battery monitoring (Bluetooth, 2.4 GHz, and USB wired charging).

---

## 2. Non-Negotiable Guardrails

1. **Strict Portable Isolation (Zero-Install):**
   - All settings, models, caches, temporary files, and logs must remain strictly inside the application's portable root (`PortablePaths.RootDirectory`, i.e., `<project-root>` during development or `AppContext.BaseDirectory` when `dist\` is moved standalone):
     - `config\` — `settings.json`, DPAPI-encrypted DeepL key, history files
     - `models\` — offline ONNX translation models, `_runtime\onnxruntime.dll`, `_llama\llama-server.exe`, `qwen2.5-3b-instruct\`
     - `cache\` — partial downloads (`downloads\`), `weather_cache.json`, `cache\temp\`
     - `logs\` — daily `LogService` logs
     - `.nuget\packages\` — local NuGet cache (pinned in `Directory.Build.props` via `$(MSBuildThisFileDirectory)`)
   - Always use `PortablePaths` in code. Never write to `SpecialFolder.ApplicationData`, `LocalApplicationData`, `Path.GetTempPath()`, etc.
   - Never hardcode developer-specific absolute paths in source code, XAML, tests, or skills.

2. **Zero-NuGet Build by Default (`FullSdk=false`):**
   - `PulseIsland.csproj` and `PulseIsland.Tests.csproj` build by default with `FullSdk=false`, zero external NuGet dependencies, and `--no-restore`.
   - Implicit global `Using` directives for `System.Drawing` and `System.Windows.Forms` are removed. Prefer WPF types (`System.Windows.Media.Color`, `Brush`, `Point`) and explicitly qualify overlapping types (`System.Windows.MessageBox`, `System.Windows.Controls.CheckBox`, `System.Diagnostics.Process`, `System.Windows.Shapes.Path`).

3. **Privacy & Local-First Design:**
   - Offline translation (ONNX MarianMT) and local AI (`llama-server.exe` bound to `127.0.0.1`) run entirely on-device without sending user text to external servers.
   - Weather (`WeatherService`) only queries public forecast data from `wttr.in` and caches it in `cache\weather_cache.json`.

4. **UI Standards:**
   - Full bilingual localization (**English default**, live switch to Russian in Settings → `UiLanguage`) via `LocalizationService` and `{DynamicResource Loc_*}` / `LocalizationService.Get` / `LocalizationService.Pick`. Never hardcode user-facing text directly in XAML or C# without localization.
   - Use crisp vector `<Path>` icons (`Window.Resources` `Geometry`) instead of emoji.
   - In dark glass themes, always apply custom dark `ScrollBar` styles (no default white Windows scrollbars).
   - While AI generation or an active notification is in progress, the AI/notification indicator takes priority in the compact island so text elements never overlap.

---

## 3. Standard Workflow

Follow these steps automatically on every task (from `<project-root>`):

1. **Read the architecture map before editing:**
   - Read `.agents\skills\pulseisland-codebase-map\SKILL.md` and the relevant subsystem skill (see Section 5).
2. **Make clean, targeted edits:**
   - Avoid duplicating logic, preserve docstrings/comments, and handle edge cases cleanly.
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
   # On a fresh clone, run the first build without --no-restore to generate obj\project.assets.json (~80 ms, Zero-NuGet)
   dotnet build PulseIsland.sln -c Debug
   dotnet run --project tests\PulseIsland.Tests\PulseIsland.Tests.csproj --no-restore -c Release
   dotnet publish src\PulseIsland\PulseIsland.csproj --no-restore -c Release -o dist
   ```
   - In `FullSdk=false` mode, `PulseIsland.Tests` runs via the built-in `TestFrameworkShim.cs` console runner, automatically discovering all `[Fact]` tests via reflection. All 23+ tests must pass (`0 failed`).
   - Keep the portable output updated in `.\dist\PulseIsland.exe`.
5. **Keep documentation and skills in sync (`auto-skill-creator`):**
   - Whenever you add new services/windows, `AppSettings` properties, tests, or uncover non-obvious Windows/WPF/HID pitfalls, update `.agents\skills\pulseisland-codebase-map\SKILL.md`, `pulseisland-settings-and-testing\SKILL.md`, and the relevant subsystem `SKILL.md`.

---

## 4. Known Windows / WPF / HID Pitfalls (Must Remember!)

- **Win32 HID `OVERLAPPED` + `CancelIoEx` (`MouseBatteryService`):**
  - **Never** use `FileStream.ReadAsync` + `Dispose()` with timeouts on silent HID interfaces (`hidusb.sys`): `FileStream.Dispose()` deadlocks the thread waiting for the kernel IRP to complete if the device sends nothing. Always use raw Win32 `ReadFile` / `WriteFile` + `OVERLAPPED` + `WaitForSingleObject` + `CancelIoEx` (`ReadHidOverlappedWithCancel`).
  - **Beken (`VID_1D57`) 2.4 GHz mice:**
    - In 2.4 GHz mode (`PID_FA60`), 5-byte packets `[0x03, devId, 0x40/0x41, status, value]` arrive via `Interrupt-IN` on `mi_02&col03` (`UsagePage 0x000A`) roughly every 2 seconds. On models emitting event `0x4010` (`data[1] == 0x10`, e.g., Attack Shark X3), `data[4]` encodes a `1..10` step (`10%..100%`), whereas other models (`X6`/`X11`/`R1`) encode direct `0..100%`. Never open a second competing handle to `col03` while the background listener is active.
    - Plugging in the USB cable exposes `PID_FA61` (`USB Gaming Mouse`) while `FA60` goes silent; never poll `FA61` or `col04` with synchronous `HidD_GetFeature` (blocks IRP). Detect `FA61` via fast zero-access `CreateFileW(..., 0, ...)` and listen for `WM_DEVICECHANGE (0x0219)` in `IslandWindow.WndProc`.
  - **Bluetooth battery (`DEVPKEY_Device_BatteryPlusCharging`):**
    - On Windows, the battery property often lives on a sibling `BTHLEDevice\{0000180f-...}` node without "Mouse" in its name. Group PnP nodes by `DEVPKEY_Device_ContainerId` and accept only containers belonging to a `Mouse` class/name (otherwise Bluetooth headphones/gamepads override a 2.4 GHz mouse in `Auto` mode).
- **WPF `Border.ClipToBounds` & Rounded Images:**
  - `ClipToBounds="True"` on a `Border` does not clip a child `<Image>` to `CornerRadius`. Use `Image.Clip` with a `RectangleGeometry`.
- **.NET Custom Numeric Format Strings (`0.#` vs `0.1`):**
  - In `ToString("0.1")`, the `1` is a literal character, not a precision specifier! Always use `0.#` / `0.##`.
- **Windows PowerShell 5.1 / WinRT Bridge:**
  - Built-in PowerShell 5.1 does not support C# 6+ syntax (`$"..."`) inside `Add-Type`, and `AsTask<T>` on WinRT interfaces can return `System.__ComObject` (invoke `WindowsRuntimeStreamExtensions.AsStreamForRead` via reflection).
  - For any ad-hoc diagnostic PowerShell script using `Add-Type`, redirect `$env:TEMP` and `$env:TMP` to `.\cache\temp` so `csc.exe` never writes temporary `.cs`/`.dll` files to system `%TEMP%`.
- **Media Controls Without System OSD:**
  - Never send `keybd_event(VK_MEDIA_*)`, as it triggers the native Windows volume/track OSD overlay on top of the island. Control playback directly via SMTC.
- **Auto-Collapse When Opening Settings:**
  - Opening `SettingsWindow` from `App.xaml.cs` collapses an `Expanded` island back to `Compact` so it never overlaps the settings window.

---

## 5. Skills Map (`.agents\skills\`)

- `pulseisland-codebase-map` — full module map of `src\PulseIsland\`.
- `pulseisland-settings-and-testing` — `AppSettings`, language/model catalogs, `SettingsWindow`, and test suite.
- `strict-portable-isolation` & `file-system-audit` — ensuring zero writes to system directories (`AppData`, `LocalAppData`, system `Temp`).
- `dotnet-cli-msbuild`, `nuget-package-management`, `automated-unit-integration-testing` — build, publish, and testing workflows.
- `pinvoke-win32-interop`, `wpf-animation-custom-chrome`, `winrt-projection` — Win32 P/Invoke, DWM glass blur, animations, and WinRT.
- `naudio-wasapi-loopback`, `fft-signal-processing` — WASAPI audio capture and FFT spectrum processing.
- `onnx-runtime-tokenizers`, `huggingface-hub-download` — offline ONNX translation and resumable model downloads.
- `portable-distribution-and-installer`, `ui-inspection-screenshot`, `auto-skill-creator` — portable packaging, visual verification, and skill maintenance.
