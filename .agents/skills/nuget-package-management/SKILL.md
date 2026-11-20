---
name: nuget-package-management
description: >-
  Use this skill when adding, updating, restoring, or auditing NuGet packages
  for the PulseIsland project, ensuring NuGet caches stay inside `<project-root>\.nuget\` and versions
  are compatible with .NET 8 WPF and WinRT.
---

# NuGet Package Management Skill

## Strict Disk Isolation for NuGet
Before running `dotnet restore` or `dotnet add package`, redirect NuGet caches away from `C:\Users\<user>\.nuget`:
```powershell
$env:NUGET_PACKAGES = "<project-root>\.nuget\packages"
$env:NUGET_HTTP_CACHE_PATH = "<project-root>\.nuget\v3-cache"
$env:NUGET_PLUGINS_CACHE_PATH = "<project-root>\.nuget\plugins-cache"
```
Alternatively, place a `nuget.config` in the repository root with `<add key="globalPackagesFolder" value="<project-root>\.nuget\packages" />`.

## Recommended Packages for PulseIsland
- Audio & Loopback: `NAudio`
- Local AI / ONNX: `Microsoft.ML.OnnxRuntime`, `Microsoft.ML.Tokenizers`
- JSON & Config: `System.Text.Json` (built-in in .NET 8)
- Testing: `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`
- WinRT: Built into `net8.0-windows10.0.19041.0` TFM (`Microsoft.Windows.SDK.NET.Ref`).

## Steps
1. Set `NUGET_*` environment variables or verify `nuget.config`.
2. Install package: `dotnet add <Project>.csproj package <PackageName> --version <Version>`.
3. Restore explicitly: `dotnet restore --packages <project-root>\.nuget\packages`.
4. Verify no `PackageReference` conflicts or vulnerable package warnings (`dotnet list package --vulnerable`).
