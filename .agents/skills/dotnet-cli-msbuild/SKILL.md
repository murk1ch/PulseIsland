---
name: dotnet-cli-msbuild
description: >-
  Use this skill when creating, configuring, building, publishing, or debugging
  .NET 8+ / WPF solutions and projects using dotnet CLI and MSBuild, especially
  for self-contained portable builds in <project-root>.
---

# Dotnet CLI & MSBuild Skill

## Core Principles
- Target `.NET 8+` (`net8.0-windows10.0.19041.0` or newer) with `<UseWPF>true</UseWPF>` when WinRT APIs (SMTC, Windows OCR) are needed.
- Keep all intermediate (`BaseIntermediateOutputPath`), output (`OutputPath`), and publish artifacts inside `<project-root>` — never rely on user-profile paths in system user directories (`AppData`, `LocalAppData`, `%Temp%`).

## Common Commands
1. **Create solution and WPF app:**
   ```powershell
   dotnet new sln -n PulseIsland
   dotnet new wpf -n PulseIsland -f net8.0-windows
   dotnet sln add PulseIsland/PulseIsland.csproj
   ```
2. **Build & Run (Debug):**
   ```powershell
   dotnet build -c Debug
   dotnet run --project PulseIsland/PulseIsland.csproj
   ```
3. **Portable Self-Contained Publish:**
   ```powershell
   dotnet publish PulseIsland/PulseIsland.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o <project-root>\publish
   ```
   *(Avoid `PublishSingleFile=true` if native DLLs like `onnxruntime.dll` or WinRT interop require side-by-side loading unless `IncludeNativeLibrariesForSelfExtract` is explicitly tested without extracting to `C:\Users\...\AppData\Local\Temp\.net`!)*

## Critical Portable Check
- **IMPORTANT:** `.NET` single-file extraction (`DOTNET_BUNDLE_EXTRACT_BASE_DIR`) defaults to `%TEMP%` in system user directories (`AppData`, `LocalAppData`, `%Temp%`). Either publish **non-single-file** self-contained or set `DOTNET_BUNDLE_EXTRACT_BASE_DIR` to `<project-root>\cache\dotnet`.
- Enable `<EnableWindowsTargeting>true</EnableWindowsTargeting>` if building on non-standard environments.

## Verification
- Run `dotnet build --no-restore` and verify exit code `0` and zero warnings on nullable/WinRT TFMs.
