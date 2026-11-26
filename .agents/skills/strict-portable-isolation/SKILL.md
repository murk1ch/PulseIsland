---
name: strict-portable-isolation
description: >-
  Guardrail skill enforcing that PulseIsland and all build/runtime tools never
  write settings, models, caches, temp files, or logs to %AppData%, %LocalAppData%,
  Documents, or system %TEMP%, keeping everything strictly inside <portable-root>.
---

# Strict Portable Isolation Guardrail

## Non-Negotiable Rules
1. **Single Source of Truth (`Core/PortablePaths.cs`):**
   - Always resolve application paths through `PortablePaths` (which resolves to the repository root in dev or `AppContext.BaseDirectory` in standalone builds on any drive/folder).
   - **Never** write runtime files to `Environment.GetFolderPath(SpecialFolder.ApplicationData)`, `LocalApplicationData`, `MyDocuments`, or `Path.GetTempPath()`.
2. **Portable Directory Layout under `<portable-root>`:**
   - `<portable-root>\config\` — `settings.json`, `translation_history.json`, `clipboard_history.json` (API keys encrypted via DPAPI `ProtectedData`)
   - `<portable-root>\models\` — offline translation models (`<model-id>\`), ONNX runtime (`_runtime\`), llama.cpp server (`_llama\`), PP-OCRv5 (`_ocr\`), whisper.cpp (`_whisper\`)
   - `<portable-root>\cache\` — resumable `.part` downloads (`downloads\`), build/runtime temp (`temp\`), verification screenshots (`screenshots\`)
   - `<portable-root>\logs\` — rolling application logs
3. **Third-Party & Toolchain Isolation:**
   - Keep NuGet caches inside `<project-root>\.nuget\` (`Directory.Build.props` + `nuget.config`).
   - Redirect `TEMP`/`TMP` to `<project-root>\cache\temp` during local builds/tests, and set `DOTNET_BUNDLE_EXTRACT_BASE_DIR` to `<portable-root>\cache\dotnet` when auditing single-file standalone builds.
   - Never use `IsolatedStorage` or store app state in the Windows Registry (the only permitted registry write is the optional user-enabled `HKCU\...\Run` autostart value pointing to `<portable-root>\PulseIsland.exe`).

## Pre-Release Code Review Checklist
- Search `src\PulseIsland\` for `SpecialFolder`, `GetTempPath`, `AppData`, `LocalAppData`, and hardcoded drive letters before every commit/release.
- Confirm `PortablePaths.EnsureDirectoriesExist()` runs at startup and passes the `file-system-audit` script with zero violations in `%AppData%`, `%LocalAppData%`, `Documents`, or system `%TEMP%`.
