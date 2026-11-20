---
name: portable-distribution-and-installer
description: >-
  Use this skill when preparing portable builds in dist\ or standalone distributable
  installers/downloaders in Release\ so PulseIsland runs cleanly on any Windows PC
  with custom icon assets and zero hardcoded developer paths.
---

# Portable Distribution & Installer Skill

## 1. Zero Developer-Specific Paths or Labels
- Never display `<project-root>` or machine-specific paths in XAML, tooltips, or Settings headers.
- `PortablePaths.RootDirectory` automatically resolves to the repo root during local development (checking upward for `PulseIsland.sln`) and falls back to `AppContext.BaseDirectory` when distributed standalone on another user's PC.

## 2. Application & Tray Icon
- `src\PulseIsland\PulseIsland.csproj` embeds `..\..\assets\PulseIsland.ico` via `<ApplicationIcon>`.
- Keep `assets\PulseIsland.ico` in sync with the tray `NotifyIcon` and window icons.

## 3. Local Build vs Public Release Packaging
- **Local Development / Testing (`dist\`):**
  ```powershell
  $env:TEMP = "<project-root>\cache\temp"; $env:TMP = "<project-root>\cache\temp"
  Get-Process PulseIsland -ErrorAction SilentlyContinue | Stop-Process -Force
  dotnet publish src\PulseIsland\PulseIsland.csproj --no-restore -c Release -o dist
  ```
  Wait until the user confirms all local fixes before regenerating `Release\PulseIsland-Setup.exe`.
- **Network / Firewall Notes for End Users:**
  If `PulseIsland.exe` receives `WSAEACCES (10013)` from Windows Firewall / antivirus / VPN after a rebuild, direct the user to allow TCP port 443 for `PulseIsland.exe` or place Hugging Face model files directly in `models\<model-id>\` and click **Refresh list**.
