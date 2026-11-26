---
name: portable-distribution-and-installer
description: >-
  Use this skill when preparing portable builds in dist\, maintaining the bilingual
  Windows installer (tools\Installer\PulseIslandInstaller.cs), or packaging
  automated portable and standalone win-x64 releases via GitHub Actions.
---

# Portable Distribution, Installer & CI/CD Release Skill

## 1. Zero Hardcoded Paths
- Never display developer-machine paths or drive letters in XAML, tooltips, code, or Settings headers.
- `PortablePaths.RootDirectory` automatically resolves to the repo root during development (walking up for `PulseIsland.sln`) and falls back to `AppContext.BaseDirectory` when distributed standalone on any Windows PC.

## 2. Application & Tray Icons
- `src\PulseIsland\PulseIsland.csproj` embeds `..\..\assets\PulseIsland.ico` via `<ApplicationIcon>`.
- Keep `assets\PulseIsland.ico` in sync with the system tray `NotifyIcon` and window icons.

## 3. Local Portable Build (`dist\`)
Run from `<project-root>`:
```powershell
New-Item -ItemType Directory -Force -Path ".\cache\temp" | Out-Null
$env:TEMP = (Resolve-Path ".\cache\temp").Path
$env:TMP  = $env:TEMP
Get-Process PulseIsland -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet publish src\PulseIsland\PulseIsland.csproj --no-restore -c Release -o dist
```

## 4. Bilingual Portable Installer (`tools\Installer\PulseIslandInstaller.cs`)
- Lightweight WinForms installer with automatic EN/RU UI detection (`CultureInfo.CurrentUICulture`).
- Allows the user to pick any portable install folder, closes running `PulseIsland` instances, verifies whether `.NET 8 Desktop Runtime` is installed (downloading and installing it automatically if missing), extracts the portable payload, and optionally creates a Desktop shortcut and launches `PulseIsland.exe`.

## 5. Automated GitHub Actions Release (`.github\workflows\release.yml`)
Triggered on `v*` tag push or manual `workflow_dispatch` on `windows-latest`:
1. Builds `PulseIsland.sln` (`Debug`) and runs all 36 tests via `dotnet run --project tests\PulseIsland.Tests\PulseIsland.Tests.csproj --no-restore -c Release`.
2. Publishes and zips two `win-x64` artifacts attached to the GitHub Release:
   - **`PulseIsland-<tag>-win-x64-portable.zip`**: lightweight framework-dependent build (requires .NET 8 Desktop Runtime).
   - **`PulseIsland-<tag>-win-x64-standalone.zip`**: self-contained single-file `win-x64` build (`PublishSingleFile=true`, `IncludeNativeLibrariesForSelfExtract=true`, `EnableCompressionInSingleFile=true` — zero .NET install required).

## 6. End-User Firewall / Offline Model Note
- If Windows Firewall or antivirus blocks outbound HTTPS (`WSAEACCES 10013`) after an update, advise allowing TCP 443 for `PulseIsland.exe` or placing downloaded model files directly under `<portable-root>\models\<model-id>\` and clicking **Refresh**.
