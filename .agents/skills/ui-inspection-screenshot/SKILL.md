---
name: ui-inspection-screenshot
description: >-
  Use this skill to launch the compiled PulseIsland application, capture
  screenshots of the screen or top-edge island area, and visually verify
  positioning, DPI scaling, hover expansion, and cursor trail rendering.
---

# UI Inspection / Screenshot Verification Skill

## Purpose
Verify visually that the Dynamic Island:
- Is centered at the top edge of the target monitor.
- Does not clip text, album art, or settings button at 100%, 125%, or 150% DPI.
- Transitions cleanly between Hidden (thin strip), Compact, and Expanded states.

## Steps
1. Build and launch the app in the background (or via `Start-Process`).
2. Capture a screenshot using the bundled helper script (saves strictly to `<project-root>\cache\screenshots\`):
   ```powershell
   powershell -ExecutionPolicy Bypass -File "<project-root>\.agents\skills\ui-inspection-screenshot\scripts\capture_screen.ps1" -OutName "island_check.png"
   ```
   Optional parameters: `-TopRegionOnly` to crop the top 250px across the screen for high-detail inspection.
3. Inspect the saved PNG image using `read_file` to visually verify layout, alignment, and contrast.
4. Close the test instance after verification (`Stop-Process -Name PulseIsland -ErrorAction SilentlyContinue`).
