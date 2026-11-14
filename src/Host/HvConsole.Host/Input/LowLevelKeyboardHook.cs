// ============================================================================
// LowLevelKeyboardHook.cs — WH_KEYBOARD_LL Hook for ActiveX-Focused Hotkeys
// Intercepts host hotkeys before mstscax.dll swallows keyboard input:
//   - Ctrl + Alt + Pause (or Ctrl + Alt + Shift + F): Toggle Borderless Fullscreen
//   - Ctrl + Alt + Shift + H: Toggle Floating Quick-Settings HUD
//   - Ctrl + Alt + Shift + M: Toggle Host Microphone Forwarding ON/OFF
//   - Esc (when Quick-Settings HUD is visible): Dismiss HUD
// ============================================================================
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace HvConsole.Host.Input;

public sealed class LowLevelKeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN     = 0x0100;
    private const int WM_SYSKEYDOWN  = 0x0104;

    private const int VK_SHIFT   = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU    = 0x12; // Alt
    private const int VK_PAUSE   = 0x13;
    private const int VK_ESCAPE  = 0x1B;
    private const int VK_F       = 0x46;
    private const int VK_H       = 0x48;
    private const int VK_M       = 0x4D;

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
    private IntPtr _hookHandle = IntPtr.Zero;
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

        using Process curProcess = Process.GetCurrentProcess();
        using ProcessModule? curModule = curProcess.MainModule;
        IntPtr hMod = GetModuleHandle(curModule?.ModuleName);

        _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookDelegate, hMod, 0);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
        {
            if (IsOurProcessForeground())
            {
                var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                int vk = (int)kb.vkCode;

                bool ctrlDown  = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                bool altDown   = (GetAsyncKeyState(VK_MENU)    & 0x8000) != 0;
                bool shiftDown = (GetAsyncKeyState(VK_SHIFT)   & 0x8000) != 0;

                // Esc auto-dismisses the floating Quick-Settings HUD when visible
                if (vk == VK_ESCAPE && IsHudVisibleCallback?.Invoke() == true)
                {
                    DismissHudRequested?.Invoke();
                    return new IntPtr(1);
                }

                // Ctrl + Alt + Pause OR Ctrl + Alt + Shift + F -> Toggle Borderless Fullscreen
                if (ctrlDown && altDown && (vk == VK_PAUSE || (shiftDown && vk == VK_F)))
                {
                    ToggleFullscreenRequested?.Invoke();
                    return new IntPtr(1); // Swallow keystroke so VM never receives it
                }

                // Ctrl + Alt + Shift + H -> Toggle Quick-Settings HUD
                if (ctrlDown && altDown && shiftDown && vk == VK_H)
                {
                    ToggleHudRequested?.Invoke();
                    return new IntPtr(1);
                }

                // Ctrl + Alt + Shift + M -> Toggle Host Microphone Forwarding ON/OFF
                if (ctrlDown && altDown && shiftDown && vk == VK_M)
                {
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
