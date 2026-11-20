using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using PulseIsland.Services;

namespace PulseIsland.Interop;

public sealed class HotkeyService : IDisposable
{
    private const int TranslationHotkeyId = 0x5049; // "PI"
    private const int ClipboardHotkeyId = 0x504A;
    private const int AiFeedHotkeyId = 0x504B;
    private HwndSource? _hwndSource;
    private IntPtr _hwnd;
    private bool _registered;
    private bool _secondaryRegistered;
    private bool _aiFeedRegistered;

    public event Action? HotkeyPressed;
    public event Action? ClipboardHotkeyPressed;
    public event Action? AiFeedHotkeyPressed;

    public void Attach(Window window)
    {
        var helper = new WindowInteropHelper(window);
        _hwnd = helper.EnsureHandle();
        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);
    }

    public bool Register(string gesture)
    {
        if (_hwnd == IntPtr.Zero)
            return false;

        UnregisterPrimary();

        if (!TryParseGesture(gesture, out uint modifiers, out uint virtualKey))
        {
            LogService.Warn($"Invalid hotkey gesture '{gesture}', falling back to Alt+Q.");
            modifiers = Win32Api.MOD_ALT | Win32Api.MOD_NOREPEAT;
            virtualKey = (uint)KeyInterop.VirtualKeyFromKey(Key.Q);
        }

        _registered = Win32Api.RegisterHotKey(_hwnd, TranslationHotkeyId, modifiers, virtualKey);
        if (!_registered)
        {
            int err = Marshal.GetLastWin32Error();
            LogService.Warn($"RegisterHotKey failed for '{gesture}' (Win32 error {err}).");
        }

        return _registered;
    }

    public bool RegisterSecondary(string gesture, bool enabled)
    {
        if (_hwnd == IntPtr.Zero)
            return false;

        UnregisterSecondary();
        if (!enabled)
            return true;

        if (!TryParseGesture(gesture, out uint modifiers, out uint virtualKey))
        {
            LogService.Warn($"Invalid clipboard hotkey gesture '{gesture}', falling back to Ctrl+Alt+C.");
            modifiers = Win32Api.MOD_CONTROL | Win32Api.MOD_ALT | Win32Api.MOD_NOREPEAT;
            virtualKey = (uint)KeyInterop.VirtualKeyFromKey(Key.C);
        }

        _secondaryRegistered = Win32Api.RegisterHotKey(_hwnd, ClipboardHotkeyId, modifiers, virtualKey);
        if (!_secondaryRegistered)
        {
            int err = Marshal.GetLastWin32Error();
            LogService.Warn($"RegisterHotKey (Clipboard) failed for '{gesture}' (Win32 error {err}).");
        }

        return _secondaryRegistered;
    }

    public bool RegisterAiFeed(string gesture, bool enabled)
    {
        if (_hwnd == IntPtr.Zero)
            return false;

        UnregisterAiFeed();
        if (!enabled)
            return true;

        if (!TryParseGesture(gesture, out uint modifiers, out uint virtualKey))
        {
            LogService.Warn($"Invalid AI feed hotkey gesture '{gesture}', falling back to Ctrl+Alt+A.");
            modifiers = Win32Api.MOD_CONTROL | Win32Api.MOD_ALT | Win32Api.MOD_NOREPEAT;
            virtualKey = (uint)KeyInterop.VirtualKeyFromKey(Key.A);
        }

        _aiFeedRegistered = Win32Api.RegisterHotKey(_hwnd, AiFeedHotkeyId, modifiers, virtualKey);
        if (!_aiFeedRegistered)
        {
            int err = Marshal.GetLastWin32Error();
            LogService.Warn($"RegisterHotKey (AI Feed) failed for '{gesture}' (Win32 error {err}).");
        }

        return _aiFeedRegistered;
    }

    public void Unregister()
    {
        UnregisterPrimary();
        UnregisterSecondary();
        UnregisterAiFeed();
    }

    private void UnregisterPrimary()
    {
        if (_registered && _hwnd != IntPtr.Zero)
        {
            Win32Api.UnregisterHotKey(_hwnd, TranslationHotkeyId);
            _registered = false;
        }
    }

    private void UnregisterSecondary()
    {
        if (_secondaryRegistered && _hwnd != IntPtr.Zero)
        {
            Win32Api.UnregisterHotKey(_hwnd, ClipboardHotkeyId);
            _secondaryRegistered = false;
        }
    }

    private void UnregisterAiFeed()
    {
        if (_aiFeedRegistered && _hwnd != IntPtr.Zero)
        {
            Win32Api.UnregisterHotKey(_hwnd, AiFeedHotkeyId);
            _aiFeedRegistered = false;
        }
    }

    public static bool TryParseGesture(string gesture, out uint modifiers, out uint virtualKey)
    {
        modifiers = Win32Api.MOD_NOREPEAT;
        virtualKey = 0;

        if (string.IsNullOrWhiteSpace(gesture))
            return false;

        var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;

        Key? mainKey = null;
        foreach (var part in parts)
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= Win32Api.MOD_CONTROL;
            }
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= Win32Api.MOD_ALT;
            }
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= Win32Api.MOD_SHIFT;
            }
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= Win32Api.MOD_WIN;
            }
            else if (Enum.TryParse<Key>(part, ignoreCase: true, out var parsedKey))
            {
                mainKey = parsedKey;
            }
        }

        if (mainKey is null || mainKey == Key.None)
            return false;

        virtualKey = (uint)KeyInterop.VirtualKeyFromKey(mainKey.Value);
        return virtualKey != 0;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32Api.WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id == TranslationHotkeyId)
            {
                handled = true;
                HotkeyPressed?.Invoke();
            }
            else if (id == ClipboardHotkeyId)
            {
                handled = true;
                ClipboardHotkeyPressed?.Invoke();
            }
            else if (id == AiFeedHotkeyId)
            {
                handled = true;
                AiFeedHotkeyPressed?.Invoke();
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
    }
}
