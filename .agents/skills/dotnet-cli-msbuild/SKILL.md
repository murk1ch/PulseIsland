---
name: dotnet-cli-msbuild
description: >-
  Use this skill when creating, configuring, building, publishing, or debugging
  .NET 8 / WPF solutions and projects using dotnet CLI and MSBuild, including
  zero-NuGet offline builds and portable or standalone win-x64 releases.
---

# Dotnet CLI & MSBuild Skill

## Project Configuration (`src\PulseIsland\PulseIsland.csproj`)
- **Dual-Mode TFM (`FullSdk`):**
  - Default (`FullSdk=false`): targets `net8.0-windows` with **zero external NuGet packages** (native Win32/COM/OrtApi/WPF).
  - Full SDK (`-p:FullSdk=true`): targets `net8.0-windows10.0.19041.0` and defines `WITH_WINRT;WITH_ONNX`.
- **Portable Restore & Cache Isolation:**
  - `Directory.Build.props` sets `<RestorePackagesPath>$(MSBuildThisFileDirectory).nuget\packages</RestorePackagesPath>`.
  - `nuget.config` pins `globalPackagesFolder` and `repositoryPath` to `.\.nuget\packages`.
- **WPF + WinForms Coexistence:** `<UseWPF>true</UseWPF>` and `<UseWindowsForms>true</UseWindowsForms>` with `<Using Remove="System.Drawing" />` and `<Using Remove="System.Windows.Forms" />` to prevent type collisions (`Color`, `Brush`, `Point`, `Application`).
- **DPI & Targeting:** `<ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>` and `<EnableWindowsTargeting>true</EnableWindowsTargeting>`.

## Portable Environment Setup (Run from `<project-root>`)
```powershell
New-Item -ItemType Directory -Force -Path ".\cache\temp" | Out-Null
$env:TEMP = (Resolve-Path ".\cache\temp").Path
$env:TMP  = $env:TEMP
```

## Common Commands
1. **Build & Run (Debug, zero-NuGet default):**
   ```powershell
   dotnet build PulseIsland.sln -c Debug
   dotnet run --project src\PulseIsland\PulseIsland.csproj
   ```
2. **Run Tests (Release, zero-NuGet runner):**
   ```powershell
   dotnet run --project tests\PulseIsland.Tests\PulseIsland.Tests.csproj --no-restore -c Release
   ```
3. **Local Portable Publish (`dist\`):**
   ```powershell
   dotnet publish src\PulseIsland\PulseIsland.csproj --no-restore -c Release -o dist
   ```
4. **GitHub Actions Release Artifacts (`.github\workflows\release.yml`):**
   - **Portable (framework-dependent, requires .NET 8 Desktop Runtime):**
     ```powershell
     dotnet publish src\PulseIsland\PulseIsland.csproj --no-restore -c Release -o .\release_out\portable
     ```
   - **Standalone (self-contained single-file `win-x64`, zero .NET install required):**
     ```powershell
     dotnet publish src\PulseIsland\PulseIsland.csproj -c Release -r win-x64 --self-contained true `
       -p:PublishSingleFile=true `
       -p:IncludeNativeLibrariesForSelfExtract=true `
       -p:EnableCompressionInSingleFile=true `
       -o .\release_out\standalone
     ```

## Portable Isolation Check
- `.NET` single-file self-extraction defaults `DOTNET_BUNDLE_EXTRACT_BASE_DIR` to `%TEMP%`. When testing strict portable isolation with a single-file standalone build, set `DOTNET_BUNDLE_EXTRACT_BASE_DIR` to `<portable-root>\cache\dotnet` or use the non-single-file portable publish.
- Always verify `dotnet build` exits with code `0` and zero nullable or Win32/COM warnings.
