---
name: winrt-projection
description: >-
  Use this skill when integrating Windows Runtime (WinRT) APIs in .NET 8 WPF,
  specifically System Media Transport Controls (SMTC) for music playback/metadata
  and Windows.Media.Ocr for local text recognition.
---

# Windows Runtime (WinRT) Projection Skill

## Project Configuration
To access WinRT APIs cleanly in .NET 8 WPF, set the TargetFramework in `.csproj`:
```xml
<TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
```

## 1. System Media Transport Controls (SMTC)
- Namespace: `Windows.Media.Control`
- Entry point: `await GlobalSystemMediaTransportControlsSessionManager.RequestAsync()`
- Subscribe to:
  - `CurrentSessionChanged` (when user switches active player/browser tab)
  - `MediaPropertiesChanged` (track title, artist, album thumbnail `IRandomAccessStreamReference`)
  - `PlaybackInfoChanged` (Playing / Paused / Stopped)
- Controls: `await session.TryTogglePlayPauseAsync()`, `TrySkipNextAsync()`, `TrySkipPreviousAsync()`.
- **Important:** WinRT events fire on background threads. Always marshal UI updates via `Dispatcher.InvokeAsync(...)` and dispose `IRandomAccessStreamWithContentType` after reading the thumbnail into a frozen `BitmapImage`.

## 2. Windows OCR (`Windows.Media.Ocr`)
- Namespace: `Windows.Media.Ocr`, `Windows.Graphics.Imaging`
- Create engine: `OcrEngine.TryCreateFromUserProfileLanguages()` or `OcrEngine.TryCreateFromLanguage(new Language(langTag))`.
- Convert captured `System.Drawing.Bitmap` / `byte[]` BGRA8 into `SoftwareBitmap` (`BitmapPixelFormat.Bgra8`, `BitmapAlphaMode.Premultiplied`).
- Call `await engine.RecognizeAsync(softwareBitmap)` and join `result.Lines` with spaces/newlines.
- Handle the case where a language pack is not installed on Windows (fallback to multi-language attempt or inform the user).
