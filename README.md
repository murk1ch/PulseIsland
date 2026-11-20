<p align="center">
  <img src="assets/github/hero-banner.png" alt="PulseIsland — Portable Dynamic Island for Windows 10/11" width="100%" />
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Windows-10%20%2F%2011-0078D6?style=for-the-badge&logo=windows&logoColor=white" alt="Windows 10/11" />
  <img src="https://img.shields.io/badge/.NET%208-WPF-512BD4?style=for-the-badge&logo=dotnet&logoColor=white" alt=".NET 8 WPF" />
  <img src="https://img.shields.io/badge/Build-Zero--NuGet-0EA5E9?style=for-the-badge" alt="Zero-NuGet Build" />
  <img src="https://img.shields.io/badge/Mode-100%25%20Portable-8B5CF6?style=for-the-badge" alt="100% Portable" />
  <img src="https://img.shields.io/badge/UI-English%20%2F%20Russian-10B981?style=for-the-badge" alt="English / Russian UI" />
  <img src="https://img.shields.io/badge/License-MIT-F59E0B?style=for-the-badge" alt="MIT License" />
</p>

# PulseIsland

**PulseIsland** is a lightweight, 100% portable ("Zero-Install") Windows 10/11 desktop app (`.NET 8 WPF`) that brings a top-edge **Dynamic Island** experience to Windows — combining a live 60 FPS audio visualizer, system notifications, lasso screen OCR & offline neural translation, a local AI assistant, and smart hardware telemetry.

Built from the ground up to:
1. **Work out of the box** on any Windows PC — no installer required, zero external NuGet dependencies by default (`FullSdk=false`), and zero files written to `%AppData%` or `%LocalAppData%`.
2. **Be effortlessly customizable with AI coding agents** — includes [`AGENT.md`](AGENT.md), [`GOAL.md`](GOAL.md), and modular agent skills in [`.agents/skills/`](.agents/skills/) so any AI assistant (Antigravity, Cursor, Claude Code, Codex, etc.) immediately understands the architecture, guardrails, and build/test pipeline.

---

## Features

<p align="center">
  <img src="assets/github/features-showcase.png" alt="PulseIsland Features Showcase" width="100%" />
</p>

- **Dynamic Island UI & Styling:** `Hidden` / `Compact` / `Expanded` states, real Windows DWM acrylic backdrop blur, 7 built-in themes, custom corner radius & opacity, smooth animations, and **bilingual UI (English by default, live switch to Russian in Settings)**.
- **Music & 60 FPS Visualizer:** Native COM WASAPI loopback capture + Core Audio active-music session filter (ignores voice calls/Discord) + Radix-2 FFT + Windows SMTC media controls (without triggering the Windows volume/media OSD).
- **In-Island System Notifications:** Event-driven WNF + `UserNotificationListener` capture, Windows bottom-right toast suppression, corner popup interception, click-to-open chat activation (Telegram/Discord), and built-in in-memory WAV chime synthesizer.
- **Screen Lasso OCR & Offline Translator:** Freehand screen lasso (`Alt+Q`) with 60 FPS fading cursor trail, clipboard translation (`Ctrl+Alt+C`), local `Windows.Media.Ocr`, online providers (Google / MyMemory / DeepL with Windows DPAPI key encryption), and fully offline ONNX MarianMT (`onnxruntime.dll` C-API + pure C# SentencePiece tokenizer) with resumable Hugging Face downloads.
- **Local AI Assistant:** `Qwen 2.5 3B Instruct` (`Q4_K_M` GGUF) powered by portable `llama-server.exe` (Vulkan GPU auto-detection with CPU fallback) on `127.0.0.1`, dedicated hotkey (`Ctrl+Alt+A`), text summaries, translation, and live weather (`wttr.in`).
- **Smart Widgets & Wireless Mouse Battery:** CPU/RAM/network/clock/PC battery/Uptime badges, searchable & pinnable Smart Clipboard, drag-and-drop File Shelf, and wireless mouse battery monitoring (Bluetooth via `DEVPKEY_Device_ContainerId` and 2.4 GHz / USB: Beken/Attack Shark, Logitech HID++, Razer HyperSpeed, HID Power Page `0x84`/`0x85`).

---

## Quick Start (Build, Test & Run)

### Requirements
- Windows 10 (19041+) or Windows 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### Build, Test & Publish
Open PowerShell in the cloned repository root:

```powershell
# Optional: isolate temporary build files inside the project folder
New-Item -ItemType Directory -Force -Path ".\cache\temp" | Out-Null
$env:TEMP = (Resolve-Path ".\cache\temp").Path
$env:TMP  = $env:TEMP

# First build after cloning (generates obj/project.assets.json with zero NuGet downloads)
dotnet build PulseIsland.sln -c Debug

# Run the built-in test suite (23 tests)
dotnet run --project tests\PulseIsland.Tests\PulseIsland.Tests.csproj --no-restore -c Release

# Publish portable executable to .\dist\
dotnet publish src\PulseIsland\PulseIsland.csproj --no-restore -c Release -o dist
```

Then run `.\dist\PulseIsland.exe`. On first launch, PulseIsland automatically creates `config\`, `models\`, `cache\`, and `logs\` inside its portable root directory.

---

## Portable Storage Layout

All application data stays strictly inside the portable root folder (see `PortablePaths.cs`):
- `config\` — `settings.json`, DPAPI-encrypted DeepL API key, translation history, and clipboard history.
- `models\` — offline ONNX translation models, `_runtime\onnxruntime.dll`, `_llama\llama-server.exe`, `qwen2.5-3b-instruct\`.
- `cache\` — resumable downloads (`downloads\`), `weather_cache.json`, `cache\temp\`.
- `logs\` — daily `LogService` diagnostics.

---

## Customizing with an AI Agent

Want to adapt PulseIsland to your own workflow (add a new theme, custom widget, or support for another wireless mouse) using an AI coding agent?

1. Open the repository in your AI IDE / CLI (Antigravity, Cursor, Claude Code, Codex, etc.).
2. Ask the agent to read:
   - [`AGENT.md`](AGENT.md) — project guardrails, build/test pipeline, and Windows/WPF/HID pitfalls;
   - [`GOAL.md`](GOAL.md) — product vision, subsystem status, and roadmap ideas;
   - [`.agents/skills/pulseisland-codebase-map/SKILL.md`](.agents/skills/pulseisland-codebase-map/SKILL.md) — module map and specialized skills in `.agents/skills/`.
3. Describe your feature — the agent can rely on the built-in skills, run the 23-test suite, and publish a ready-to-run `dist\PulseIsland.exe`.

---

## License

Licensed under the [MIT License](LICENSE).
