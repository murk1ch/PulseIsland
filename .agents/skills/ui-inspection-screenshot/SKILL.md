---
name: ui-inspection-screenshot
description: >-
  Use this skill to launch the compiled PulseIsland application, capture
  screenshots of the screen or top-edge island area, and visually verify
  positioning, DPI scaling, hover expansion, bilingual EN/RU layout, and overlays.
---

# UI Inspection & Screenshot Verification Skill

## Purpose
Visually verify that PulseIsland renders cleanly on any Windows 10/11 monitor configuration:
- Dynamic Island is centered at the top edge of the target monitor.
- Zero clipped text, overlapping badges, or misaligned controls at 100%, 125%, and 150% DPI in both **English** and **Russian** UI languages.
- Clean transitions across Hidden (top strip), Compact, and Expanded states, as well as `SettingsWindow`, `WelcomeOnboardingWindow`, `AiAssistantPopupWindow`, `TranslationPopupWindow`, and `SmartClipboardWindow`.

## Steps
1. Build and launch `PulseIsland` (e.g., from `dist\PulseIsland.exe` or `dotnet run --project src\PulseIsland\PulseIsland.csproj`).
2. Capture a screenshot using the bundled helper script (saves strictly to `<project-root>\cache\screenshots\`):
   ```powershell
   powershell -ExecutionPolicy Bypass -File "<project-root>\.agents\skills\ui-inspection-screenshot\scripts\capture_screen.ps1" -OutName "island_check.png"
   ```
   Pass `-TopRegionOnly` to crop the top 250 px across the primary monitor for high-detail Dynamic Island inspection.
3. Inspect the saved PNG with `read_file` to verify alignment, contrast, glass blur transparency, and text wrapping.
4. Close the test instance after verification:
   ```powershell
   Stop-Process -Name PulseIsland -ErrorAction SilentlyContinue
   ```
