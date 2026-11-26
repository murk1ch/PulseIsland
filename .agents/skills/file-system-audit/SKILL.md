---
name: file-system-audit
description: >-
  Use this skill after running PulseIsland or its tests to audit Windows user
  and temp directories (%AppData%, %LocalAppData%, system %TEMP%, Documents)
  and verify zero files or folders were created outside <portable-root>.
---

# File System Audit Skill

## Purpose
Guarantee strict portable isolation on **any Windows 10/11 PC** by scanning standard Windows user and temp directories (`%AppData%`, `%LocalAppData%`, system `%TEMP%`, `Documents`, and `%LocalAppData%\Temp\.net`) after running `PulseIsland.exe` or the test suite.

## Steps
1. Run the bundled audit script immediately after closing `PulseIsland` or completing tests:
   ```powershell
   powershell -ExecutionPolicy Bypass -File "<project-root>\.agents\skills\file-system-audit\scripts\audit_c_drive.ps1" -MinutesBack 15
   ```
   *(The script automatically resolves `<project-root>` relative to `$PSScriptRoot` and excludes any target path that already points inside `<project-root>`.)*
2. Inspect the output:
   - Any file or folder matching `*PulseIsland*` in `%AppData%`, `%LocalAppData%`, system `%TEMP%`, or `Documents` is a **violation**.
   - Any newly modified file in `%LocalAppData%\Temp\.net` flags an accidental `.NET` single-file extraction outside `<portable-root>`.
3. Remediate violations:
   - Trace the offending API call (`SpecialFolder`, `Path.GetTempPath()`, third-party cache, or `.NET` bundle extraction).
   - Redirect all writes to `PortablePaths.CacheDirectory`, `PortablePaths.DownloadsCacheDirectory`, `PortablePaths.ConfigDirectory`, `PortablePaths.ModelsDirectory`, or `PortablePaths.LogsDirectory` inside `<portable-root>`.
