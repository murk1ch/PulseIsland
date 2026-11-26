---
name: winrt-projection
description: >-
  Use this skill when integrating Windows Runtime (WinRT) APIs in .NET 8 WPF
  for PulseIsland, covering both direct FullSdk=true WinRT projections (SMTC,
  Windows.Media.Ocr) and FullSdk=false zero-NuGet fallback bridges.
---

# Windows Runtime (WinRT) Projection & Dual-Mode Bridge Skill

## Project Configuration (`src\PulseIsland\PulseIsland.csproj`)
PulseIsland supports two build modes so the repository compiles cleanly both offline and with full WinRT projections:
- **Full SDK (`-p:FullSdk=true`):** Sets `<TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>` and defines `WITH_WINRT` for direct CsWinRT API calls.
- **Default Offline (`FullSdk=false`):** Targets `net8.0-windows` without `WITH_WINRT`, using built-in zero-NuGet fallback bridges (`#if !WITH_WINRT`).

## 1. System Media Transport Controls (`Media/SmtcMediaService.cs`)
- **Direct WinRT (`#if WITH_WINRT`):**
  - `Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager.RequestAsync()`.
  - Subscribe to `CurrentSessionChanged`, `MediaPropertiesChanged` (title, artist, album thumbnail `IRandomAccessStreamReference`), and `PlaybackInfoChanged`.
  - Invoke `TryTogglePlayPauseAsync()`, `TrySkipNextAsync()`, and `TrySkipPreviousAsync()`.
- **Thread & Resource Rules:** WinRT events fire on background threads. Always marshal WPF UI updates via `Dispatcher.InvokeAsync(...)`, freeze decoded `BitmapImage` thumbnails, and dispose `IRandomAccessStreamWithContentType`.

## 2. Windows OCR (`Translation/WindowsOcrService.cs`)
- **Direct WinRT (`#if WITH_WINRT`):**
  - Uses `Windows.Media.Ocr.OcrEngine` (`TryCreateFromUserProfileLanguages` / `TryCreateFromLanguage`) and `Windows.Graphics.Imaging.SoftwareBitmap` (`Bgra8`, `Premultiplied`).
- **Default Zero-NuGet Bridge (`#if !WITH_WINRT`):**
  - Runs a persistent resident WinRT OCR worker (`WarmUpAsync` / `RecognizeViaResidentBridgeAsync`) so cold startup latency is eliminated while keeping zero NuGet dependencies.
- **Preprocessing & Fallback:** Both modes feed multi-pass preprocessed PNG variants from `OcrImagePreprocessor.BuildVariants`, apply script/plausibility-aware consensus voting, and pair with `PaddleOcrOnnxService` (PP-OCRv5 Mobile ONNX) when installed for cursive, handwriting, math, and multi-script text.
