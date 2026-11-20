---
name: auto-skill-creator
description: >-
  Meta-skill for PulseIsland (<project-root>) that instructs the agent to automatically
  create new project skills or update existing skills in .agents\skills\ whenever
  code changes alter architecture, add new subsystems, or uncover non-obvious platform
  pitfalls.
---

# Auto Skill Creator & Maintainer (`<project-root>\.agents\skills\`)

## When to Automatically Update or Create a Skill
Trigger this workflow automatically (or proactively propose/apply it at the end of a feature/fix) whenever **any** of the following happens in `<project-root>`:

1. **New Subsystem or Distribution Flow:**
   - A new service, interop layer, UI window, or build/release tool is added (for example, a new `tools\` script or `Services\` module) that does not fit cleanly into an existing skill → **create** `.agents\skills\<kebab-case-name>\SKILL.md`.
2. **Architectural or Dependency Shift:**
   - A major mechanism changes (e.g., replacing NuGet packages with native COM/C-API, changing WinRT bridge behavior, changing fallback priority in `TranslationCoordinator`, or updating `Release\` packaging) → **update** the corresponding skill (`pulseisland-codebase-map`, `onnx-runtime-tokenizers`, `naudio-wasapi-loopback`, `winrt-projection`, `portable-distribution-and-installer`, etc.).
3. **Non-Obvious Platform Pitfall Discovered & Fixed:**
   - Whenever debugging reveals a subtle Windows/WPF/CLR/.NET trap that future edits could easily reintroduce, record the rule in the relevant skill:
     - *Examples already learned:* WPF `Border.ClipToBounds` does not clip child `<Image>` corners (must use `Image.Clip` `RectangleGeometry`); PowerShell 5.1 WinRT `AsTask<Interface>` returns `System.__ComObject` (use reflection `WindowsRuntimeStreamExtensions.AsStreamForRead` via `IInputStream`); `keybd_event(VK_MEDIA_*)` triggers the Windows Media OSD popup (call SMTC `TryTogglePlayPauseAsync` / `TrySkipNextAsync` / `TrySkipPreviousAsync` directly); `.NET` `"0.1"`/`"0.2"` appends literal `'1'`/`'2'` instead of decimal places (use `"0.#"`/`"0.##"`).
4. **Settings / DTO / Test Count Drift:**
   - When new `AppSettings` properties, `SettingsWindow` controls/dependencies, or test suites in `tests\PulseIsland.Tests` change behavior, update `pulseisland-settings-and-testing` and `pulseisland-codebase-map`.

## Skill Authoring Rules
- **Location:** `<project-root>\.agents\skills\<skill-name>\SKILL.md` (lowercase kebab-case directory name matching `name:` in YAML frontmatter).
- **Frontmatter:**
  ```yaml
  ---
  name: <kebab-case-name>
  description: >-
    Clear "Use this skill when..." trigger describing exact files, APIs, and scenarios.
  ---
  ```
- **Accuracy & Conciseness:**
  - Document **only** file paths, symbol names, build commands, and behaviors directly verified in the current workspace.
  - Keep each `SKILL.md` focused and scannable (under ~80 lines): architecture rules, exact entry points, and concrete gotchas — avoid dumping whole source files.
  - Prefer updating an existing overlapping skill over creating redundant skills.
  - Never hardcode developer-only runtime paths inside app code examples; use `PortablePaths` for runtime code and repo-relative paths for build/test commands.

## Quick Maintenance Checklist
1. List `<project-root>\.agents\skills\` to check whether an existing skill already covers the domain.
2. Read the target `SKILL.md` before editing so existing accurate guidance is preserved.
3. Update `pulseisland-codebase-map\SKILL.md` whenever modules under `src\PulseIsland\` are added, removed, or renamed.
4. Confirm every referenced path and command still works (`dotnet build`, `dotnet run --project tests\PulseIsland.Tests\PulseIsland.Tests.csproj --no-restore -c Release`).
