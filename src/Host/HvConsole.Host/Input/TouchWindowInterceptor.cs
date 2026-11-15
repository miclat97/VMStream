// ============================================================================
// TouchWindowInterceptor.cs - Complete mstscax.dll Touch & Mouse-Promotion Blocker
// 1. Installs thread-local WH_CALLWNDPROC hooks when mstscax.dll child windows
//    (IHWindowClass, OPWindowClass, UIMainClass, UIContainerClass, etc.) belong
//    to background threads so UnregisterTouchWindow, SetGestureConfig,
//    SetWindowFeedbackSetting, and SetWindowSubclass execute on the owning HWND thread.
// 2. Captures synchronized WM_POINTER* / WM_NCPOINTER* touch frames via
//    GetPointerFrameTouchInfo + SkipPointerFrameMessages, normalizes [0..32767]
//    with bezel edge snap, and streams 64-byte HID frames over AF_HYPERV.
// 3. Swallows WM_POINTER*, WM_NCPOINTER*, WM_TOUCH, WM_GESTURE*, WM_CONTEXTMENU,
//    and MI_WP_SIGNATURE synthesized mouse messages so mstscax.dll never emulates
//    mouse clicks or drags on touch input.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using HvConsole.Host.Protocol;
using HvConsole.Host.Transport;

namespace HvConsole.Host.Input;

public sealed class TouchWindowInterceptor : IDisposable
{
    private const uint WM_NULL                  = 0x0000;
    private const uint WM_SETFOCUS              = 0x0007;
    private const uint WM_SETCURSOR             = 0x0020;
    private const uint WM_CONTEXTMENU           = 0x007B;
    private const uint WM_NCDESTROY             = 0x0082;
    private const uint WM_NCMOUSEMOVE           = 0x00A0;
    private const uint WM_NCLBUTTONDOWN         = 0x00A1;
    private const uint WM_NCLBUTTONUP           = 0x00A2;
    private const uint WM_NCRBUTTONDOWN         = 0x00A4;
    private const uint WM_NCRBUTTONUP           = 0x00A5;
    private const uint WM_GESTURE               = 0x0119;
    private const uint WM_GESTURENOTIFY         = 0x011A;
    private const uint WM_MOUSEMOVE             = 0x0200;
    private const uint WM_LBUTTONDOWN           = 0x0201;
    private const uint WM_LBUTTONUP             = 0x0202;
    private const uint WM_LBUTTONDBLCLK         = 0x0203;
    private const uint WM_RBUTTONDOWN           = 0x0204;
    private const uint WM_RBUTTONUP             = 0x0205;
    private const uint WM_RBUTTONDBLCLK         = 0x0206;
    private const uint WM_MBUTTONDOWN           = 0x0207;
    private const uint WM_MBUTTONUP             = 0x0208;
    private const uint WM_TOUCH                 = 0x0240;
    private const uint WM_NCPOINTERUPDATE       = 0x0241;
    private const uint WM_NCPOINTERDOWN         = 0x0242;
    private const uint WM_NCPOINTERUP           = 0x0243;
    private const uint WM_POINTERUPDATE         = 0x0245;
    private const uint WM_POINTERDOWN           = 0x0246;
    private const uint WM_POINTERUP             = 0x0247;
    private const uint WM_POINTERENTER          = 0x0249;
    private const uint WM_POINTERLEAVE          = 0x024A;
    private const uint WM_POINTERACTIVATE       = 0x024B;
    private const uint WM_POINTERCAPTURECHANGED = 0x024C;
    private const uint WM_MOUSEHOVER            = 0x02A1;
    private const uint WM_MOUSELEAVE            = 0x02A3;

    private const uint PT_TOUCH = 0x00000002;

    private const uint POINTER_FLAG_INCONTACT = 0x00000004u;
    private const uint POINTER_FLAG_CANCELED  = 0x00008000u;
    private const uint POINTER_FLAG_DOWN      = 0x00010000u;
    private const uint POINTER_FLAG_UP        = 0x00040000u;

    private const int WH_CALLWNDPROC    = 4;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint GC_ALLGESTURES   = 0x00000001u;
    private const int PA_ACTIVATE       = 1;
    private const int EdgeSnapThresholdPx = 8;

    // Microsoft signature for mouse messages synthesized from Touch / Pen:
    // (GetMessageExtraInfo() & 0xFFFFFF00) == 0xFF515700 (MI_WP_SIGNATURE)
    private const ulong MI_WP_SIGNATURE_MASK = 0xFFFFFF00UL;
    private const ulong MI_WP_SIGNATURE      = 0xFF515700UL;

    private static readonly UIntPtr SubclassId = new(0x564D5354); // 'VMST'

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr SubclassProc(
        IntPtr hWnd,
        uint uMsg,
        IntPtr wParam,
        IntPtr lParam,
        UIntPtr uIdSubclass,
        UIntPtr dwRefData
    );

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GESTURECONFIG
    {
        public uint dwID;
        public uint dwWant;
        public uint dwBlock;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_INFO
    {
        public uint pointerType;
        public uint pointerId;
        public uint frameId;
        public uint pointerFlags;
        public IntPtr sourceDevice;
        public IntPtr hwndTarget;
        public POINT ptPixelLocation;
        public POINT ptHimetricLocation;
        public POINT ptPixelLocationRaw;
        public POINT ptHimetricLocationRaw;
        public uint dwTime;
        public uint historyCount;
        public int InputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_TOUCH_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint touchFlags;
        public uint touchMask;
        public RECT rcContact;
        public RECT rcContactRaw;
        public uint orientation;
        public uint pressure;
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnableMouseInPointer(bool fEnable);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterTouchWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsTouchWindow(IntPtr hWnd, out uint pulFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseTouchInputHandle(IntPtr hTouchInput);

    [DllImport("user32.dll")]
    private static extern IntPtr GetMessageExtraInfo();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeoutW(
        IntPtr hWnd,
        uint Msg,
        UIntPtr wParam,
        IntPtr lParam,
        uint fuFlags,
        uint uTimeout,
        out UIntPtr lpdwResult
    );

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetPointerType(uint pointerId, out uint pointerType);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetPointerTouchInfo(uint pointerId, out POINTER_TOUCH_INFO touchInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern unsafe bool GetPointerFrameTouchInfo(
        uint pointerId,
        ref uint pointerCount,
        POINTER_TOUCH_INFO* touchInfo
    );

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SkipPointerFrameMessages(uint pointerId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowFeedbackSetting(
        IntPtr hwnd,
        int feedback,
        uint dwFlags,
        uint size,
        ref int configuration
    );

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetGestureConfig(
        IntPtr hwnd,
        uint dwReserved,
        uint cIDs,
        ref GESTURECONFIG pGestureConfig,
        uint cbSize
    );

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(
        IntPtr hWnd,
        SubclassProc pfnSubclass,
        UIntPtr uIdSubclass,
        UIntPtr dwRefData
    );

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(
        IntPtr hWnd,
        SubclassProc pfnSubclass,
        UIntPtr uIdSubclass
    );

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern IntPtr DefSubclassProc(
        IntPtr hWnd,
        uint uMsg,
        IntPtr wParam,
        IntPtr lParam
    );

    private enum PendingOpKind
    {
        AttachOrReconfigure,
        Detach
    }

    private readonly struct PendingWindowOp
    {
        public PendingWindowOp(IntPtr hwnd, string className, PendingOpKind kind)
        {
            Hwnd = hwnd;
            ClassName = className;
            Kind = kind;
        }

        public IntPtr Hwnd { get; }
        public string ClassName { get; }
        public PendingOpKind Kind { get; }
    }

    private struct ActiveContactSlot
    {
        public bool Occupied;
        public bool TipSwitch;
        public bool Confidence;
        public uint Win32PointerId;
        public byte ContactId;
        public ushort X;
        public ushort Y;
        public IntPtr SourceDevice;
    }

    private readonly IHvTransport _touchTransport;
    private readonly SubclassProc _subclassDelegate;
    private readonly HookProc _callWndProcDelegate;
    private readonly uint _currentProcessId;
    private readonly long _qpcOrigin;

    private readonly HashSet<IntPtr> _subclassedHwnds = new();
    private readonly Dictionary<uint, IntPtr> _threadHooks = new();
    private readonly Dictionary<uint, List<PendingWindowOp>> _pendingOpsByThread = new();
    private readonly object _stateLock = new();

    private readonly ActiveContactSlot[] _slots = new ActiveContactSlot[HvProtocolConstants.MaxTouchContacts];
    private readonly object _touchLock = new();

    private IntPtr _rdpRootHwnd;
    private int _pendingOpCount;
    private int _activeTouchCount;
    private long _lastTouchTickMs;
    private volatile bool _disposed;

    public int ActiveTouchPoints => Volatile.Read(ref _activeTouchCount);

    public event Action? RdpFocusAcquired;

    public static void ConfigureProcessTouchSettings()
    {
        try
        {
            // Prevent Windows from promoting mouse messages into pointer messages
            EnableMouseInPointer(false);
        }
        catch { }
    }

    public TouchWindowInterceptor(IHvTransport touchTransport)
    {
        _touchTransport = touchTransport;
        _currentProcessId = GetCurrentProcessId();
        _qpcOrigin = Stopwatch.GetTimestamp();

        // Keep delegates rooted for the lifetime of the interceptor
        _subclassDelegate = SubclassWndProc;
        _callWndProcDelegate = CallWndProc;
    }

    /// <summary>
    /// Recursively prepares and subclasses the RDP ActiveX control and ALL internal
    /// mstscax.dll child windows (IHWindowClass, OPWindowClass, UIMainClass,
    /// UIContainerClass, ATL:*, etc.) on each window's owning thread.
    /// </summary>
    public int AttachToRdpControlHierarchy(IntPtr rdpRootHwnd)
    {
        if (rdpRootHwnd == IntPtr.Zero || _disposed) return 0;

        _rdpRootHwnd = rdpRootHwnd;
        var candidates = new List<(IntPtr Hwnd, string ClassName)>(16)
        {
            (rdpRootHwnd, "AxRdpRoot")
        };

        var classBuilder = new StringBuilder(128);
        EnumChildWindows(rdpRootHwnd, (childHwnd, _) =>
        {
            classBuilder.Clear();
            string className = GetClassNameW(childHwnd, classBuilder, classBuilder.Capacity) > 0
                ? classBuilder.ToString()
                : "UnknownChild";
            candidates.Add((childHwnd, className));
            return true;
        }, IntPtr.Zero);

        // Prune any previously tracked handles that Windows has destroyed
        lock (_stateLock)
        {
            if (_subclassedHwnds.Count > 0)
            {
                _subclassedHwnds.RemoveWhere(h => !IsWindow(h));
            }
        }

        uint currentTid = GetCurrentThreadId();
        int attachedOrScheduled = 0;

        foreach (var (hwnd, className) in candidates)
        {
            if (_disposed || hwnd == IntPtr.Zero || !IsWindow(hwnd)) continue;

            uint ownerTid = GetWindowThreadProcessId(hwnd, out uint ownerPid);
            if (ownerTid == 0 || ownerPid != _currentProcessId) continue;

            bool alreadySubclassed;
            lock (_stateLock)
            {
                alreadySubclassed = _subclassedHwnds.Contains(hwnd);
            }

            bool legacyTouchRegistered = false;
            try { legacyTouchRegistered = IsTouchWindow(hwnd, out _); } catch { }

            if (alreadySubclassed && !legacyTouchRegistered)
            {
                continue;
            }

            if (ownerTid == currentTid)
            {
                if (ExecuteConfigureAndSubclassOnOwningThread(hwnd, className))
                {
                    attachedOrScheduled++;
                }
            }
            else if (DispatchRemoteThreadOp(hwnd, ownerTid, className, PendingOpKind.AttachOrReconfigure, timeoutMs: 100))
            {
                attachedOrScheduled++;
            }
        }

        return attachedOrScheduled;
    }

    private bool DispatchRemoteThreadOp(IntPtr hwnd, uint targetTid, string className, PendingOpKind kind, uint timeoutMs)
    {
        lock (_stateLock)
        {
            if (!_threadHooks.ContainsKey(targetTid))
            {
                IntPtr hHook = SetWindowsHookExW(WH_CALLWNDPROC, _callWndProcDelegate, IntPtr.Zero, targetTid);
                if (hHook == IntPtr.Zero)
                {
                    Debug.WriteLine($"[TouchWindowInterceptor] SetWindowsHookExW failed for TID {targetTid}, err={Marshal.GetLastWin32Error()}");
                    return false;
                }
                _threadHooks[targetTid] = hHook;
            }

            if (!_pendingOpsByThread.TryGetValue(targetTid, out var list))
            {
                list = new List<PendingWindowOp>(4);
                _pendingOpsByThread[targetTid] = list;
            }

            bool alreadyQueued = false;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Hwnd == hwnd && list[i].Kind == kind)
                {
                    alreadyQueued = true;
                    break;
                }
            }

            if (!alreadyQueued)
            {
                list.Add(new PendingWindowOp(hwnd, className, kind));
                Interlocked.Increment(ref _pendingOpCount);
            }
        }

        // Release _stateLock BEFORE synchronous cross-thread send to prevent deadlocks
        IntPtr sendRes = SendMessageTimeoutW(hwnd, WM_NULL, UIntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, timeoutMs, out _);
        return sendRes != IntPtr.Zero;
    }

    private IntPtr CallWndProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Volatile.Read(ref _pendingOpCount) > 0)
        {
            List<PendingWindowOp>? opsToRun = null;
            uint currentTid = GetCurrentThreadId();

            lock (_stateLock)
            {
                if (_pendingOpsByThread.TryGetValue(currentTid, out var queued) && queued.Count > 0)
                {
                    opsToRun = new List<PendingWindowOp>(queued);
                    Interlocked.Add(ref _pendingOpCount, -queued.Count);
                    queued.Clear();
                }
            }

            if (opsToRun != null)
            {
                for (int i = 0; i < opsToRun.Count; i++)
                {
                    var op = opsToRun[i];
                    if (op.Kind == PendingOpKind.AttachOrReconfigure)
                    {
                        if (!_disposed && IsWindow(op.Hwnd))
                        {
                            ExecuteConfigureAndSubclassOnOwningThread(op.Hwnd, op.ClassName);
                        }
                    }
                    else if (op.Kind == PendingOpKind.Detach)
                    {
                        ExecuteDetachOnOwningThread(op.Hwnd);
                    }
                }
            }
        }

        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private bool ExecuteConfigureAndSubclassOnOwningThread(IntPtr hwnd, string className)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;

        // CRITICAL: mstscax.dll calls RegisterTouchWindow() on its internal child windows
        // (such as IHWindowClass) on a background thread. UnregisterTouchWindow MUST succeed
        // to switch Windows from legacy WM_TOUCH mouse emulation back to pure WM_POINTER*.
        try { UnregisterTouchWindow(hwnd); } catch { }

        DisableHostTouchVisualFeedback(hwnd);
        DisableLegacyGestures(hwnd);

        lock (_stateLock)
        {
            if (_subclassedHwnds.Contains(hwnd))
            {
                return false;
            }
        }

        if (SetWindowSubclass(hwnd, _subclassDelegate, SubclassId, UIntPtr.Zero))
        {
            lock (_stateLock)
            {
                _subclassedHwnds.Add(hwnd);
            }
            Debug.WriteLine($"[TouchWindowInterceptor] Subclassed {className} (HWND=0x{hwnd:X}, TID={GetCurrentThreadId()})");
            return true;
        }

        Debug.WriteLine($"[TouchWindowInterceptor] SetWindowSubclass failed for {className} (HWND=0x{hwnd:X}, err={Marshal.GetLastWin32Error()})");
        return false;
    }

    private void ExecuteDetachOnOwningThread(IntPtr hwnd)
    {
        try { RemoveWindowSubclass(hwnd, _subclassDelegate, SubclassId); } catch { }
        lock (_stateLock)
        {
            _subclassedHwnds.Remove(hwnd);
        }
    }

    private static void DisableHostTouchVisualFeedback(IntPtr hwnd)
    {
        try
        {
            // FEEDBACK_TOUCH_CONTACTVISUALIZATION (1), FEEDBACK_TOUCH_TAP (7),
            // FEEDBACK_TOUCH_DOUBLETAP (8), FEEDBACK_TOUCH_PRESSANDHOLD (9),
            // FEEDBACK_TOUCH_RIGHTTAP (10), FEEDBACK_GESTURE_PRESSANDTAP (11)
            int[] feedbackTypes = { 1, 7, 8, 9, 10, 11 };
            foreach (int fb in feedbackTypes)
            {
                int disabled = 0;
                SetWindowFeedbackSetting(hwnd, fb, 0, sizeof(int), ref disabled);
            }
        }
        catch { }
    }

    private static void DisableLegacyGestures(IntPtr hwnd)
    {
        try
        {
            var gc = new GESTURECONFIG
            {
                dwID = 0,
                dwWant = 0,
                dwBlock = GC_ALLGESTURES
            };
            SetGestureConfig(hwnd, 0, 1, ref gc, (uint)Marshal.SizeOf<GESTURECONFIG>());
        }
        catch { }
    }

    public void SendTouchReset()
    {
        lock (_touchLock)
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                _slots[i] = default;
            }
            Volatile.Write(ref _activeTouchCount, 0);
        }

        _touchTransport.SendStruct(HvPacketType.TouchReset, 0);
    }

    private bool IsTouchPromotedMouseMessage()
    {
        ulong extra = unchecked((ulong)GetMessageExtraInfo().ToInt64());
        if ((extra & MI_WP_SIGNATURE_MASK) == MI_WP_SIGNATURE)
        {
            return true;
        }

        if (Volatile.Read(ref _activeTouchCount) > 0)
        {
            return true;
        }

        long elapsedSinceTouch = Environment.TickCount64 - Interlocked.Read(ref _lastTouchTickMs);
        return elapsedSinceTouch >= 0 && elapsedSinceTouch < 250;
    }

    private bool IsWindowInRdpHierarchy(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        IntPtr root = _rdpRootHwnd;
        if (root == IntPtr.Zero) return false;
        return hwnd == root || IsChild(root, hwnd);
    }

    private IntPtr SubclassWndProc(
        IntPtr hWnd,
        uint uMsg,
        IntPtr wParam,
        IntPtr lParam,
        UIntPtr uIdSubclass,
        UIntPtr dwRefData)
    {
        switch (uMsg)
        {
            case WM_NCDESTROY:
            {
                IntPtr res = DefSubclassProc(hWnd, uMsg, wParam, lParam);
                try { RemoveWindowSubclass(hWnd, _subclassDelegate, SubclassId); } catch { }
                lock (_stateLock)
                {
                    _subclassedHwnds.Remove(hWnd);
                }
                return res;
            }

            case WM_SETFOCUS:
            {
                IntPtr res = DefSubclassProc(hWnd, uMsg, wParam, lParam);
                try { RdpFocusAcquired?.Invoke(); } catch { }
                return res;
            }

            case WM_POINTERDOWN:
            case WM_POINTERUPDATE:
            case WM_POINTERUP:
            case WM_NCPOINTERDOWN:
            case WM_NCPOINTERUPDATE:
            case WM_NCPOINTERUP:
            {
                uint pointerId = (uint)(wParam.ToInt64() & 0xFFFF);
                if (GetPointerType(pointerId, out uint pointerType) && pointerType == PT_TOUCH)
                {
                    Interlocked.Exchange(ref _lastTouchTickMs, Environment.TickCount64);
                    HandleTouchPointerFrameEvent(hWnd, pointerId);
                    return IntPtr.Zero;
                }
                break;
            }

            case WM_POINTERCAPTURECHANGED:
            {
                uint pointerId = (uint)(wParam.ToInt64() & 0xFFFF);
                if (GetPointerType(pointerId, out uint pointerType) && pointerType == PT_TOUCH)
                {
                    Interlocked.Exchange(ref _lastTouchTickMs, Environment.TickCount64);

                    // If capture simply shifted to another child inside _rdpRootHwnd (e.g., UIMainClass -> IHWindowClass)
                    // and the pointer is neither UP nor CANCELED, keep the touch contact down so drags/holds don't abort!
                    if (IsWindowInRdpHierarchy(lParam) &&
                        (!GetPointerTouchInfo(pointerId, out POINTER_TOUCH_INFO capInfo) ||
                         (capInfo.pointerInfo.pointerFlags & (POINTER_FLAG_UP | POINTER_FLAG_CANCELED)) == 0))
                    {
                        return IntPtr.Zero;
                    }

                    HandleSinglePointerRelease(hWnd, pointerId);
                    return IntPtr.Zero;
                }
                break;
            }

            case WM_POINTERENTER:
            case WM_POINTERLEAVE:
            {
                uint pointerId = (uint)(wParam.ToInt64() & 0xFFFF);
                if (GetPointerType(pointerId, out uint pointerType) && pointerType == PT_TOUCH)
                {
                    Interlocked.Exchange(ref _lastTouchTickMs, Environment.TickCount64);
                    return IntPtr.Zero;
                }
                break;
            }

            case WM_POINTERACTIVATE:
            {
                uint pointerId = (uint)(wParam.ToInt64() & 0xFFFF);
                if (GetPointerType(pointerId, out uint pointerType) && pointerType == PT_TOUCH)
                {
                    Interlocked.Exchange(ref _lastTouchTickMs, Environment.TickCount64);
                    return new IntPtr(PA_ACTIVATE);
                }
                break;
            }

            case WM_TOUCH:
            {
                Interlocked.Exchange(ref _lastTouchTickMs, Environment.TickCount64);
                if (lParam != IntPtr.Zero)
                {
                    try { CloseTouchInputHandle(lParam); } catch { }
                }
                try { UnregisterTouchWindow(hWnd); } catch { }
                return IntPtr.Zero;
            }

            case WM_GESTURENOTIFY:
            {
                DisableLegacyGestures(hWnd);
                return DefWindowProcW(hWnd, uMsg, wParam, lParam);
            }

            case WM_GESTURE:
            {
                if (IsTouchPromotedMouseMessage())
                {
                    return IntPtr.Zero;
                }
                break;
            }

            case WM_CONTEXTMENU:
            case WM_MOUSEMOVE:
            case WM_LBUTTONDOWN:
            case WM_LBUTTONUP:
            case WM_LBUTTONDBLCLK:
            case WM_RBUTTONDOWN:
            case WM_RBUTTONUP:
            case WM_RBUTTONDBLCLK:
            case WM_MBUTTONDOWN:
            case WM_MBUTTONUP:
            case WM_MOUSEHOVER:
            case WM_MOUSELEAVE:
            case WM_NCMOUSEMOVE:
            case WM_NCLBUTTONDOWN:
            case WM_NCLBUTTONUP:
            case WM_NCRBUTTONDOWN:
            case WM_NCRBUTTONUP:
            {
                // Swallow all touch-synthesized mouse and context-menu events so mstscax.dll never
                // moves the mouse cursor or opens mouse context menus on touch hold / edge drag
                if (IsTouchPromotedMouseMessage())
                {
                    return IntPtr.Zero;
                }
                break;
            }

            case WM_SETCURSOR:
            {
                if (IsTouchPromotedMouseMessage())
                {
                    return new IntPtr(1);
                }
                break;
            }
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private unsafe void HandleTouchPointerFrameEvent(IntPtr targetHwnd, uint pointerId)
    {
        uint pointerCount = 16;
        POINTER_TOUCH_INFO* stackInfos = stackalloc POINTER_TOUCH_INFO[(int)pointerCount];

        if (GetPointerFrameTouchInfo(pointerId, ref pointerCount, stackInfos) && pointerCount > 0)
        {
            ProcessTouchFrame(stackInfos, (int)pointerCount, targetHwnd, fromFullFrame: true);
            try { SkipPointerFrameMessages(pointerId); } catch { }
            return;
        }

        // Fallback if frame exceeded stack buffer or pointer metadata requires dynamic allocation
        uint neededCount = 0;
        if (!GetPointerFrameTouchInfo(pointerId, ref neededCount, null) && neededCount > 16 && neededCount <= 64)
        {
            var heapInfos = new POINTER_TOUCH_INFO[neededCount];
            fixed (POINTER_TOUCH_INFO* heapPtr = heapInfos)
            {
                uint count = neededCount;
                if (GetPointerFrameTouchInfo(pointerId, ref count, heapPtr) && count > 0)
                {
                    ProcessTouchFrame(heapPtr, (int)count, targetHwnd, fromFullFrame: true);
                    try { SkipPointerFrameMessages(pointerId); } catch { }
                    return;
                }
            }
        }

        // Single-pointer fallback if frame retrieval is unavailable
        if (GetPointerTouchInfo(pointerId, out POINTER_TOUCH_INFO singleInfo))
        {
            ProcessTouchFrame(&singleInfo, 1, targetHwnd, fromFullFrame: false);
        }
    }

    private unsafe void HandleSinglePointerRelease(IntPtr targetHwnd, uint pointerId)
    {
        if (GetPointerTouchInfo(pointerId, out POINTER_TOUCH_INFO singleInfo))
        {
            singleInfo.pointerInfo.pointerFlags |= POINTER_FLAG_UP;
            singleInfo.pointerInfo.pointerFlags &= ~POINTER_FLAG_INCONTACT;
            ProcessTouchFrame(&singleInfo, 1, targetHwnd, fromFullFrame: false);
            return;
        }

        HvTouchInputReport report = default;
        report.ReportId = HvProtocolConstants.TouchReportIdInput;
        report.ScanTime = ComputeScanTime();

        bool shouldSend = false;
        lock (_touchLock)
        {
            int slotIndex = FindOrAllocateSlot(pointerId, allowAllocate: false);
            if (slotIndex < 0)
            {
                return;
            }

            _slots[slotIndex].TipSwitch = false;
            _slots[slotIndex].Confidence = true;

            Span<bool> releaseMask = stackalloc bool[HvProtocolConstants.MaxTouchContacts];
            releaseMask[slotIndex] = true;
            PackAndReleaseSlots(ref report, releaseMask);
            shouldSend = report.ContactCount > 0;
        }

        if (shouldSend)
        {
            _touchTransport.SendStruct(HvPacketType.TouchFrame, in report);
        }
    }

    private unsafe void ProcessTouchFrame(
        POINTER_TOUCH_INFO* infos,
        int frameCount,
        IntPtr targetHwnd,
        bool fromFullFrame)
    {
        IntPtr normHwnd = (_rdpRootHwnd != IntPtr.Zero) ? _rdpRootHwnd : targetHwnd;
        GetClientRect(normHwnd, out RECT clientRect);
        if (clientRect.Width <= 0 || clientRect.Height <= 0)
        {
            normHwnd = targetHwnd;
            GetClientRect(normHwnd, out clientRect);
        }

        int width = Math.Max(1, clientRect.Width);
        int height = Math.Max(1, clientRect.Height);
        int maxX = Math.Max(1, width - 1);
        int maxY = Math.Max(1, height - 1);

        HvTouchInputReport report = default;
        report.ReportId = HvProtocolConstants.TouchReportIdInput;
        report.ScanTime = ComputeScanTime();

        bool shouldSend = false;
        IntPtr frameSourceDevice = IntPtr.Zero;

        lock (_touchLock)
        {
            Span<bool> seenSlots = stackalloc bool[HvProtocolConstants.MaxTouchContacts];
            Span<bool> releaseAfterSend = stackalloc bool[HvProtocolConstants.MaxTouchContacts];

            for (int i = 0; i < frameCount; i++)
            {
                POINTER_TOUCH_INFO info = infos[i];
                if (info.pointerInfo.pointerType != PT_TOUCH)
                {
                    continue;
                }

                if (frameSourceDevice == IntPtr.Zero && info.pointerInfo.sourceDevice != IntPtr.Zero)
                {
                    frameSourceDevice = info.pointerInfo.sourceDevice;
                }

                uint pid = info.pointerInfo.pointerId;
                uint flags = info.pointerInfo.pointerFlags;

                bool isUp = (flags & POINTER_FLAG_UP) != 0;
                bool isCanceled = (flags & POINTER_FLAG_CANCELED) != 0;
                bool isInContact = (flags & POINTER_FLAG_INCONTACT) != 0;
                bool isDown = (flags & POINTER_FLAG_DOWN) != 0;
                bool tipDown = !isUp && !isCanceled && (isInContact || isDown);

                // Ignore pure hover events for untracked pointers
                if (!tipDown && !isUp && !isCanceled)
                {
                    int hoverExisting = FindOrAllocateSlot(pid, allowAllocate: false);
                    if (hoverExisting < 0)
                    {
                        continue;
                    }
                }

                int slotIndex = FindOrAllocateSlot(pid, allowAllocate: tipDown);
                if (slotIndex < 0)
                {
                    continue;
                }

                seenSlots[slotIndex] = true;
                ref var slot = ref _slots[slotIndex];
                slot.SourceDevice = info.pointerInfo.sourceDevice;
                slot.TipSwitch = tipDown;
                slot.Confidence = !isCanceled;

                if (!isCanceled)
                {
                    POINT pt = info.pointerInfo.ptPixelLocation;
                    ScreenToClient(normHwnd, ref pt);
                    NormalizeCoordinates(pt, width, height, maxX, maxY, out slot.X, out slot.Y);
                }

                if (!tipDown)
                {
                    releaseAfterSend[slotIndex] = true;
                }
            }

            // Reconcile any previously active slot on the same digitizer absent from a full hardware frame
            if (fromFullFrame && frameSourceDevice != IntPtr.Zero)
            {
                for (int s = 0; s < HvProtocolConstants.MaxTouchContacts; s++)
                {
                    if (_slots[s].Occupied && !seenSlots[s] && _slots[s].SourceDevice == frameSourceDevice)
                    {
                        _slots[s].TipSwitch = false;
                        _slots[s].Confidence = true;
                        releaseAfterSend[s] = true;
                    }
                }
            }

            PackAndReleaseSlots(ref report, releaseAfterSend);
            shouldSend = report.ContactCount > 0;
        }

        if (shouldSend)
        {
            _touchTransport.SendStruct(HvPacketType.TouchFrame, in report);
        }
    }

    private void PackAndReleaseSlots(ref HvTouchInputReport report, ReadOnlySpan<bool> releaseAfterSend)
    {
        byte reportCount = 0;
        int touchDownCount = 0;

        for (int i = 0; i < HvProtocolConstants.MaxTouchContacts; i++)
        {
            if (_slots[i].Occupied)
            {
                var contact = HvTouchContact.Create(
                    _slots[i].ContactId,
                    _slots[i].X,
                    _slots[i].Y,
                    _slots[i].TipSwitch,
                    _slots[i].Confidence
                );
                report.SetContact(reportCount, in contact);
                reportCount++;

                if (_slots[i].TipSwitch)
                {
                    touchDownCount++;
                }

                if (releaseAfterSend[i])
                {
                    _slots[i] = default;
                }
            }
        }

        report.ContactCount = reportCount;
        Volatile.Write(ref _activeTouchCount, touchDownCount);
    }

    private static void NormalizeCoordinates(
        POINT pt,
        int width,
        int height,
        int maxX,
        int maxY,
        out ushort normX,
        out ushort normY)
    {
        int clampedX = Math.Clamp(pt.X, 0, maxX);
        int clampedY = Math.Clamp(pt.Y, 0, maxY);

        if (width >= 64)
        {
            if (clampedX <= EdgeSnapThresholdPx) clampedX = 0;
            else if (clampedX >= maxX - EdgeSnapThresholdPx) clampedX = maxX;
        }

        if (height >= 64)
        {
            if (clampedY <= EdgeSnapThresholdPx) clampedY = 0;
            else if (clampedY >= maxY - EdgeSnapThresholdPx) clampedY = maxY;
        }

        normX = (ushort)((clampedX * (long)HvProtocolConstants.TouchCoordinateMax) / maxX);
        normY = (ushort)((clampedY * (long)HvProtocolConstants.TouchCoordinateMax) / maxY);
    }

    private ushort ComputeScanTime()
    {
        // Scan Time in 100 microsecond (0.1 ms) units without Int64 multiplication overflow
        long ticks100Us = Stopwatch.GetElapsedTime(_qpcOrigin).Ticks / 1000L;
        return (ushort)(ticks100Us & 0xFFFF);
    }

    private int FindOrAllocateSlot(uint pointerId, bool allowAllocate)
    {
        for (int i = 0; i < HvProtocolConstants.MaxTouchContacts; i++)
        {
            if (_slots[i].Occupied && _slots[i].Win32PointerId == pointerId)
            {
                return i;
            }
        }

        if (!allowAllocate)
        {
            return -1;
        }

        for (int i = 0; i < HvProtocolConstants.MaxTouchContacts; i++)
        {
            if (!_slots[i].Occupied)
            {
                _slots[i].Occupied = true;
                _slots[i].Win32PointerId = pointerId;
                _slots[i].ContactId = (byte)i;
                _slots[i].Confidence = true;
                return i;
            }
        }

        return -1;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        List<IntPtr> hwndsSnapshot;
        lock (_stateLock)
        {
            hwndsSnapshot = new List<IntPtr>(_subclassedHwnds);
        }

        uint currentTid = GetCurrentThreadId();

        foreach (IntPtr hwnd in hwndsSnapshot)
        {
            if (!IsWindow(hwnd))
            {
                lock (_stateLock) { _subclassedHwnds.Remove(hwnd); }
                continue;
            }

            uint ownerTid = GetWindowThreadProcessId(hwnd, out _);
            if (ownerTid == 0 || ownerTid == currentTid)
            {
                ExecuteDetachOnOwningThread(hwnd);
            }
            else
            {
                bool hasHook;
                lock (_stateLock) { hasHook = _threadHooks.ContainsKey(ownerTid); }
                if (hasHook)
                {
                    DispatchRemoteThreadOp(hwnd, ownerTid, string.Empty, PendingOpKind.Detach, timeoutMs: 150);
                }
            }
        }

        List<IntPtr> hooksToRemove;
        lock (_stateLock)
        {
            hooksToRemove = new List<IntPtr>(_threadHooks.Values);
            _threadHooks.Clear();
            _pendingOpsByThread.Clear();
            Interlocked.Exchange(ref _pendingOpCount, 0);
            _subclassedHwnds.Clear();
        }

        foreach (IntPtr hHook in hooksToRemove)
        {
            try { UnhookWindowsHookEx(hHook); } catch { }
        }
    }
}
