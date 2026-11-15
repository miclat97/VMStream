// ============================================================================
// TouchWindowInterceptor.cs - Complete mstscax.dll Touch & Mouse-Promotion Blocker
// 1. Calls UnregisterTouchWindow on all mstscax.dll child windows so Windows
//    delivers pure Win8/10/11 WM_POINTER messages instead of legacy WM_TOUCH.
// 2. Subclasses all child windows inside RdpConsoleControl (IHWindowClass,
//    OPWindowClass, UIMainClass, UIContainerClass, etc.) via SetWindowSubclass.
// 3. Captures WM_POINTERDOWN / WM_POINTERUPDATE / WM_POINTERUP, normalizes
//    coordinates to [0..32767], and streams 64-byte HID frames over AF_HYPERV.
// 4. Swallows WM_POINTER*, WM_TOUCH, WM_GESTURE, and MI_WP_SIGNATURE synthesized
//    mouse messages so mstscax.dll NEVER emulates a mouse cursor on touch!
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
    private const uint WM_SETCURSOR             = 0x0020;
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
    private const uint PT_PEN   = 0x00000003;

    private const int MA_ACTIVATE = 1;

    // Microsoft signature for mouse messages synthesized from Touch / Pen:
    // (GetMessageExtraInfo() & 0xFFFFFF00) == 0xFF515700 (MI_WP_SIGNATURE)
    private const ulong MI_WP_SIGNATURE_MASK = 0xFFFFFF00UL;
    private const ulong MI_WP_SIGNATURE      = 0xFF515700UL;

    private static readonly UIntPtr SubclassId = new(0x564D5354); // 'VMST'

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetPointerType(uint pointerId, out uint pointerType);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetPointerTouchInfo(uint pointerId, out POINTER_TOUCH_INFO touchInfo);

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

    private struct ActiveContactSlot
    {
        public bool Occupied;
        public bool TipSwitch;
        public uint Win32PointerId;
        public byte ContactId;
        public ushort X;
        public ushort Y;
    }

    private readonly IHvTransport _touchTransport;
    private readonly SubclassProc _subclassDelegate;
    private readonly HashSet<IntPtr> _subclassedHwnds = new();
    private readonly ActiveContactSlot[] _slots = new ActiveContactSlot[HvProtocolConstants.MaxTouchContacts];
    private readonly object _touchLock = new();
    private IntPtr _rdpRootHwnd;
    private int _activeTouchCount;
    private long _lastTouchTickMs;
    private bool _disposed;

    public int ActiveTouchPoints => Volatile.Read(ref _activeTouchCount);

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
        // Keep delegate rooted for the lifetime of the interceptor
        _subclassDelegate = SubclassWndProc;
    }

    /// <summary>
    /// Recursively subclasses the RDP ActiveX control and ALL of its internal child windows
    /// (IHWindowClass, OPWindowClass, UIMainClass, UIContainerClass, ATL:*, etc.) and calls
    /// UnregisterTouchWindow on each HWND so mstscax.dll cannot use legacy WM_TOUCH mouse emulation.
    /// </summary>
    public int AttachToRdpControlHierarchy(IntPtr rdpRootHwnd)
    {
        if (rdpRootHwnd == IntPtr.Zero || _disposed) return 0;

        _rdpRootHwnd = rdpRootHwnd;
        int newlyAttached = 0;
        var classBuilder = new StringBuilder(128);

        // First attach to the root ActiveX HWND itself
        if (PrepareAndSubclassWindow(rdpRootHwnd, "AxRdpRoot"))
        {
            newlyAttached++;
        }

        // Attach to EVERY child window inside the mstscax.dll hierarchy
        EnumChildWindows(rdpRootHwnd, (childHwnd, _) =>
        {
            classBuilder.Clear();
            string className = GetClassNameW(childHwnd, classBuilder, classBuilder.Capacity) > 0
                ? classBuilder.ToString()
                : "UnknownChild";

            if (PrepareAndSubclassWindow(childHwnd, className))
            {
                newlyAttached++;
            }
            return true;
        }, IntPtr.Zero);

        return newlyAttached;
    }

    private bool PrepareAndSubclassWindow(IntPtr hwnd, string className)
    {
        if (hwnd == IntPtr.Zero) return false;

        // CRITICAL: mstscax.dll calls RegisterTouchWindow() on its internal child windows
        // (such as IHWindowClass), which switches the HWND from Win8+ WM_POINTER delivery
        // to legacy Win7 WM_TOUCH delivery (causing mouse cursor emulation).
        // Calling UnregisterTouchWindow() forces Windows back to delivering pure WM_POINTER*
        // events directly to our SubclassWndProc!
        try
        {
            if (IsTouchWindow(hwnd, out _))
            {
                UnregisterTouchWindow(hwnd);
            }
            else
            {
                UnregisterTouchWindow(hwnd);
            }
        }
        catch { }

        // Suppress host visual touch ripples / press-and-hold circles on the VM viewport
        DisableHostTouchVisualFeedback(hwnd);

        if (_subclassedHwnds.Contains(hwnd))
        {
            return false;
        }

        if (SetWindowSubclass(hwnd, _subclassDelegate, SubclassId, UIntPtr.Zero))
        {
            _subclassedHwnds.Add(hwnd);
            Debug.WriteLine($"[TouchWindowInterceptor] Subclassed {className} (HWND=0x{hwnd:X})");
            return true;
        }

        return false;
    }

    private static void DisableHostTouchVisualFeedback(IntPtr hwnd)
    {
        try
        {
            // FEEDBACK_TOUCH_CONTACTVISUALIZATION (1), FEEDBACK_TOUCH_TAP (7),
            // FEEDBACK_TOUCH_DOUBLETAP (8), FEEDBACK_TOUCH_PRESSANDHOLD (9), FEEDBACK_TOUCH_RIGHTTAP (10)
            int[] feedbackTypes = { 1, 7, 8, 9, 10 };
            foreach (int fb in feedbackTypes)
            {
                int disabled = 0;
                SetWindowFeedbackSetting(hwnd, fb, 0, sizeof(int), ref disabled);
            }
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
        // 1. Check Microsoft MI_WP_SIGNATURE on GetMessageExtraInfo()
        ulong extra = unchecked((ulong)GetMessageExtraInfo().ToInt64());
        if ((extra & MI_WP_SIGNATURE_MASK) == MI_WP_SIGNATURE)
        {
            return true;
        }

        // 2. Also suppress mouse synthesis while any finger is touching or within 180ms of lift
        if (Volatile.Read(ref _activeTouchCount) > 0)
        {
            return true;
        }

        long elapsedSinceTouch = Environment.TickCount64 - Interlocked.Read(ref _lastTouchTickMs);
        if (elapsedSinceTouch >= 0 && elapsedSinceTouch < 180)
        {
            return true;
        }

        return false;
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
            case WM_POINTERDOWN:
            case WM_POINTERUPDATE:
            case WM_POINTERUP:
            case WM_POINTERCAPTURECHANGED:
            {
                uint pointerId = (uint)(wParam.ToInt64() & 0xFFFF);
                if (GetPointerType(pointerId, out uint pointerType) && pointerType == PT_TOUCH)
                {
                    Interlocked.Exchange(ref _lastTouchTickMs, Environment.TickCount64);
                    bool isDownOrUpdate = (uMsg == WM_POINTERDOWN || uMsg == WM_POINTERUPDATE);
                    HandleTouchPointerEvent(hWnd, pointerId, isDownOrUpdate);
                    // Return 0 WITHOUT calling DefSubclassProc so neither mstscax.dll nor
                    // DefWindowProc ever sees the touch event or promotes it to a mouse click!
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
                    return new IntPtr(MA_ACTIVATE);
                }
                break;
            }

            case WM_TOUCH:
            {
                // If mstscax.dll re-registered legacy touch on this HWND, unregister it immediately
                Interlocked.Exchange(ref _lastTouchTickMs, Environment.TickCount64);
                if (lParam != IntPtr.Zero)
                {
                    try { CloseTouchInputHandle(lParam); } catch { }
                }
                try { UnregisterTouchWindow(hWnd); } catch { }
                return IntPtr.Zero;
            }

            case WM_GESTURE:
            case WM_GESTURENOTIFY:
            {
                if (IsTouchPromotedMouseMessage())
                {
                    return IntPtr.Zero;
                }
                break;
            }

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
            {
                // Swallow all touch-synthesized mouse events so mstscax.dll never moves or clicks the mouse cursor
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

    private void HandleTouchPointerEvent(IntPtr targetHwnd, uint pointerId, bool isTipDown)
    {
        if (!GetPointerTouchInfo(pointerId, out POINTER_TOUCH_INFO touchInfo))
        {
            return;
        }

        IntPtr normHwnd = (_rdpRootHwnd != IntPtr.Zero) ? _rdpRootHwnd : targetHwnd;
        POINT pt = touchInfo.pointerInfo.ptPixelLocation;
        ScreenToClient(normHwnd, ref pt);
        GetClientRect(normHwnd, out RECT clientRect);

        if (clientRect.Width <= 0 || clientRect.Height <= 0)
        {
            pt = touchInfo.pointerInfo.ptPixelLocation;
            ScreenToClient(targetHwnd, ref pt);
            GetClientRect(targetHwnd, out clientRect);
        }

        int width = Math.Max(1, clientRect.Width);
        int height = Math.Max(1, clientRect.Height);

        int clampedX = Math.Clamp(pt.X, 0, width);
        int clampedY = Math.Clamp(pt.Y, 0, height);

        ushort normX = (ushort)((clampedX * (long)HvProtocolConstants.TouchCoordinateMax) / width);
        ushort normY = (ushort)((clampedY * (long)HvProtocolConstants.TouchCoordinateMax) / height);

        HvTouchInputReport report = default;
        report.ReportId = HvProtocolConstants.TouchReportIdInput;
        // Scan Time in 100 microsecond (0.1 ms) units required by Windows 11 DirectManipulation
        report.ScanTime = (ushort)((Stopwatch.GetTimestamp() * 10000L / Stopwatch.Frequency) & 0xFFFF);

        lock (_touchLock)
        {
            int slotIndex = FindOrAllocateSlot(pointerId, isTipDown);
            if (slotIndex < 0)
            {
                return;
            }

            ref var slot = ref _slots[slotIndex];
            slot.TipSwitch = isTipDown;
            slot.X = normX;
            slot.Y = normY;

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
                        confidence: true
                    );
                    report.SetContact(reportCount, in contact);
                    reportCount++;

                    if (_slots[i].TipSwitch)
                    {
                        touchDownCount++;
                    }
                }
            }

            report.ContactCount = reportCount;
            Volatile.Write(ref _activeTouchCount, touchDownCount);

            // If this contact just lifted (WM_POINTERUP), release its slot AFTER including
            // the final TipSwitch=0 report so Windows 11 registers the finger lift cleanly.
            if (!isTipDown)
            {
                slot = default;
            }
        }

        _touchTransport.SendStruct(HvPacketType.TouchFrame, in report);
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
                return i;
            }
        }

        return -1;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (IntPtr hwnd in _subclassedHwnds)
        {
            try
            {
                RemoveWindowSubclass(hwnd, _subclassDelegate, SubclassId);
            }
            catch { }
        }
        _subclassedHwnds.Clear();
    }
}
