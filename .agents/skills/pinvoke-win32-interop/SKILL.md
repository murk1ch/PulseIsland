---
name: pinvoke-win32-interop
description: >-
  Use this skill when implementing global hotkeys, mouse/keyboard hooks,
  click-through or topmost window flags, multi-monitor DPI queries, or screen
  capture using Win32 P/Invoke in WPF/C#.
---

# P/Invoke & Win32 Interop Skill

## Common Win32 APIs for PulseIsland
1. **Global Hotkeys (`user32.dll`):**
   - `RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk)` and `UnregisterHotKey(IntPtr hWnd, int id)`.
   - Hook `HwndSource.FromHwnd(hwnd).AddHook(WndProc)` to listen for `WM_HOTKEY (0x0312)`.
   - Always unregister hotkeys on `Window.Closing` / app exit.
2. **Window Styling & Behavior (`user32.dll`):**
   - `GetWindowLong` / `SetWindowLong` with `GWL_EXSTYLE (-20)`:
     - `WS_EX_TOOLWINDOW (0x00000080)` to hide the island from `Alt+Tab` and taskbar.
     - `WS_EX_TRANSPARENT (0x00000020)` when click-through is needed.
     - `WS_EX_NOACTIVATE (0x08000000)` so clicking the island does not steal focus from the active player/app if undesired.
3. **Multi-Monitor & DPI (`user32.dll`, `shcore.dll`):**
   - `MonitorFromPoint` / `EnumDisplayMonitors` + `GetMonitorInfo` for accurate monitor work area and physical bounds.
   - `GetDpiForMonitor` / `VisualTreeHelper.GetDpi(visual)` to convert physical pixels to WPF DIPs (`1.0 / dpiScale`).
4. **Fullscreen Detection:**
   - Compare `GetForegroundWindow()` + `GetWindowRect()` against monitor bounds (excluding Desktop/Shell windows) to optionally auto-hide the island during fullscreen games/videos.

## Safety Rules
- Declare `[LibraryImport]` or `[DllImport(..., SetLastError = true)]` with exact blittable types (`IntPtr`, `uint`, `int`).
- Never swallow Win32 errors silently when registering hotkeys; surface conflicts (e.g., hotkey already taken) to the user in settings.
- **HID Overlapped I/O (`CancelIoEx`):** Never wrap an `Interrupt-IN` HID handle in `FileStream.ReadAsync` + `Dispose()` with a timeout — `FileStream.Dispose()` blocks indefinitely on silent USB HID drivers (`hidusb.sys`). Use raw Win32 `ReadFile` + `NativeOverlapped` + `WaitForSingleObject` + `CancelIoEx(handle, IntPtr.Zero)` before freeing buffers or closing the handle (`MouseBatteryService.ReadHidOverlappedWithCancel`).
- **USB Hotplug (`WM_DEVICECHANGE` = `0x0219`):** Handle `WM_DEVICECHANGE` inside `HwndSource` `WndProc` with a short ~500 ms debounce timer to react instantaneously to wired mouse charging / dongle changes without polling.
