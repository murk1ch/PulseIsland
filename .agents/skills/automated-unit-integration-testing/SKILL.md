---
name: automated-unit-integration-testing
description: >-
  Use this skill when writing and running xUnit unit and integration tests for
  PulseIsland (bounding-box calculation from mouse lasso points, DPAPI key
  encryption, portable path resolution, and settings serialization).
---

# Automated Unit & Integration Testing Skill

## Test Project Setup
- Create `PulseIsland.Tests` (`xunit`) targeting `net8.0-windows10.0.19041.0`.
- Keep all test outputs and NuGet packages on `<project-root>`.

## Core Test Suites to Maintain
1. **Lasso Bounding Box Calculator:**
   - Given a `List<Point>` from mouse-drag obvodka, verify min/max X/Y, padding margin, clamping to monitor bounds, and rejection of degenerate tiny strokes (e.g., accidental single click < 5x5 px).
2. **DPAPI Encryption (`ProtectedData`):**
   - Round-trip test for API key encryption/decryption (`DataProtectionScope.CurrentUser`), ensuring plaintext never appears in serialized `settings.json`.
3. **Settings & Portable Path Resolution:**
   - Verify `SettingsService` loads defaults (target language = `"ru"`, auto-detect = `true`), saves/reloads JSON cleanly, and resolves all paths strictly under `AppContext.BaseDirectory` (`<project-root>`).
4. **Audio/FFT Math:**
   - Feed a synthetic `1000 Hz` sine wave buffer into the FFT pipeline and assert the peak magnitude lands in the expected frequency band.

## Running Tests
```powershell
dotnet test PulseIsland.Tests/PulseIsland.Tests.csproj --logger "console;verbosity=normal"
```
