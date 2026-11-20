---
name: pulseisland-settings-and-testing
description: >-
  Guide for PulseIsland configuration DTOs, language/model catalogs, Windows
  autostart registry integration, tabbed SettingsWindow UI, and the dual-mode
  xUnit/offline test suite in tests\PulseIsland.Tests.
---

# PulseIsland Settings, Catalogs & Testing Skill

## 1. Catalogs & DTOs (`Core/CatalogModels.cs`)
- `LanguageCatalog.SupportedLanguages`: 16 `LanguageOption` entries with `FindOrDefault(code, fallback)` region-subtag normalization (`en-US` → `en`).
- `DiskSpaceSummaryDto.FormatBytes(long bytes)`: **always** use `{mb:0.#}` and `{gb:0.##}` (never `0.1` or `0.2`, because in .NET custom numeric format strings `1` and `2` are literal characters appended to the number!).
- `OfflineModelCatalogEntry`, `OfflineModelStatusDto`, `AudioDeviceOptionDto`: immutable DTOs for UI binding.

## 2. Offline Model Manager & Autostart (`Services/`)
- `OfflineModelCatalogService`:
  - Built-in catalog: `opus-mt-en-ru`, `opus-mt-ru-en`, `opus-mt-de-ru`, `opus-mt-mul-en` (all include `encoder_model.onnx`, `decoder_model.onnx`, `source.spm`, `vocab.json`, `config.json`, `tokenizer_config.json`).
  - Skips `_`-prefixed folders (e.g. `_runtime`) when scanning `models\`.
  - Distinguishes `HasCoreModelWeights` (`encoder_model.onnx` + `decoder_model.onnx`) from `IsModelDirectoryComplete` (all 5 required files including `vocab.json` + `config.json`).
  - `DownloadModelPackageAsync` skips files already present on disk (`Length > 0`), ensures `OrtNativeRuntime`, and reads `config.json` token IDs into `pulseisland_manifest.json`.
- `AutoStartService`:
  - Reads/writes `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (`PulseIsland`) pointing strictly to the running `PulseIsland.exe` path without `.lnk` shortcuts in system user directories (`AppData`, `LocalAppData`, `%Temp%`).

## 3. Tabbed Settings Window (`UI/SettingsWindow.xaml(.cs)`)
- **Three tabs:** General & Interface, Translator & Models, Music & Waves.
- **Footer Layout:** Uses a 2-column `Grid` (`Width="*"` for `FooterStatusText` with `TextTrimming="CharacterEllipsis"`, `Width="Auto"` for Cancel and Save buttons) so status text never overlaps buttons.
- **Live Language Selector (`UiLanguageCombo`):** Located at the top of Tab 1 (`AppSettings.UiLanguage`, defaults to `AppLanguage.English`). Changing the combo applies live preview via `LocalizationService.ApplyLanguage(...)` and refreshes dynamic labels; Cancel reverts to the previously saved language, and Save persists it to `settings.json`.
- **Auto-collapse Island:** Opening Settings from `App.xaml.cs` collapses `IslandWindow` from `Expanded` to `Compact`.
- **Model Manager UX:** Includes Open folder (opens `PortablePaths.ModelsDirectory` in Explorer), Refresh list, and a clear `MessageBox` on socket/network errors (`WSAEACCES 10013`) listing manual Hugging Face download steps.
- **Themes, Smart Widgets & Mouse Battery:** 7 themes (`ThemeService`) with live preview bar, toggles for DWM glass blur, progress bar, album glow, smart clipboard, volume/mic indicator, file drop shelf, system indicators, and a collapsible `MouseBatteryDetailsPanel` (`MouseConnectionMode`, compact visibility, low-battery threshold & sound preset test).
- **Root Project Guides:** Always keep `<project-root>\AGENT.md` and `<project-root>\GOAL.md` aligned with architecture and build/test workflows.

## 4. Dual-Mode Test Suite (`tests/PulseIsland.Tests/`)
- **Offline mode (`FullSdk=false`, default):** Console test runner using `TestFrameworkShim.cs`:
  ```powershell
  $env:TEMP = "<project-root>\cache\temp"; $env:TMP = "<project-root>\cache\temp"
  dotnet run --project tests\PulseIsland.Tests\PulseIsland.Tests.csproj --no-restore -c Release
  ```
- **Test Coverage (23 tests):** `LassoGeometryTests`, `SecurityHelperTests`, `PortablePathsAndSettingsTests` (including `SentencePieceAndOrtNativeRuntime_WorkZeroDependency`, `ThemeService_ContainsAllSevenRequestedThemes`, `IslandViewModel_NotificationPushAndDismiss_UpdatesHistoryAndActiveNotification`, `IslandViewModel_FileDropZoneVisible_OnlyWhenDraggingOrHasFiles`, `NotificationFormattingAndSounds_DetectMediaTypesChatTargetsAndGenerateWav`, `WeatherAndLocalLlmCatalog_ParseWeatherAndBuildPromptsCleanly`, `IslandViewModel_MouseBattery_UpdatesCompactVisibilityAndSamplesSafely`, and `LocalizationService_DefaultsToEnglishAndSwitchesToRussianCleanly`), and `FftAndHotkeyTests`.
