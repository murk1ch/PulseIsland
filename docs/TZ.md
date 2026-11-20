# Technical Specification
**Project Name:** **PulseIsland**  
**Target OS:** Windows 10 / Windows 11  
**Tech Stack:** C# (.NET 8+), WPF  
**Deployment Directory (Portable Mode):** `<portable-root>`  

---

## 1. Project Objective
Develop a lightweight, resident, portable Windows application (**PulseIsland**) whose UI is designed as an interactive top-edge "Dynamic Island". The application provides fast screen text translation via cursor lasso selection, live music display and playback control with real-time audio wave visualization, in-island notifications, local AI assistance, smart widgets, and flexible settings.

---

## 2. Portable Storage & Isolation Requirements
To keep the system drive clean and make the app effortlessly portable, PulseIsland operates in **strict Portable Mode**:
- **Application Root Folder:** `<portable-root>`
- **No System-Drive Writes:** No configuration files, caches, logs, temporary files, or downloaded language/AI models may be saved to `%AppData%`, `%LocalAppData%`, system `%Temp%`, or `Documents`.
- **Local Directory Structure inside `<portable-root>`:**
  - `<portable-root>\PulseIsland.exe` — application executable and libraries.
  - `<portable-root>\config\` — settings file (`settings.json`), DPAPI-encrypted API keys, and local histories.
  - `<portable-root>\models\` — all downloaded offline models for translation and local AI (ONNX / GGUF / local runtimes).
  - `<portable-root>\cache\` and `<portable-root>\logs\` — temporary download staging, weather cache, and daily diagnostic logs.
- Any additional components downloaded via the UI (such as offline translation packs or local AI models) must be downloaded and unpacked **directly into subfolders of `<portable-root>`** (including `.part` staging files).

---

## 3. Island UI & States

The island sits centered along the top edge of the screen above all windows (`Topmost`) with three core states and smooth transition animations:

1. **Hidden State (Tucked to Top Edge):**
   - Slides past the top edge, leaving a thin, subtle trigger strip.
   - Hovering the mouse at the top edge smoothly reveals the island.
   - Users can hide the island manually or configure auto-hide behavior.
2. **Compact State (Base Pill):**
   - Rendered as a sleek rounded pill.
   - **Idle mode:** minimal footprint with subtle hover highlight (or optional system telemetry / wireless mouse battery badge when music is inactive).
   - **Playing music:** displays mini album cover (optional), track title (with marquee scrolling when overflowing), and live 60 FPS audio spectrum waves.
3. **Expanded State (Full Dashboard):**
   - Opens on click (or hover, depending on settings) on the compact pill.
   - Contains:
     - **Settings button** (gear icon) — **visible strictly in the Expanded state**.
     - Collapse/hide controls for both individual panels and the island itself.
     - Expanded media player widget (when a media session is active).
     - Notifications & recent translations history panels, Smart Clipboard button, and File Drop Shelf.

**Additional UI Requirements:**
- Proper multi-monitor and Per-Monitor V2 DPI scaling support.
- Optional automatic hiding when fullscreen applications (games, movies) are active.
- Full bilingual UI (English default, switchable to Russian live in Settings).

---

## 4. Functional Requirements

### 4.1. Screen Lasso Translation Module

#### 4.1.1. Activation & Cursor Behavior
- **Global Hotkey:** Configurable global shortcut (default `Alt+Q`, reassignable in Settings).
- When triggered:
  - Enters screen capture mode via a transparent fullscreen overlay with zero distortion of underlying windows.
  - Replaces the cursor with a **dot** followed by a smooth, fading **white cursor trail** at 60 FPS.
- **Text Selection:**
  - Hold **LMB** and circle or underline the target text on screen.
  - On release, computes the clamped bounding box around the drawn points, captures the screen region, and sends it to OCR + translation.
  - Pressing `Esc` or **RMB** cancels selection.

#### 4.1.2. OCR & Translation
- **Auto Language Detection:** Automatically detects the source text language (or uses a manual override).
- **Target Language:** Configurable in Settings (defaults to English, 16 languages supported).
- **Two Operating Modes (selectable in Settings):**
  1. **Online (Windows OCR + API):** Local `Windows.Media.Ocr` recognition followed by online translation (Google Translate / MyMemory / DeepL with DPAPI-encrypted API key).
  2. **Fully Offline Mode:** Local OCR and offline neural translation with zero internet access using ONNX MarianMT / Opus-MT models stored in `<portable-root>\models\`.

#### 4.1.3. Result Display
Translation results appear in **two places simultaneously**:
1. **Floating popup card near the selected region** (clamped to screen bounds) with copy button, close button, and optional auto-hide.
2. **Inside the Expanded island** in the session translation history for quick review and copying.

---

## 5. Music & Audio Wave Visualization Module

### 5.1. Media Player Integration
- Reads current track metadata and controls playback via Windows **SMTC (System Media Transport Controls)**.
- Works across browsers (YouTube, Spotify Web, etc.) and desktop media players (Spotify, AIMP, VLC, etc.).

### 5.2. Display & Controls
- **Display:** track title, artist, album art, and seekable progress bar.
- **Controls:** `Play / Pause`, `Next`, `Previous` (without triggering the Windows media OSD).
- **Live Audio Waves:**
  - Real-time system loopback capture via native COM **WASAPI Loopback** + active-music session filtering.
  - Radix-2 FFT spectral analysis rendering smooth 60 FPS frequency waves reacting to actual audio rhythm and amplitude.

---

## 6. Settings Module

Accessible strictly from the **Expanded** island state, organized into three tabs:

1. **General & Island:**
   - UI language selector (English default / Russian with live preview).
   - Theme selector (7 built-in themes with live preview), DWM glass blur, corner radius, and opacity.
   - Smart island features: Smart Clipboard, volume/mic indicator, File Drop Shelf, system telemetry badges, collapsible wireless mouse battery settings, and notifications settings.
   - Windows autostart (`HKCU\...\Run`), target monitor selection, hide delay, tray icon toggle, and fullscreen behavior.
2. **Translator & AI:**
   - Engine mode: Online (Windows OCR + API) / Fully Offline (ONNX).
   - Source/target languages and global hotkey recorders (lasso, clipboard, AI assistant).
   - Offline ONNX model manager and Local AI (`Qwen 2.5 3B` + `llama-server`) manager with disk usage summary and resumable downloads in `<portable-root>\models\`.
   - Encrypted DeepL API key input, cursor trail settings, and popup auto-hide timer.
3. **Music & Waves:**
   - Enable/disable music module, auto-collapse delay on pause, audio output device selector.
   - Visualizer style, sensitivity, smoothing, bar count, wave colors (fixed, gradient, or adaptive album art glow), and compact album art / marquee toggles.

---

## 7. Non-Functional Requirements

- **Performance:** Idle CPU usage under 1–2%; smooth 60 FPS visualization with zero memory leaks over extended sessions.
- **Security & Paths:** All paths resolved relative to `PortablePaths.RootDirectory`; API keys encrypted via Windows DPAPI (`CryptProtectData`/`CryptUnprotectData`).
