---
name: pulseisland-settings-and-testing
description: >-
  Guide for PulseIsland v2.0.0 configuration DTOs, language/OCR/AI/Whisper model catalogs,
  Windows autostart registry integration, async tabbed SettingsWindow UI, onboarding wizard,
  and the 36-test dual-mode xUnit/offline test suite in tests\PulseIsland.Tests.
---

# PulseIsland Settings, Catalogs & Testing Skill (v2.0.0)

## 1. Settings & Catalogs (`Core/AppSettings.cs`, `Core/CatalogModels.cs`)
- `AppSettings` key v2.0 properties:
  - `UiLanguage` (`AppLanguage.English` default, live EN ⇄ RU), `HasCompletedOnboarding`, `ThemeStyle` (7 themes), `EnableDwmBackdropBlur`.
  - `TranslationMode` (`OnlineWindowsOcr`, `OfflineOnnx`, `OfflineLocalLlm`), `OcrMode` (`AutoSmart`, `VisionLlmAlways`, `WindowsOcrOnly`), `OnlineProvider` (`GoogleFree`, `DeepL`), `EncryptedDeepLApiKey`.
  - `SelectedAiModelId` (`qwen2.5-3b-instruct` default), `AiBackend` (`Auto`, `GpuVulkan`, `CpuOnly`), `CustomAiPrompts` (up to 8 `CustomAiPromptChip` items, with `GetDefaultCustomAiPrompts(lang)` and `IsBuiltInDefaultPromptSet(...)` for automatic EN/RU switching of untouched defaults).
  - Mouse battery (`EnableMouseBatteryIndicator`, `MouseConnectionMode`, `ShowMouseBatteryInCompact`, low-battery threshold & chime).
- `LanguageCatalog.SupportedLanguages`: 16 `LanguageOption` entries with `FindOrDefault(code, fallback)` region-subtag normalization (`en-US` → `en`).
- `DiskSpaceSummaryDto.FormatBytes(long bytes)`: **always** use `{mb:0.#}` and `{gb:0.##}` (never `0.1` or `0.2`, which append literal digits in .NET custom format strings).

## 2. Model Managers & Autostart (`Services/`, `Translation/`)
- **Zero Auto-Downloads Rule:** None of the model managers download files automatically on startup; all downloads require explicit user action in Settings and stage `.part` files in `PortablePaths.DownloadsCacheDirectory`.
- `OfflineModelCatalogService`: MarianMT packs (`opus-mt-en-ru`, `opus-mt-ru-en`, `opus-mt-de-ru`, `opus-mt-mul-en`), skips `_`-prefixed folders (`_runtime`, `_ocr`, `_llama`, `_whisper`), checks `HasCoreModelWeights` vs `IsModelDirectoryComplete`.
- `PaddleOcrOnnxService`: PP-OCRv5 Mobile ONNX models in `models\_ocr\` (`ppocrv5_det.onnx`, `ppocrv5_eslav_rec.onnx`, `ppocrv5_en_rec.onnx`, YAML dicts) using `models\_runtime\onnxruntime.dll`.
- `LocalLlmCatalogService`: Multi-model GGUF catalog (Qwen 2.5 3B/7B, Qwen 3 4B, Gemma 2 9B, Vision VLMs Qwen 2.5 VL 3B & 7B + `--mmproj`) + portable `llama-server.exe` in `models\_llama\`.
- `WhisperVoiceService`: Portable `whisper.cpp` (`whisper-cli.exe` + `ggml-base.bin`) in `models\_whisper\`.
- `AutoStartService`: Manages `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (`PulseIsland`) pointing to the active `PulseIsland.exe` with zero system `%AppData%` shortcuts.

## 3. Tabbed Settings Window & Onboarding (`UI/SettingsWindow.xaml(.cs)`, `UI/WelcomeOnboardingWindow.xaml(.cs)`)
- **Non-Blocking Startup (`OnWindowLoaded`):** Keep heavy COM audio enumeration, GPU/Vulkan detection, and recursive `models\` disk scanning off the constructor/UI thread; run asynchronously in `Loaded` (`OnWindowLoaded`) and marshal UI updates via `Dispatcher`.
- **Three Tabs + Guide Button:** General & Interface, Translator & AI (OCR, ONNX, Local AI/Vision, Whisper, up to 8 editable custom prompt chips with in-box placeholders), and Music & Waves. Clicking **Guide** (`OnOpenOnboardingGuideClick`) reopens the 3-step `WelcomeOnboardingWindow`.
- **Responsive EN/RU Layout:** Use 2-column `Grid` (`*` + `TextTrimming="CharacterEllipsis"` / `Auto`) and `WrapPanel` for headers, footers, and button strips so neither English nor longer Russian strings ever overlap.
- **Live Language & Theme Preview:** Changing `UiLanguageCombo` or themes previews immediately; Cancel reverts cleanly, Save persists to `config\settings.json`. Opening Settings from `App.xaml.cs` auto-collapses an `Expanded` island to `Compact`.

## 4. Dual-Mode Automated Test Suite (`tests/PulseIsland.Tests/`)
- **Offline default (`FullSdk=false`):** Runs via `TestFrameworkShim.cs` (discovers all `[Fact]` methods by reflection):
  ```powershell
  New-Item -ItemType Directory -Force -Path ".\cache\temp" | Out-Null
  $env:TEMP = (Resolve-Path ".\cache\temp").Path; $env:TMP = $env:TEMP
  dotnet run --project tests\PulseIsland.Tests\PulseIsland.Tests.csproj --no-restore -c Release
  ```
- **36 Portable Tests (`PulseIslandTests.cs`):** Zero hardcoded drives or developer folders; passes identically locally and on fresh CI clones. Covers:
  - Lasso geometry (`ComputeBoundingBox` + shrinkable `ComputeRectangleBox`), DPAPI encryption, `PortablePaths` isolation, `SettingsService` round-trip, `LanguageCatalog`, `HotkeyService`, `FftSpectrumProcessor`, and WASAPI capture lifecycle.
  - `LocalizationService` EN/RU 100% key parity & live switching, `AppSettings` `UiLanguage` & built-in bilingual prompt chip detection, `ThemeService` (7 themes), and `IslandViewModel` (Settings button visibility, notifications, drop shelf, Live Activities mic/output, live translation search, mouse battery).
  - `WindowsOcrService` (English, diverse fonts/themes, Cyrillic vs Latin scoring, `°C` & zero-alpha clipboard bitmap normalization) and `PaddleOcrOnnxService` (cursive/handwriting, degrees, math, all fonts).
  - `OfflineModelCatalogService`, `SentencePieceMarianTokenizer` + `OrtNativeRuntime`, `LocalLlmCatalogService` multi-model & Vision VLM/`--mmproj` + in-memory JPEG encoding (zero auto-download), `AiResponseFormatter` (LaTeX/Markdown/Tables), `WhisperVoiceService`, multi-turn AI chat, `SmartClipboardService` image history dedup + Drop Shelf quick actions, and `MouseBatteryService` Compx/ATK/VXE/Lamzu/Pulsar protocol packet builders/parsers.
