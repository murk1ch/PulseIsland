---
name: automated-unit-integration-testing
description: >-
  Use this skill when writing and running unit and integration tests for
  PulseIsland (dual-mode zero-NuGet runner and xUnit suite covering OCR,
  PP-OCRv5, Vision/JPEG, Whisper, Markdown/LaTeX, HID battery protocols,
  audio/FFT, localization parity, and portable isolation).
---

# Automated Unit & Integration Testing Skill

## Dual-Mode Test Project (`tests\PulseIsland.Tests\PulseIsland.Tests.csproj`)
- **Default offline mode (`FullSdk=false`):** Compiles as a console executable targeting `net8.0-windows` using `TestFrameworkShim.cs` with **zero external NuGet packages**.
- **Full xUnit mode (`-p:FullSdk=true`):** Compiles as a test library targeting `net8.0-windows10.0.19041.0` with `xunit`, `xunit.runner.visualstudio`, and `Microsoft.NET.Test.Sdk`.
- Keep all test outputs, NuGet caches, and temp files strictly inside `<project-root>` (`<portable-root>`).

## Core Test Coverage (36 Tests in `tests\PulseIsland.Tests\PulseIslandTests.cs`)
1. **Lasso & Screen Geometry:** Bounding-box padding, monitor-bound clamping, tiny accidental click rejection, and dynamic rectangle shrinking (`LassoGeometryHelper`).
2. **DPAPI Encryption:** Round-trip `SecurityHelper.ProtectString` / `UnprotectString` (`DataProtectionScope.CurrentUser`), ensuring secrets never appear in plaintext and corrupted inputs fail safely.
3. **Portable Isolation & Settings:** Verify `PortablePaths` resolves every directory strictly inside `<portable-root>` and never inside `%AppData%` / `%LocalAppData%` (with zero drive-letter hardcoding); verify `SettingsService` and `AppSettings` round-trip (`UiLanguage`, built-in prompts).
4. **Audio, WASAPI & FFT:** Verify `FftSpectrumProcessor` produces non-zero bands for a synthetic 1 kHz sine wave and decays cleanly; verify `WasapiAudioCaptureService` starts, reads, and stops cleanly.
5. **Windows OCR & PP-OCRv5 Neural OCR:** Rendered English/Cyrillic text recognition, Cyrillic vs. Latin plausibility scoring, diverse fonts/themes/colors, degree-symbol & zero-alpha bitmap normalization (`WindowsOcrService`), plus `PaddleOcrOnnxService` cursive/handwriting/degrees/math/all-font checks.
6. **Offline ONNX Translation:** `OfflineModelCatalogService` completeness & disk summaries, `SentencePieceMarianTokenizer` + `OrtNativeRuntime` zero-dependency checks, and `LanguageCatalog` subtag normalization.
7. **Local AI, Vision (VLM) & Whisper Voice:** Multi-model `LocalLlmCatalogService` without auto-download, VLM `--mmproj` metadata, `ScreenCaptureService` in-memory JPEG encoding, `AiResponseFormatter` Markdown/LaTeX/table conversion to Unicode and `FlowDocument`, and `WhisperVoiceService` WAV/cleaning & multi-turn chat checks.
8. **Mouse Battery HID Protocols:** `MouseBatteryService` packet building and parsing for Compx/ATK/VXE/Lamzu/Pulsar wireless HID devices.
9. **Island UI State, Clipboard, Drop Shelf & Live Activities:** `ThemeService` (all 7 themes), `IslandViewModel` (`SettingsButton` Expanded-only visibility, notifications & WAV chimes, File Drop Shelf image actions, Live Activities, translation search), and `SmartClipboardService` image history deduplication & pinning.
10. **Bilingual Localization Parity:** `LocalizationService` asserts **100% EN/RU key parity** and verifies live language switching.

## Running Tests
```powershell
# Portable temp setup inside <project-root>
$tempDir = (New-Item -ItemType Directory -Force -Path ".\cache\temp").FullName
$env:TEMP = $tempDir; $env:TMP = $tempDir

# Default zero-NuGet console runner (36 tests)
dotnet run --project tests\PulseIsland.Tests\PulseIsland.Tests.csproj --no-restore -c Release

# Full xUnit + WinRT SDK mode (when NuGet is available)
dotnet test tests\PulseIsland.Tests\PulseIsland.Tests.csproj -p:FullSdk=true --logger "console;verbosity=normal"
```
