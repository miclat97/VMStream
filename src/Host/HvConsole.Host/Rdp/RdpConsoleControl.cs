// ============================================================================
// RdpConsoleControl.cs - Native mstscax.dll ActiveX Host (VMConnect Parity)
// Replicates Microsoft VMConnect (RdpClient / RdpViewerControl) ActiveX setup
// strictly in Hyper-V Basic Console Session mode (localhost:2179, clean PCB
// GUID without braces or ;EnhancedMode=1, ZoomLevel + pixel-exact sizing,
// default NetworkConnectionType unset) while keeping VMStream's
// AF_HYPERV touch/audio isolation and LowLevelKeyboardHook compatibility.
// ============================================================================
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HvConsole.Host.Rdp;

/// <summary>
/// RDP console zoom percentages matching VMConnect (ZoomLevel.cs).
/// ZoomLevel.Auto computes native DPI scaling (DeviceDpi / 96.0 * 100.0).
/// </summary>
public enum ZoomLevel : uint
{
    Auto = 0U,
    P25  = 25U,
    P50  = 50U,
    P75  = 75U,
    P100 = 100U,
    P125 = 125U,
    P150 = 150U,
    P200 = 200U
}

[ComImport]
[Guid("00000113-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleInPlaceObject
{
    void GetWindow(out IntPtr phwnd);
    void ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)][In] bool fEnterMode);
    void InPlaceDeactivate();
    void UIDeactivate();
    void SetObjectRects(IntPtr lprcPosRect, IntPtr lprcClipRect);
    void ReactivateAndUndo();
}

/// <summary>
/// COM Event Sink for IMsTscAxEvents ({336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6}).
/// DispIds match MSTSCLib-x64.idl verbatim.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
[ComDefaultInterface(typeof(IMsTscAxEventsSink))]
public sealed class RdpActiveXEventSink : IMsTscAxEventsSink
{
    private readonly RdpConsoleControl _owner;

    public RdpActiveXEventSink(RdpConsoleControl owner)
    {
        _owner = owner;
    }

    [DispId(1)] public void OnConnecting() => _owner.RaiseOnConnecting();
    [DispId(2)] public void OnConnected() => _owner.RaiseOnConnected();
    [DispId(3)] public void OnLoginComplete() => _owner.RaiseOnLoginComplete();
    [DispId(4)] public void OnDisconnected(int discReason) => _owner.RaiseOnDisconnected(discReason);
    [DispId(5)] public void OnEnterFullScreenMode() { }
    [DispId(6)] public void OnLeaveFullScreenMode() { }
    [DispId(7)] public void OnChannelReceivedData(string chanName, string data) { }
    [DispId(8)] public void OnRequestGoFullScreen() => _owner.RaiseOnRequestGoFullScreen();
    [DispId(9)] public void OnRequestLeaveFullScreen() => _owner.RaiseOnRequestLeaveFullScreen();
    [DispId(10)] public void OnFatalError(int errorCode) => _owner.RaiseOnFatalError(errorCode);
    [DispId(11)] public void OnWarning(int warningCode) { }
    [DispId(12)] public void OnRemoteDesktopSizeChange(int width, int height) => _owner.RaiseOnRemoteDesktopSizeChange(width, height);
    [DispId(14)] public void OnRequestContainerMinimize() => _owner.RaiseOnRequestContainerMinimize();

    [DispId(15)]
    public void OnConfirmClose([MarshalAs(UnmanagedType.VariantBool)] out bool pfAllowClose)
    {
        pfAllowClose = true;
        _owner.RaiseOnConfirmClose();
    }

    [DispId(23)] public void OnFocusReleased(int iDirection) => _owner.RaiseOnFocusReleased(iDirection);

    [DispId(26)]
    public void OnMouseInputModeChanged([MarshalAs(UnmanagedType.VariantBool)] bool fMouseModeRelative)
        => _owner.RaiseOnMouseInputModeChanged(fMouseModeRelative);

    [DispId(32)]
    public void OnNetworkStatusChanged(uint qualityLevel, int bandwidth, int rtt)
        => _owner.RaiseOnNetworkStatusChanged(qualityLevel, bandwidth, rtt);
}

[ComImport]
[Guid("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface IMsTscAxEventsSink
{
    [DispId(1)] void OnConnecting();
    [DispId(2)] void OnConnected();
    [DispId(3)] void OnLoginComplete();
    [DispId(4)] void OnDisconnected(int discReason);
    [DispId(5)] void OnEnterFullScreenMode();
    [DispId(6)] void OnLeaveFullScreenMode();
    [DispId(7)] void OnChannelReceivedData([MarshalAs(UnmanagedType.BStr)] string chanName, [MarshalAs(UnmanagedType.BStr)] string data);
    [DispId(8)] void OnRequestGoFullScreen();
    [DispId(9)] void OnRequestLeaveFullScreen();
    [DispId(10)] void OnFatalError(int errorCode);
    [DispId(11)] void OnWarning(int warningCode);
    [DispId(12)] void OnRemoteDesktopSizeChange(int width, int height);
    [DispId(14)] void OnRequestContainerMinimize();
    [DispId(15)] void OnConfirmClose([MarshalAs(UnmanagedType.VariantBool)] out bool pfAllowClose);
    [DispId(23)] void OnFocusReleased(int iDirection);
    [DispId(26)] void OnMouseInputModeChanged([MarshalAs(UnmanagedType.VariantBool)] bool fMouseModeRelative);
    [DispId(32)] void OnNetworkStatusChanged(uint qualityLevel, int bandwidth, int rtt);
}

/// <summary>
/// Direct COM interface for IMsRdpExtendedSettings ({302D8188-0052-4807-806A-362B628F9AC5})
/// used for "DisableCredentialsDelegation", "EnableFrameBufferRedirection",
/// "AllowAxToContainerEvents", "DisableTouchRemoting", and runtime "ZoomLevel" (VT_UI4).
/// </summary>
[ComImport]
[Guid("302D8188-0052-4807-806A-362B628F9AC5")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMsRdpExtendedSettings
{
    [PreserveSig]
    int put_Property(
        [MarshalAs(UnmanagedType.BStr)] string bstrPropertyName,
        [In][MarshalAs(UnmanagedType.Struct)] ref object pValue);

    [PreserveSig]
    int get_Property(
        [MarshalAs(UnmanagedType.BStr)] string bstrPropertyName,
        [Out][MarshalAs(UnmanagedType.Struct)] out object? pValue);
}

/// <summary>
/// Direct COM interface for IMsRdpClientNonScriptable3 ({B3378D90-0728-45C7-8ED7-B6159FB92219}).
/// Declares the complete vtable through ConnectionBarText matching MSTSCLib-x64.idl.
/// </summary>
[ComImport]
[Guid("B3378D90-0728-45C7-8ED7-B6159FB92219")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMsRdpClientNonScriptable3
{
    // IMsTscNonScriptable
    void put_ClearTextPassword([MarshalAs(UnmanagedType.BStr)] string password);
    void put_PortablePassword([MarshalAs(UnmanagedType.BStr)] string portablePass);
    [return: MarshalAs(UnmanagedType.BStr)] string get_PortablePassword();
    void put_PortableSalt([MarshalAs(UnmanagedType.BStr)] string portableSalt);
    [return: MarshalAs(UnmanagedType.BStr)] string get_PortableSalt();
    void put_BinaryPassword([MarshalAs(UnmanagedType.BStr)] string binaryPassword);
    [return: MarshalAs(UnmanagedType.BStr)] string get_BinaryPassword();
    void put_BinarySalt([MarshalAs(UnmanagedType.BStr)] string binarySalt);
    [return: MarshalAs(UnmanagedType.BStr)] string get_BinarySalt();
    void ResetPassword();

    // IMsRdpClientNonScriptable
    void NotifyRedirectDeviceChange(UIntPtr wParam, IntPtr lParam);
    void SendKeys(int numKeys, [In] ref short pbArrayKeyUp, [In] ref int plKeyData);

    // IMsRdpClientNonScriptable2
    void put_UIParentWindowHandle(IntPtr hwndUIParentWindowHandle);
    void get_UIParentWindowHandle(out IntPtr phwndUIParentWindowHandle);

    // IMsRdpClientNonScriptable3
    void put_ShowRedirectionWarningDialog([MarshalAs(UnmanagedType.VariantBool)] bool fShowRdrDlg);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_ShowRedirectionWarningDialog();
    void put_PromptForCredentials([MarshalAs(UnmanagedType.VariantBool)] bool fPrompt);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_PromptForCredentials();
    void put_NegotiateSecurityLayer([MarshalAs(UnmanagedType.VariantBool)] bool fNegotiate);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_NegotiateSecurityLayer();
    void put_EnableCredSspSupport([MarshalAs(UnmanagedType.VariantBool)] bool fEnableSupport);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_EnableCredSspSupport();
    void put_RedirectDynamicDrives([MarshalAs(UnmanagedType.VariantBool)] bool fRedirectDynamicDrives);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_RedirectDynamicDrives();
    void put_RedirectDynamicDevices([MarshalAs(UnmanagedType.VariantBool)] bool fRedirectDynamicDevices);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_RedirectDynamicDevices();
    [return: MarshalAs(UnmanagedType.Interface)] object get_DeviceCollection();
    [return: MarshalAs(UnmanagedType.Interface)] object get_DriveCollection();
    void put_WarnAboutSendingCredentials([MarshalAs(UnmanagedType.VariantBool)] bool fWarn);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_WarnAboutSendingCredentials();
    void put_WarnAboutClipboardRedirection([MarshalAs(UnmanagedType.VariantBool)] bool fWarn);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_WarnAboutClipboardRedirection();
    void put_ConnectionBarText([MarshalAs(UnmanagedType.BStr)] string connectionBarText);
    [return: MarshalAs(UnmanagedType.BStr)] string get_ConnectionBarText();
}

/// <summary>
/// Typed IDispatch interface for IMsRdpClient7 ({B2A5B5CE-3461-444A-91D4-ADD26D070638}).
/// </summary>
[ComImport]
[Guid("B2A5B5CE-3461-444A-91D4-ADD26D070638")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface IMsRdpClientDisp
{
    [DispId(1)] string Server { [param: MarshalAs(UnmanagedType.BStr)] set; [return: MarshalAs(UnmanagedType.BStr)] get; }
    [DispId(6)] short Connected { get; }
    [DispId(12)] int DesktopWidth { set; get; }
    [DispId(13)] int DesktopHeight { set; get; }
    [DispId(30)] void Connect();
    [DispId(31)] void Disconnect();
    [DispId(102)] IMsRdpClientSecuredSettingsDisp SecuredSettings2 { get; }
    [DispId(104)] bool FullScreen { [param: MarshalAs(UnmanagedType.VariantBool)] set; [return: MarshalAs(UnmanagedType.VariantBool)] get; }
    [DispId(600)] IMsRdpClientAdvSettingsDisp AdvancedSettings8 { get; }
}

/// <summary>
/// Typed IDispatch interface for IMsRdpClientAdvancedSettings7 ({26036036-4010-4578-8091-0DB9A1EDF9C3}),
/// ensuring exact VARIANT types (VT_UI4, VT_BOOL, VT_I4, VT_BSTR) for NetworkConnectionType,
/// RelativeMouseMode, PCB, AuthenticationServiceClass, etc.
/// </summary>
[ComImport]
[Guid("26036036-4010-4578-8091-0DB9A1EDF9C3")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface IMsRdpClientAdvSettingsDisp
{
    [DispId(108)] int RDPPort { set; get; }
    [DispId(157)] int minInputSendInterval { set; get; }
    [DispId(173)] int ContainerHandledFullScreen { set; get; }
    [DispId(187)] bool DisplayConnectionBar { [param: MarshalAs(UnmanagedType.VariantBool)] set; [return: MarshalAs(UnmanagedType.VariantBool)] get; }
    [DispId(189)] bool GrabFocusOnConnect { [param: MarshalAs(UnmanagedType.VariantBool)] set; [return: MarshalAs(UnmanagedType.VariantBool)] get; }
    [DispId(191)] bool RedirectDrives { [param: MarshalAs(UnmanagedType.VariantBool)] set; [return: MarshalAs(UnmanagedType.VariantBool)] get; }
    [DispId(192)] bool RedirectPrinters { [param: MarshalAs(UnmanagedType.VariantBool)] set; [return: MarshalAs(UnmanagedType.VariantBool)] get; }
    [DispId(193)] bool RedirectPorts { [param: MarshalAs(UnmanagedType.VariantBool)] set; [return: MarshalAs(UnmanagedType.VariantBool)] get; }
    [DispId(194)] bool RedirectSmartCards { [param: MarshalAs(UnmanagedType.VariantBool)] set; [return: MarshalAs(UnmanagedType.VariantBool)] get; }
    [DispId(206)] bool EnableAutoReconnect { [param: MarshalAs(UnmanagedType.VariantBool)] set; [return: MarshalAs(UnmanagedType.VariantBool)] get; }
    [DispId(210)] bool ConnectionBarShowMinimizeButton { [param: MarshalAs(UnmanagedType.VariantBool)] set; [return: MarshalAs(UnmanagedType.VariantBool)] get; }
    [DispId(211)] bool ConnectionBarShowRestoreButton { [param: MarshalAs(UnmanagedType.VariantBool)] set; [return: MarshalAs(UnmanagedType.VariantBool)] get; }
    [DispId(212)] uint AuthenticationLevel { set; get; }
    [DispId(215)] uint AudioRedirectionMode { set; get; }
    [DispId(216)] bool ConnectionBarShowPinButton { [param: MarshalAs(UnmanagedType.VariantBool)] set; [return: MarshalAs(UnmanagedType.VariantBool)] get; }
    [DispId(219)] bool RedirectPOSDevices { [param: MarshalAs(UnmanagedType.VariantBool)] set; [return: MarshalAs(UnmanagedType.VariantBool)] get; }
    [DispId(221)] bool RelativeMouseMode { [param: MarshalAs(UnmanagedType.VariantBool)] set; [return: MarshalAs(UnmanagedType.VariantBool)] get; }
    [DispId(222)] string AuthenticationServiceClass { [return: MarshalAs(UnmanagedType.BStr)] get; [param: MarshalAs(UnmanagedType.BStr)] set; }
    [DispId(223)] string PCB { [return: MarshalAs(UnmanagedType.BStr)] get; [param: MarshalAs(UnmanagedType.BStr)] set; }
    [DispId(228)] bool AudioCaptureRedirectionMode { [param: MarshalAs(UnmanagedType.VariantBool)] set; [return: MarshalAs(UnmanagedType.VariantBool)] get; }
    [DispId(235)] uint NetworkConnectionType { set; get; }
}

/// <summary>
/// Typed IDispatch interface for IMsRdpClientSecuredSettings ({605BEFCF-39C1-45CC-A811-068FB7BE346D}).
/// </summary>
[ComImport]
[Guid("605BEFCF-39C1-45CC-A811-068FB7BE346D")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface IMsRdpClientSecuredSettingsDisp
{
    [DispId(4)] int KeyboardHookMode { set; get; }
    [DispId(5)] int AudioRedirectionMode { set; get; }
}

/// <summary>
/// Hosts the native Windows mstscax.dll ActiveX Control for Hyper-V Basic Console Sessions
/// matching Microsoft VMConnect (RdpClient + RdpViewerControl) behavior.
/// </summary>
[DesignerCategory("Code")]
public sealed class RdpConsoleControl : AxHost
{
    // MsRdpClient9NotSafeForScripting:  {8B918B82-7985-4C24-89DF-C33AD2BBFBCD} (used by VMConnect)
    // MsRdpClient10NotSafeForScripting: {A0C63C30-F08D-4AB4-907C-34905D770C7D}
    // MsRdpClient8NotSafeForScripting:  {A3BC03A0-041D-42E3-AD22-882B7865C9C5}
    private const string ClsidMsRdpClient9NotSafe  = "8b918b82-7985-4c24-89df-c33ad2bbfbcd";
    private const string ClsidMsRdpClient10NotSafe = "a0c63c30-f08d-4ab4-907c-34905d770c7d";
    private const string ClsidMsRdpClient8NotSafe  = "a3bc03a0-041d-42e3-ad22-882b7865c9c5";

    private const uint GA_ROOT = 2;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int WM_SYSCHAR = 0x0106;
    private const int VK_SPACE = 32;

    private const int VMCONNECT_MIN_INPUT_SEND_INTERVAL = 20;

    public static readonly Size BiosResolution = new(640, 400);
    public static readonly Size DisabledVideoResolution = new(240, 240);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    private object? _ocx;
    private ConnectionPointCookie? _eventCookie;
    private RdpActiveXEventSink? _eventSink;

    public event Action? Connecting;
    public event Action? Connected;
    public event Action? LoginCompleted;
    public event Action<int>? Disconnected;
    public event Action<int>? FatalError;
    public event Action<int, int>? RemoteDesktopSizeChanged;
    public event Action? RequestEnterFullScreen;
    public event Action? RequestExitFullScreen;
    public event Action? RequestContainerMinimize;
    public event Action? RequestClose;
    public event Action<int>? FocusReleased;
    public event Action? AltSpaceReleased;
    public event EventHandler? MouseActivate;
    public event Action<bool>? MouseInputModeChanged;
    public event Action<uint, int, int>? NetworkStatusChanged;

    public int RemoteDesktopWidth { get; private set; } = BiosResolution.Width;
    public int RemoteDesktopHeight { get; private set; } = BiosResolution.Height;
    public bool IsConnected { get; private set; }
    public bool HasActiveVideo { get; private set; } = true;
    public bool IsMouseModeRelative { get; private set; }
    public ZoomLevel CurrentZoomLevel { get; private set; } = ZoomLevel.P100;
    public uint LastNetworkQualityLevel { get; private set; }
    public int LastBandwidthKbps { get; private set; }
    public int LastRoundTripTimeMs { get; private set; }

    public Size DesktopResolution => new(RemoteDesktopWidth, RemoteDesktopHeight);

    public Size ScaledDesktopResolution => new(
        (int)((double)(RemoteDesktopWidth * (int)CurrentZoomLevel) / 100.0),
        (int)((double)(RemoteDesktopHeight * (int)CurrentZoomLevel) / 100.0)
    );

    public RdpConsoleControl()
        : base(SelectAvailableRdpClsid())
    {
        // Matches VMConnect RdpClient constructor: enable OnNotifyMessage for WM_MOUSEACTIVATE
        SetStyle(ControlStyles.EnableNotifyMessage, true);
        Dock = DockStyle.None;
        Location = Point.Empty;
        Size = BiosResolution;
    }

    private static string SelectAvailableRdpClsid()
    {
        if (Type.GetTypeFromCLSID(new Guid(ClsidMsRdpClient9NotSafe)) != null)
        {
            return ClsidMsRdpClient9NotSafe;
        }
        if (Type.GetTypeFromCLSID(new Guid(ClsidMsRdpClient10NotSafe)) != null)
        {
            return ClsidMsRdpClient10NotSafe;
        }
        return ClsidMsRdpClient8NotSafe;
    }

    protected override void AttachInterfaces()
    {
        base.AttachInterfaces();
        _ocx = GetOcx();
    }

    protected override void CreateSink()
    {
        base.CreateSink();
        _ocx ??= GetOcx();
        if (_ocx != null)
        {
            try
            {
                _eventSink = new RdpActiveXEventSink(this);
                _eventCookie = new ConnectionPointCookie(_ocx, _eventSink, typeof(IMsTscAxEventsSink));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RdpConsoleControl] CreateSink warning: {ex.Message}");
            }
        }
    }

    protected override void DetachSink()
    {
        try
        {
            _eventCookie?.Disconnect();
            _eventCookie = null;
            _eventSink = null;
        }
        catch
        {
        }
        base.DetachSink();
    }

    protected override void OnNotifyMessage(Message m)
    {
        base.OnNotifyMessage(m);
        if (m.Msg == WM_MOUSEACTIVATE)
        {
            MouseActivate?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void WndProc(ref Message m)
    {
        // Matches VMConnect RdpViewerControl.WndProc: Alt+Space deactivates ActiveX in-place
        // UI focus cleanly so the host system menu/window focus isn't swallowed by mstscax.
        if (m.Msg == WM_SYSCHAR && (int)m.WParam == VK_SPACE)
        {
            if (IsHandleCreated)
            {
                BeginInvoke(() => AltSpaceReleased?.Invoke());
            }
            Deactivate();
        }
        base.WndProc(ref m);
    }

    /// <summary>
    /// Computes VMConnect's ZoomLevel.Auto value from the current monitor DPI:
    /// (int)((double)DeviceDpi / 96.0 * 100.0).
    /// </summary>
    public ZoomLevel ComputeAutoZoomLevel()
    {
        int dpi = DeviceDpi > 0 ? DeviceDpi : 96;
        int pct = Math.Max(25, (int)((double)dpi / 96.0 * 100.0));
        return (ZoomLevel)(uint)pct;
    }

    /// <summary>
    /// Applies IMsRdpExtendedSettings["ZoomLevel"] (VT_UI4) and resizes the ActiveX control
    /// identically to VMConnect RdpViewerControl.SetZoomLevel, without SmartSizing blur.
    /// </summary>
    public void SetZoomLevel(ZoomLevel level, Size viewportSize, bool isFullScreen = false)
    {
        ZoomLevel target = level == ZoomLevel.Auto ? ComputeAutoZoomLevel() : level;

        _ocx ??= GetOcx();
        if (IsConnected && CurrentZoomLevel != target && _ocx is IMsRdpExtendedSettings extSettings)
        {
            try
            {
                object zoomVal = (uint)target;
                int hr = extSettings.put_Property("ZoomLevel", ref zoomVal);
                if (hr >= 0)
                {
                    CurrentZoomLevel = target;
                }
                else
                {
                    Debug.WriteLine($"[RdpConsoleControl] put_Property(ZoomLevel={(uint)target}) returned 0x{hr:X8}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RdpConsoleControl] SetZoomLevel({(uint)target}) ignored: {ex.Message}");
            }
        }

        ApplyViewportLayout(viewportSize, isFullScreen);
    }

    /// <summary>
    /// Positions and sizes the ActiveX host cleanly to exact native/scaled pixels inside the
    /// dark viewport container, centering in fullscreen (and whenever guest < viewport).
    /// Matches VMConnect RdpViewerControl (Dock = None, no SmartSizing stretching).
    /// </summary>
    public void ApplyViewportLayout(Size viewportSize, bool isFullScreen)
    {
        if (viewportSize.Width <= 0 || viewportSize.Height <= 0)
        {
            return;
        }

        Size targetExtent = isFullScreen ? DesktopResolution : ScaledDesktopResolution;
        if (targetExtent.Width <= 0 || targetExtent.Height <= 0)
        {
            targetExtent = BiosResolution;
        }

        Size newSize = new(
            Math.Min(viewportSize.Width, targetExtent.Width),
            Math.Min(viewportSize.Height, targetExtent.Height)
        );

        // In fullscreen (like VMConnect) or when viewport is larger than guest resolution,
        // center the unstretched frame inside the black canvas; otherwise (0, 0) fills exact client rect.
        Point newLocation = isFullScreen
            ? new Point(
                Math.Max((viewportSize.Width - DesktopResolution.Width) / 2, 0),
                Math.Max((viewportSize.Height - DesktopResolution.Height) / 2, 0))
            : new Point(
                Math.Max((viewportSize.Width - newSize.Width) / 2, 0),
                Math.Max((viewportSize.Height - newSize.Height) / 2, 0));

        if (Dock != DockStyle.None)
        {
            Dock = DockStyle.None;
        }
        if (Size != newSize)
        {
            Size = newSize;
        }
        if (Location != newLocation)
        {
            Location = newLocation;
        }
    }

    /// <summary>
    /// Configures and connects identically to VMConnect's RdpViewerControl (CreateRdpClient +
    /// ConnectCallback) in Basic Console Session mode on localhost:2179, while keeping
    /// VMStream's AF_HYPERV touch/audio isolation.
    /// </summary>
    public void ConnectToHyperVConsole(Guid vmGuid, string? vmName = null, bool disableTouchRemoting = true)
    {
        if (!IsHandleCreated)
        {
            CreateControl();
        }

        _ocx ??= GetOcx();
        if (_ocx == null)
        {
            throw new InvalidOperationException("Underlying mstscax.dll ActiveX control is not initialized.");
        }

        IsConnected = false;
        HasActiveVideo = true;
        IsMouseModeRelative = false;
        RemoteDesktopWidth = BiosResolution.Width;
        RemoteDesktopHeight = BiosResolution.Height;
        Dock = DockStyle.None;
        Location = Point.Empty;

        EnsureLocalCredSspPolicyDefaults();

        // PCB must be a clean 36-char GUID string WITHOUT curly braces or ";EnhancedMode=1"
        // so vmms.exe on port 2179 opens a strict Basic Console Session.
        string cleanPcbGuid = vmGuid.ToString("D").ToUpperInvariant();
        string displayVmName = string.IsNullOrWhiteSpace(vmName) ? cleanPcbGuid : vmName;

        var clientDisp = _ocx as IMsRdpClientDisp;
        IMsRdpClientAdvSettingsDisp? advDisp = null;
        try
        {
            advDisp = clientDisp?.AdvancedSettings8;
        }
        catch
        {
        }

        // Fallback late-bound objects only if typed IDispatch QI isn't supported on an older host
        object? fallbackAdv = advDisp == null
            ? (GetComProperty(_ocx, "AdvancedSettings8") ??
               GetComProperty(_ocx, "AdvancedSettings7") ??
               GetComProperty(_ocx, "AdvancedSettings6") ??
               GetComProperty(_ocx, "AdvancedSettings2") ??
               GetComProperty(_ocx, "AdvancedSettings"))
            : null;

        // --------------------------------------------------------------------
        // Phase 1: RdpViewerControl.CreateRdpClient parity
        // Intentionally leaves ColorDepth, Compress, BitmapPeristence, SmartSizing,
        // PerformanceFlags, BandwidthDetection, NetworkConnectionType, EnableHardwareMode,
        // DesktopScaleFactor, and DeviceScaleFactor at their mstscax Basic Console defaults!
        // (VMConnect only sets NetworkConnectionType = 6 when EnhancedSessionTransportType == HvSocket.)
        // --------------------------------------------------------------------
        SetAdvProperty(advDisp, fallbackAdv, a => a.EnableAutoReconnect = false, "EnableAutoReconnect", false);
        SetAdvProperty(advDisp, fallbackAdv, a => a.ContainerHandledFullScreen = 1, "ContainerHandledFullScreen", 1);
        SetAdvProperty(advDisp, fallbackAdv, a => a.RelativeMouseMode = true, "RelativeMouseMode", true);
        SetAdvProperty(
            advDisp,
            fallbackAdv,
            a => a.AuthenticationServiceClass = "Microsoft Virtual Console Service",
            "AuthenticationServiceClass",
            "Microsoft Virtual Console Service");

        // Parent security/CredSSP prompts cleanly & set ConnectionBarText ("{0} on {1}")
        if (_ocx is IMsRdpClientNonScriptable3 nonScriptable)
        {
            try
            {
                IntPtr rootHwnd = GetAncestor(Handle, GA_ROOT);
                if (rootHwnd == IntPtr.Zero) rootHwnd = Handle;
                nonScriptable.put_UIParentWindowHandle(rootHwnd);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RdpConsoleControl] put_UIParentWindowHandle ignored: {ex.Message}");
            }

            try
            {
                string barText = string.Format(
                    CultureInfo.CurrentCulture,
                    "{0} on {1}",
                    displayVmName,
                    Environment.MachineName);
                nonScriptable.put_ConnectionBarText(barText);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RdpConsoleControl] put_ConnectionBarText ignored: {ex.Message}");
            }
        }

        // VMConnect SetSecureModeOn()
        SetAdvProperty(advDisp, fallbackAdv, a => a.AuthenticationLevel = 0U, "AuthenticationLevel", 0U);
        if (_ocx is IMsRdpClientNonScriptable3 nonScriptableAuth)
        {
            // EnableCredSspSupport(true) MUST precede NegotiateSecurityLayer(false)
            try { nonScriptableAuth.put_EnableCredSspSupport(true); } catch { }
            try { nonScriptableAuth.put_NegotiateSecurityLayer(false); } catch { }
        }

        if (_ocx is IMsRdpExtendedSettings extSettings)
        {
            TrySetExtendedProperty(extSettings, "DisableCredentialsDelegation", true);

            // VMConnect EnableFrameBufferRedirection(): checks HKLM registry opt-out
            if (ShouldEnableFrameBufferRedirection())
            {
                TrySetExtendedProperty(extSettings, "EnableFrameBufferRedirection", true);
            }

            // VMStream isolation: touch is handled over VM-isolated AF_HYPERV HID bridge when enabled
            TrySetExtendedProperty(extSettings, "DisableTouchRemoting", disableTouchRemoting);
        }

        SetAdvProperty(advDisp, fallbackAdv, a => a.GrabFocusOnConnect = false, "GrabFocusOnConnect", false);

        // KeyboardHookMode = 1 forwards Windows shortcuts (Win, Alt+Tab, etc.) while focused
        // (our LowLevelKeyboardHook calls BringToFront so Ctrl+Alt+Shift+* hotkeys fire first),
        // and AudioRedirectionMode = 2 disables RDP audio since VMStream uses AF_HYPERV WaveRT.
        IMsRdpClientSecuredSettingsDisp? secDisp = null;
        try
        {
            secDisp = clientDisp?.SecuredSettings2;
        }
        catch
        {
        }

        if (secDisp != null)
        {
            SafeComAction(() => secDisp.KeyboardHookMode = 1, "SecuredSettings2.KeyboardHookMode");
            SafeComAction(() => secDisp.AudioRedirectionMode = 2, "SecuredSettings2.AudioRedirectionMode");
        }
        else
        {
            object? fallbackSec =
                GetComProperty(_ocx, "SecuredSettings3") ??
                GetComProperty(_ocx, "SecuredSettings2") ??
                GetComProperty(_ocx, "SecuredSettings");
            if (fallbackSec != null)
            {
                SetComProperty(fallbackSec, "KeyboardHookMode", 1);
                SetComProperty(fallbackSec, "AudioRedirectionMode", 2);
            }
        }

        // VMConnect RdpViewerControl.CreateRdpClient parity: enable AX-to-container events
        // (required on Windows 11 22H2+ where this defaults to false so focus-release events propagate)
        if (_ocx is IMsRdpExtendedSettings extContainerEvents)
        {
            TrySetExtendedProperty(extContainerEvents, "AllowAxToContainerEvents", true);
        }

        // Keep VMStream distraction-free fullscreen bar hidden by default (still reachable via Ctrl+Alt+Home)
        // and disable all non-console RDP redirections (audio/drives/printers/smartcards/ports/POS).
        SetAdvProperty(advDisp, fallbackAdv, a => a.DisplayConnectionBar = false, "DisplayConnectionBar", false);
        SetAdvProperty(advDisp, fallbackAdv, a => a.ConnectionBarShowMinimizeButton = false, "ConnectionBarShowMinimizeButton", false);
        SetAdvProperty(advDisp, fallbackAdv, a => a.ConnectionBarShowRestoreButton = false, "ConnectionBarShowRestoreButton", false);
        SetAdvProperty(advDisp, fallbackAdv, a => a.ConnectionBarShowPinButton = false, "ConnectionBarShowPinButton", false);

        SetAdvProperty(advDisp, fallbackAdv, a => a.AudioRedirectionMode = 2U, "AudioRedirectionMode", 2U);
        SetAdvProperty(advDisp, fallbackAdv, a => a.AudioCaptureRedirectionMode = false, "AudioCaptureRedirectionMode", false);
        SetAdvProperty(advDisp, fallbackAdv, a => a.RedirectDrives = false, "RedirectDrives", false);
        SetAdvProperty(advDisp, fallbackAdv, a => a.RedirectPrinters = false, "RedirectPrinters", false);
        SetAdvProperty(advDisp, fallbackAdv, a => a.RedirectSmartCards = false, "RedirectSmartCards", false);
        SetAdvProperty(advDisp, fallbackAdv, a => a.RedirectPorts = false, "RedirectPorts", false);
        SetAdvProperty(advDisp, fallbackAdv, a => a.RedirectPOSDevices = false, "RedirectPOSDevices", false);

        // --------------------------------------------------------------------
        // Phase 2: RdpViewerControl.ConnectCallback parity (exact property order before Connect())
        // --------------------------------------------------------------------
        CurrentZoomLevel = ZoomLevel.P100;

        if (clientDisp != null)
        {
            clientDisp.Server = "localhost";
        }
        else
        {
            SetComProperty(_ocx, "Server", "localhost");
        }

        SetAdvProperty(advDisp, fallbackAdv, a => a.RDPPort = 2179, "RDPPort", 2179);
        SetAdvProperty(advDisp, fallbackAdv, a => a.minInputSendInterval = VMCONNECT_MIN_INPUT_SEND_INTERVAL, "minInputSendInterval", VMCONNECT_MIN_INPUT_SEND_INTERVAL);
        SetAdvProperty(advDisp, fallbackAdv, a => a.PCB = cleanPcbGuid, "PCB", cleanPcbGuid);

        if (clientDisp != null)
        {
            clientDisp.Connect();
        }
        else
        {
            InvokeComMethod(_ocx, "Connect");
        }
    }

    public void SyncFullScreenState(bool fullScreen)
    {
        if (_ocx == null) return;
        try
        {
            if (_ocx is IMsRdpClientDisp clientDisp)
            {
                clientDisp.FullScreen = fullScreen;
                return;
            }
            SetComProperty(_ocx, "FullScreen", fullScreen);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RdpConsoleControl] SyncFullScreenState({fullScreen}) ignored: {ex.Message}");
        }
    }

    public void CaptureKeyboardFocus()
    {
        if (IsDisposed || Disposing) return;
        try
        {
            if (Visible)
            {
                Focus();
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Deactivate()
    {
        if (IsDisposed || Disposing) return;
        try
        {
            if ((_ocx ?? GetOcx()) is IOleInPlaceObject inPlaceObject)
            {
                inPlaceObject.UIDeactivate();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RdpConsoleControl] Deactivate ignored: {ex.Message}");
        }
    }

    public void NotifyRedirectDeviceChange(IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if ((_ocx ?? GetOcx()) is IMsRdpClientNonScriptable3 nonScriptable)
            {
                nonScriptable.NotifyRedirectDeviceChange((UIntPtr)unchecked((ulong)wParam.ToInt64()), lParam);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RdpConsoleControl] NotifyRedirectDeviceChange ignored: {ex.Message}");
        }
    }

    private static bool ShouldEnableFrameBufferRedirection()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization");
            if (key != null)
            {
                object? val = key.GetValue("DisableFrameBufferRedirection");
                if (val is int intVal)
                {
                    return intVal == 0;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RdpConsoleControl] DisableFrameBufferRedirection reg read ignored: {ex.Message}");
        }
        return true;
    }

    private static void EnsureLocalCredSspPolicyDefaults()
    {
        try
        {
            string[] subKeys =
            {
                "AllowDefaultCredentials",
                "AllowDefaultCredentialsDomain",
                "AllowDefaultCredentialsWhenNTLMOnly",
                "AllowDefaultCredentialsWhenNTLMOnlyDomain",
                "AllowFreshCredentials",
                "AllowFreshCredentialsDomain",
                "AllowFreshCredentialsWhenNTLMOnly",
                "AllowFreshCredentialsWhenNTLMOnlyDomain",
                "AllowSavedCredentials",
                "AllowSavedCredentialsDomain",
                "AllowSavedCredentialsWhenNTLMOnly",
                "AllowSavedCredentialsWhenNTLMOnlyDomain"
            };

            const string basePath = @"SYSTEM\CurrentControlSet\Control\Lsa\Credssp\PolicyDefaults";
            foreach (string sub in subKeys)
            {
                using var key = Registry.LocalMachine.CreateSubKey($@"{basePath}\{sub}", writable: true);
                key?.SetValue("Hyper-V", "Microsoft Virtual Console Service/*", RegistryValueKind.String);
                key?.SetValue("Hyper-V-Local", "Microsoft Virtual Console Service/localhost", RegistryValueKind.String);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RdpConsoleControl] EnsureLocalCredSspPolicyDefaults ignored: {ex.Message}");
        }
    }

    private static void TrySetExtendedProperty(IMsRdpExtendedSettings extSettings, string name, object value)
    {
        try
        {
            object boxed = value;
            int hr = extSettings.put_Property(name, ref boxed);
            if (hr < 0)
            {
                Debug.WriteLine($"[RdpConsoleControl] put_Property({name}) returned 0x{hr:X8}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RdpConsoleControl] TrySetExtendedProperty({name}) ignored: {ex.Message}");
        }
    }

    private static void SetAdvProperty(
        IMsRdpClientAdvSettingsDisp? advDisp,
        object? fallbackAdv,
        Action<IMsRdpClientAdvSettingsDisp> typedSetter,
        string propertyName,
        object fallbackValue)
    {
        if (advDisp != null)
        {
            SafeComAction(() => typedSetter(advDisp), $"AdvancedSettings.{propertyName}");
            return;
        }

        if (fallbackAdv != null)
        {
            SetComProperty(fallbackAdv, propertyName, fallbackValue);
        }
    }

    private static void SafeComAction(Action action, string label)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RdpConsoleControl] {label} ignored: {ex.Message}");
        }
    }

    public void DisconnectConsole()
    {
        if (_ocx == null) return;
        try
        {
            short connected = 0;
            if (_ocx is IMsRdpClientDisp clientDisp)
            {
                connected = clientDisp.Connected;
                if (connected != 0)
                {
                    clientDisp.Disconnect();
                }
                return;
            }

            object? connectedVal = GetComProperty(_ocx, "Connected");
            if ((connectedVal is short s && s != 0) || (connectedVal is int i && i != 0))
            {
                InvokeComMethod(_ocx, "Disconnect");
            }
        }
        catch
        {
        }
    }

    internal void RaiseOnConnecting() => Connecting?.Invoke();

    internal void RaiseOnConnected()
    {
        IsConnected = true;
        Connected?.Invoke();
    }

    internal void RaiseOnLoginComplete() => LoginCompleted?.Invoke();

    internal void RaiseOnDisconnected(int reason)
    {
        IsConnected = false;
        Deactivate();
        Disconnected?.Invoke(reason);
    }

    internal void RaiseOnFatalError(int errorCode) => FatalError?.Invoke(errorCode);

    internal void RaiseOnRemoteDesktopSizeChange(int w, int h)
    {
        if (w > 0 && h > 0)
        {
            RemoteDesktopWidth = w;
            RemoteDesktopHeight = h;
            HasActiveVideo = !(w == DisabledVideoResolution.Width && h == DisabledVideoResolution.Height);
        }
        RemoteDesktopSizeChanged?.Invoke(w, h);
    }

    internal void RaiseOnRequestGoFullScreen() => RequestEnterFullScreen?.Invoke();
    internal void RaiseOnRequestLeaveFullScreen() => RequestExitFullScreen?.Invoke();
    internal void RaiseOnRequestContainerMinimize() => RequestContainerMinimize?.Invoke();
    internal void RaiseOnConfirmClose() => RequestClose?.Invoke();

    internal void RaiseOnFocusReleased(int direction)
    {
        Deactivate();
        FocusReleased?.Invoke(direction);
    }

    internal void RaiseOnMouseInputModeChanged(bool relative)
    {
        IsMouseModeRelative = relative;
        MouseInputModeChanged?.Invoke(relative);
    }

    internal void RaiseOnNetworkStatusChanged(uint qualityLevel, int bandwidth, int rtt)
    {
        LastNetworkQualityLevel = qualityLevel;
        LastBandwidthKbps = bandwidth;
        LastRoundTripTimeMs = rtt;
        NetworkStatusChanged?.Invoke(qualityLevel, bandwidth, rtt);
    }

    private static object? GetComProperty(object target, string propertyName)
    {
        try
        {
            return target.GetType().InvokeMember(
                propertyName,
                BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance,
                null,
                target,
                null
            );
        }
        catch
        {
            return null;
        }
    }

    private static void SetComProperty(object target, string propertyName, object value)
    {
        try
        {
            target.GetType().InvokeMember(
                propertyName,
                BindingFlags.SetProperty | BindingFlags.Public | BindingFlags.Instance,
                null,
                target,
                new[] { value }
            );
            return;
        }
        catch
        {
            // Fallback to explicit DISPATCH_PROPERTYPUT if SetProperty failed
        }

        try
        {
            target.GetType().InvokeMember(
                propertyName,
                BindingFlags.PutDispProperty | BindingFlags.Public | BindingFlags.Instance,
                null,
                target,
                new[] { value }
            );
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RdpConsoleControl] SetComProperty({propertyName}) ignored: {ex.Message}");
        }
    }

    private static void InvokeComMethod(object target, string methodName, params object[] args)
    {
        try
        {
            target.GetType().InvokeMember(
                methodName,
                BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance,
                null,
                target,
                args
            );
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw tie.InnerException;
        }
    }
}
