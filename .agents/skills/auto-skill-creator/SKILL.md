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
Trigger this workflow automatically whenever **any** of the following happens in `<project-root>`:

1. **New Subsystem or Distribution Flow:**
   - A new service, interop layer, UI window, or build/release tool is added (e.g., a new `Services\` or `Translation\` module) that does not fit cleanly into an existing skill → **create** `.agents\skills\<kebab-case-name>\SKILL.md`.
2. **Architectural or Dependency Shift:**
   - A major mechanism changes (e.g., replacing NuGet packages with native COM/C-API, adding PP-OCRv5 ONNX or Vision VLM routing in `TranslationCoordinator`, adding COM `IPolicyConfig` audio switching, or updating portable packaging) → **update** the corresponding skill (`pulseisland-codebase-map`, `onnx-runtime-tokenizers`, `naudio-wasapi-loopback`, `winrt-projection`, `portable-distribution-and-installer`, etc.).
3. **Non-Obvious Platform Pitfall Discovered & Fixed:**
   - Whenever debugging reveals a subtle Windows/WPF/CLR/HID/COM trap that future edits could easily reintroduce, record the rule in `AGENT.md` and the relevant skill:
     - *Examples already learned:*
       - Win32 HID `ReadFile`/`WriteFile` with `OVERLAPPED` **must** use unmanaged heap memory (`Marshal.AllocHGlobal`) + `CancelIoEx` (never stack `ref NativeOverlapped` or `FileStream.Dispose()`).
       - `WS_EX_NOACTIVATE` windows block `TextBox` keyboard input; clear `WS_EX_NOACTIVATE` while typing in `TranslationSearchBox` and restore on `LostFocus`.
       - `VisualTreeHelper.GetParent` throws on `FlowDocument` `ContentElement`s (`Run`/`Span`/`TableCell`); use `DwmBackdropService.GetParentSafe`.
       - Keep heavy COM/WMI/disk enumeration out of `SettingsWindow` constructor (defer to async `OnWindowLoaded`), and use responsive `Grid`/`WrapPanel` toolbars so EN/RU labels never overlap.
       - WPF `Border.ClipToBounds` does not clip child `<Image>` corners (use `Image.Clip` `RectangleGeometry`); `.NET` `"0.1"` appends literal `'1'` (use `"0.#"`/`"0.##"`); never use `keybd_event(VK_MEDIA_*)` (triggers Windows media OSD).
4. **Settings / DTO / Test Count Drift:**
   - When new `AppSettings` properties, `SettingsWindow` controls, model catalogs, or tests in `tests\PulseIsland.Tests` change behavior, update `pulseisland-settings-and-testing`, `pulseisland-codebase-map`, `AGENT.md`, and `GOAL.md`.

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
- **Accuracy, Portability & Conciseness:**
  - Document **only** file paths, symbol names, build commands, and behaviors directly verified in `src\PulseIsland\` and `tests\PulseIsland.Tests\`.
  - Keep each `SKILL.md` focused and scannable (~45–90 lines): architecture rules, exact entry points, and concrete gotchas.
  - Prefer updating an existing overlapping skill over creating redundant skills.
  - **Zero machine-specific paths or hardware names:** Always use `<project-root>`, `<portable-root>`, repo-relative paths, and `PortablePaths`.

## Quick Maintenance Checklist
1. List `<project-root>\.agents\skills\` to check whether an existing skill already covers the domain.
2. Read the target `SKILL.md` before editing so existing accurate guidance is preserved.
3. Update `pulseisland-codebase-map\SKILL.md` whenever modules under `src\PulseIsland\` are added, removed, or renamed.
4. Confirm every referenced path and command still works (`dotnet build PulseIsland.sln -c Debug` and `dotnet run --project tests\PulseIsland.Tests\PulseIsland.Tests.csproj --no-restore -c Release`).
