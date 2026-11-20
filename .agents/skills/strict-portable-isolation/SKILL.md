---
name: strict-portable-isolation
description: >-
  Guardrail skill to enforce that PulseIsland and all build/runtime tools
  never write settings, models, caches, temp files, or logs to system user directories (AppData, LocalAppData, system Temp),
  keeping everything strictly inside <project-root>.
---

# Strict Portable Isolation Guardrail

## Non-Negotiable Rules
1. **Base Directory Resolution:** Always resolve paths relative to `AppContext.BaseDirectory` (which points to `<project-root>\`), never `Environment.GetFolderPath(SpecialFolder.ApplicationData)`, `LocalApplicationData`, `MyDocuments`, or `Path.GetTempPath()`.
2. **Allowed Directory Layout:**
   - `<project-root>\config\` — `settings.json`, encrypted API keys
   - `<project-root>\models\` — offline OCR/translation models
   - `<project-root>\cache\` — temporary downloads, album art cache, temp files
   - `<project-root>\logs\` — application logs
3. **Third-Party Library Isolation:**
   - Explicitly override temp/cache paths for HTTP clients, ONNX Runtime, NuGet, and .NET bundle extraction (`DOTNET_BUNDLE_EXTRACT_BASE_DIR`).
   - Do not use `IsolatedStorage` or Windows Registry for storing app data (only optional HKCU `Run` entry if the user explicitly enables autostart, pointing to `<project-root>\PulseIsland.exe`).

## Code Review Checklist
- Grep the codebase for `SpecialFolder`, `GetTempPath`, `AppData`, `LocalAppData`, `%AppData%` before every release/commit.
- Verify `Directory.CreateDirectory` is called on startup for `config`, `models`, `cache`, and `logs` under `AppContext.BaseDirectory`.
