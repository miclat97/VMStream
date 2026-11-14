// ============================================================================
// TouchWindowInterceptor.cs - IHWindowClass Subclassing & WM_POINTER Capture
// Intercepts raw multi-touch input directly on mstscax.dll's internal
// IHWindowClass viewport via SetWindowSubclass, normalizes coordinates to
// [0..32767], streams 64-byte HID frames over AF_HYPERV, and returns 0 so
// mstscax.dll never converts touch gestures into legacy mouse clicks.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using HvConsole.Host.Protocol;
using HvConsole.Host.Transport;

namespace HvConsole.Host.Input;

public sealed class TouchWindowInterceptor : IDisposable
{
    private const uint WM_POINTERUPDATE         = 0x0245;
    private const uint WM_POINTERDOWN           = 0x0246;
    private const uint WM_POINTERUP             = 0x0247;
    private const uint WM_POINTERENTER          = 0x0249;
    private const uint WM_POINTERLEAVE          = 0x024A;
    private const uint WM_POINTERCAPTURECHANGED = 0x024C;

    private const uint PT_TOUCH = 0x00000002;
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
    private int _activeTouchCount;
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
    /// Searches all child windows of the RDP ActiveX control for "IHWindowClass"
    /// (and fallback input hosts) and attaches the native WM_POINTER subclass.
    /// </summary>
    public int AttachToRdpControlHierarchy(IntPtr rdpRootHwnd)
    {
        if (rdpRootHwnd == IntPtr.Zero || _disposed) return 0;

        int newlyAttached = 0;
        var classBuilder = new StringBuilder(128);

        EnumChildWindows(rdpRootHwnd, (childHwnd, _) =>
        {
            classBuilder.Clear();
            if (GetClassNameW(childHwnd, classBuilder, classBuilder.Capacity) > 0)
            {
                string className = classBuilder.ToString();
                if (string.Equals(className, "IHWindowClass", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(className, "OPWindowClass", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(className, "UIMainClass", StringComparison.OrdinalIgnoreCase))
                {
                    if (!_subclassedHwnds.Contains(childHwnd))
                    {
                        if (SetWindowSubclass(childHwnd, _subclassDelegate, SubclassId, UIntPtr.Zero))
                        {
                            _subclassedHwnds.Add(childHwnd);
                            newlyAttached++;
                            Debug.WriteLine($"[TouchWindowInterceptor] Subclassed {className} (HWND=0x{childHwnd:X})");
                        }
                    }
                }
            }
            return true;
        }, IntPtr.Zero);

        // Also subclass the root ActiveX control HWND itself as a fallback
        if (!_subclassedHwnds.Contains(rdpRootHwnd))
        {
            if (SetWindowSubclass(rdpRootHwnd, _subclassDelegate, SubclassId, UIntPtr.Zero))
            {
                _subclassedHwnds.Add(rdpRootHwnd);
                newlyAttached++;
            }
        }

        return newlyAttached;
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
                    bool isDownOrUpdate = (uMsg == WM_POINTERDOWN || uMsg == WM_POINTERUPDATE);
                    HandleTouchPointerEvent(hWnd, pointerId, isDownOrUpdate);
                    // Return 0 so mstscax.dll NEVER sees the touch event and never emulates a mouse click!
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
                    return IntPtr.Zero;
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

        POINT pt = touchInfo.pointerInfo.ptPixelLocation;
        ScreenToClient(targetHwnd, ref pt);
        GetClientRect(targetHwnd, out RECT clientRect);

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
