---
name: pinvoke-win32-interop
description: >-
  Use this skill when implementing global hotkeys, window style flags and
  WS_EX_NOACTIVATE focus toggling, heap-allocated OVERLAPPED HID I/O, Win32
  waveIn audio capture, multi-monitor DPI queries, or GDI screen capture.
---

# P/Invoke & Win32 Interop Skill

## Core Win32 Subsystems in PulseIsland
1. **Global Hotkeys (`Interop/HotkeyService.cs`, `user32.dll`):**
   - `RegisterHotKey` / `UnregisterHotKey` + `HwndSource.FromHwnd(hwnd).AddHook(WndProc)` listening for `WM_HOTKEY (0x0312)`.
   - Always unregister hotkeys on exit and surface registration conflicts to the user in Settings.
2. **Window Styling & `WS_EX_NOACTIVATE` Focus Toggling (`Interop/Win32Api.cs`, `UI/IslandWindow.xaml.cs`):**
   - `GetWindowLong` / `SetWindowLong` (`GWL_EXSTYLE = -20`): apply `WS_EX_TOOLWINDOW` (hide from Alt+Tab/taskbar), `WS_EX_TOPMOST`, `WS_EX_TRANSPARENT` (click-through when needed), and `WS_EX_NOACTIVATE` so clicking the island does not steal focus from games or media players.
   - **In-Island Search `TextBox` Focus Toggle:** Call `EnableInteractiveKeyboardFocus()` to temporarily clear `WS_EX_NOACTIVATE` when focusing `TranslationSearchBox`, then restore `WS_EX_NOACTIVATE` on `OnTranslationSearchLostFocus` / collapse so typing works cleanly without permanent focus stealing.
3. **Multi-Monitor, DPI & Fullscreen Detection (`Interop/MonitorService.cs`):**
   - `EnumDisplayMonitors` / `MonitorFromPoint` + `GetMonitorInfo` + `VisualTreeHelper.GetDpi` for PerMonitorV2 physical-pixel to DIP conversion.
   - Compare `GetForegroundWindow()` + `GetWindowRect()` against monitor bounds (excluding Shell/Desktop windows) to auto-hide in fullscreen apps.
4. **Screen Capture & Alpha Compositing (`Interop/ScreenCaptureService.cs`):**
   - Capture directly to an in-memory BGRA32 buffer via `GetDC` -> `CreateCompatibleDC` -> `BitBlt(SRCCOPY)` -> top-down 32-bit `GetDIBits` (`biHeight = -Height`), releasing all GDI handles in `finally` with zero disk I/O.
   - Use `CompositeBgraOverWhiteIfNeeded` + `EncodeToJpegBytes` before sending images to Vision LLMs so transparent PNG/WebP/GIF stickers and dark-ink diagrams never render with black backgrounds in JPEG.
5. **Heap-Allocated `OVERLAPPED` + `CancelIoEx` HID I/O (`Services/MouseBatteryService.cs`):**
   - Never wrap an `Interrupt-IN` USB HID handle in `FileStream.ReadAsync` + `Dispose()` with a timeout — `FileStream.Dispose()` blocks indefinitely on silent drivers (`hidusb.sys`).
   - Use raw Win32 `ReadFile`/`WriteFile` with `Marshal.AllocHGlobal`-allocated `OVERLAPPED` and native buffers (`ReadHidOverlappedWithCancel`, `WriteHidOverlappedWithCancel`), `WaitForSingleObject`, and `CancelIoEx(handle, ovPtr)` before freeing buffers or closing the handle.
   - Supports Bluetooth PnP battery properties, wired USB charging detection, and 2.4 GHz HID dongles (Compx/VXE/VGN/ATK/Scyrox/Darmoshark/Zaopin/Ajazz/Mchose, Lamzu/Pulsar/Nordic, Beken/Rongyuan, Logitech HID++ 2.0, Razer HyperSpeed).
   - Handle `WM_DEVICECHANGE (0x0219)` in `WndProc` with a debounce timer for instant hotplug/charging updates.
6. **Win32 `waveIn` 16 kHz Mono FIFO Capture (`Services/WhisperVoiceService.cs`):**
   - Capture 16 kHz / 16-bit / mono PCM directly via `winmm.dll` (`waveInOpen(WAVE_MAPPER)`, `waveInPrepareHeader`, `waveInAddBuffer`, `waveInStart`, `waveInUnprepareHeader`, `waveInReset`, `waveInClose`).
   - Traverse completed `WAVEHDR` buffers (`WHDR_DONE`) in strict FIFO ring order starting from `_nextBufferIndex` so recorded audio stays chronologically exact.

## Safety Rules
- Declare `[DllImport(..., SetLastError = true)]` with exact blittable types (`IntPtr`, `uint`, `int`) and `Pack = 1` where native structs require it (`WAVEFORMATEX`).
- Always pair native allocations (`AllocHGlobal`, GDI objects, `SafeFileHandle`) with `try`/`finally` cleanup.
