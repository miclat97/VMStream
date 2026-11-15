// ============================================================================
// LowLevelKeyboardHook.cs - WH_KEYBOARD_LL Hook for ActiveX-Focused Hotkeys
// Intercepts host hotkeys before mstscax.dll swallows keyboard input:
//   - Ctrl + Alt + Pause (or Ctrl + Alt + Shift + F): Toggle Borderless Fullscreen
//   - Ctrl + Alt + Shift + H: Toggle Floating Quick-Settings HUD
//   - Ctrl + Alt + Shift + M: Toggle Host Microphone Forwarding ON/OFF
//   - Esc (when Quick-Settings HUD is visible): Dismiss HUD
// ============================================================================
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HvConsole.Host.Input;

public sealed class LowLevelKeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN     = 0x0100;
    private const int WM_KEYUP       = 0x0101;
    private const int WM_SYSKEYDOWN  = 0x0104;
    private const int WM_SYSKEYUP    = 0x0105;

    private const uint LLKHF_ALTDOWN = 0x20u;

    private const int VK_CANCEL   = 0x03;
    private const int VK_SHIFT    = 0x10;
    private const int VK_CONTROL  = 0x11;
    private const int VK_MENU     = 0x12; // Alt
    private const int VK_PAUSE    = 0x13;
    private const int VK_ESCAPE   = 0x1B;
    private const int VK_F        = 0x46;
    private const int VK_H        = 0x48;
    private const int VK_M        = 0x4D;
    private const int VK_LSHIFT   = 0xA0;
    private const int VK_RSHIFT   = 0xA1;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_LMENU    = 0xA4;
    private const int VK_RMENU    = 0xA5;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        LowLevelKeyboardProc lpfn,
        IntPtr hMod,
        uint dwThreadId
    );

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private readonly LowLevelKeyboardProc _hookDelegate;
    private readonly uint _currentProcessId;
    private IntPtr _moduleHandle = IntPtr.Zero;
    private IntPtr _hookHandle = IntPtr.Zero;
    private int _swallowedHotkeyVk;
    private bool _disposed;

    public Func<bool>? IsHudVisibleCallback { get; set; }
    public event Action? ToggleFullscreenRequested;
    public event Action? ToggleHudRequested;
    public event Action? DismissHudRequested;
    public event Action? ToggleMicrophoneRequested;

    public LowLevelKeyboardHook()
    {
        _hookDelegate = HookCallback;
        _currentProcessId = (uint)Environment.ProcessId;
    }

    public void Install()
    {
        if (_hookHandle != IntPtr.Zero || _disposed) return;

        EnsureModuleHandle();
        _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookDelegate, _moduleHandle, 0);
    }

    /// <summary>
    /// Re-installs this WH_KEYBOARD_LL hook at the head of the global hook chain so it runs
    /// BEFORE mstscax.dll's internal hook when KeyboardHookMode=1 is active.
    /// Must be called on the UI message-loop thread.
    /// </summary>
    public void BringToFront()
    {
        if (_disposed) return;

        if (_hookHandle == IntPtr.Zero)
        {
            Install();
            return;
        }

        EnsureModuleHandle();
        IntPtr newHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookDelegate, _moduleHandle, 0);
        if (newHandle == IntPtr.Zero)
        {
            return;
        }

        IntPtr oldHandle = _hookHandle;
        _hookHandle = newHandle;
        try { UnhookWindowsHookEx(oldHandle); } catch { }
    }

    private void EnsureModuleHandle()
    {
        if (_moduleHandle != IntPtr.Zero) return;

        try
        {
            using Process curProcess = Process.GetCurrentProcess();
            using ProcessModule? curModule = curProcess.MainModule;
            _moduleHandle = GetModuleHandle(curModule?.ModuleName);
        }
        catch { }

        if (_moduleHandle == IntPtr.Zero)
        {
            _moduleHandle = GetModuleHandle(null);
        }
    }

    private static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            long msg = wParam.ToInt64();
            bool isKeyDown = (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN);
            bool isKeyUp   = (msg == WM_KEYUP   || msg == WM_SYSKEYUP);

            if (isKeyUp && _swallowedHotkeyVk != 0)
            {
                var kbUp = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if ((int)kbUp.vkCode == _swallowedHotkeyVk)
                {
                    _swallowedHotkeyVk = 0;
                    return new IntPtr(1);
                }
            }

            if (isKeyDown && IsOurProcessForeground())
            {
                var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                int vk = (int)kb.vkCode;

                bool ctrlDown  = IsKeyDown(VK_CONTROL) || IsKeyDown(VK_LCONTROL) || IsKeyDown(VK_RCONTROL);
                bool altDown   = ((kb.flags & LLKHF_ALTDOWN) != 0) || IsKeyDown(VK_MENU) || IsKeyDown(VK_LMENU) || IsKeyDown(VK_RMENU);
                bool shiftDown = IsKeyDown(VK_SHIFT)   || IsKeyDown(VK_LSHIFT)   || IsKeyDown(VK_RSHIFT);

                // Esc auto-dismisses the floating Quick-Settings HUD when visible
                if (vk == VK_ESCAPE && IsHudVisibleCallback?.Invoke() == true)
                {
                    _swallowedHotkeyVk = vk;
                    DismissHudRequested?.Invoke();
                    return new IntPtr(1);
                }

                // Ctrl + Alt + Pause/Break OR Ctrl + Alt + Shift + F -> Toggle Borderless Fullscreen
                if (ctrlDown && altDown && (vk == VK_PAUSE || vk == VK_CANCEL || (shiftDown && vk == VK_F)))
                {
                    _swallowedHotkeyVk = vk;
                    ToggleFullscreenRequested?.Invoke();
                    return new IntPtr(1); // Swallow keystroke so VM never receives it
                }

                // Ctrl + Alt + Shift + H -> Toggle Quick-Settings HUD
                if (ctrlDown && altDown && shiftDown && vk == VK_H)
                {
                    _swallowedHotkeyVk = vk;
                    ToggleHudRequested?.Invoke();
                    return new IntPtr(1);
                }

                // Ctrl + Alt + Shift + M -> Toggle Host Microphone Forwarding ON/OFF
                if (ctrlDown && altDown && shiftDown && vk == VK_M)
                {
                    _swallowedHotkeyVk = vk;
                    ToggleMicrophoneRequested?.Invoke();
                    return new IntPtr(1);
                }
            }
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private bool IsOurProcessForeground()
    {
        IntPtr fgHwnd = GetForegroundWindow();
        if (fgHwnd == IntPtr.Zero) return false;

        GetWindowThreadProcessId(fgHwnd, out uint fgPid);
        return fgPid == _currentProcessId;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }
}
