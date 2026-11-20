---
name: file-system-audit
description: >-
  Use this skill after running PulseIsland or its tests to audit drive C:
  (AppData, LocalAppData, Temp) and verify zero files or folders were created
  outside <project-root>.
---

# File System Audit Skill

## Purpose
Guarantee strict portable isolation by scanning standard Windows user/temp directories in system user directories (`AppData`, `LocalAppData`, `%Temp%`) before and after running `PulseIsland.exe` or `dotnet test`.

## Steps
1. Record a baseline timestamp before launching the app, or run the audit script immediately after closing `PulseIsland`:
   ```powershell
   powershell -ExecutionPolicy Bypass -File "<project-root>\.agents\skills\file-system-audit\scripts\audit_c_drive.ps1" -MinutesBack 15
   ```
2. Inspect the output:
   - Any file or folder matching `*PulseIsland*` in `%AppData%`, `%LocalAppData%`, `%Temp%`, or `Documents` is a **violation**.
   - Also review any newly modified files in `%Temp%` or `%LocalAppData%\.net` during the test window to catch accidental `.NET` single-file extraction or third-party library temp writes.
3. If any violation is found, trace the offending API call and redirect it to `<project-root>\cache\` or `<project-root>\config\`.
