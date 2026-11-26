---
name: nuget-package-management
description: >-
  Use this skill when adding, updating, restoring, or auditing NuGet packages
  for PulseIsland while keeping all caches strictly inside <project-root>\.nuget\
  and preserving the default zero-NuGet FullSdk=false build.
---

# NuGet Package Management Skill

## Zero-NuGet Default Architecture (`FullSdk=false`)
PulseIsland builds and runs offline by default (`<FullSdk>false</FullSdk>`) with **zero external NuGet packages** — Core Audio WASAPI, FFT, ONNX Runtime C-API, SentencePiece, Win32 HID/waveIn, and WPF UI are all implemented natively.
- Only add a `PackageReference` if strictly necessary, and gate optional SDK/test packages behind `Condition="'$(FullSdk)'=='true'"` so default offline builds never break.
- Current `FullSdk=true` packages:
  - `src\PulseIsland\PulseIsland.csproj`: `Microsoft.ML.OnnxRuntime` (`1.20.1`), `Microsoft.ML.Tokenizers` (`1.0.2`), plus built-in WinRT projection via `net8.0-windows10.0.19041.0`.
  - `tests\PulseIsland.Tests\PulseIsland.Tests.csproj`: `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`.

## Strict Portable Cache Isolation
Repository-level configuration already pins restore caches inside `<project-root>`:
- `Directory.Build.props`: `<RestorePackagesPath>$(MSBuildThisFileDirectory).nuget\packages</RestorePackagesPath>`
- `nuget.config`: `<add key="globalPackagesFolder" value=".\.nuget\packages" />` and `<add key="repositoryPath" value=".\.nuget\packages" />`

Before running `dotnet restore` or `dotnet add package`, also redirect HTTP/plugin caches and temp paths inside `<project-root>`:
```powershell
$root = (Resolve-Path ".").Path
New-Item -ItemType Directory -Force -Path "$root\.nuget\packages","$root\.nuget\v3-cache","$root\.nuget\plugins-cache","$root\cache\temp" | Out-Null
$env:NUGET_PACKAGES             = "$root\.nuget\packages"
$env:NUGET_HTTP_CACHE_PATH      = "$root\.nuget\v3-cache"
$env:NUGET_PLUGINS_CACHE_PATH   = "$root\.nuget\plugins-cache"
$env:TEMP                       = "$root\cache\temp"
$env:TMP                        = "$root\cache\temp"
```

## Workflow
1. Verify `nuget.config` and `Directory.Build.props` are intact and set the environment variables above.
2. Restore explicitly when using `FullSdk=true`:
   ```powershell
   dotnet restore PulseIsland.sln -p:FullSdk=true --packages .\.nuget\packages
   ```
3. Audit for vulnerabilities and conflicts:
   ```powershell
   dotnet list PulseIsland.sln package --vulnerable
   ```
4. Always re-verify the default zero-NuGet build (`dotnet build PulseIsland.sln -c Debug`) passes cleanly without restore.
