// ============================================================================
// RdpConsoleControl.cs - Native mstscax.dll ActiveX Host for Strict Hyper-V Console
// Configures MsRdpClient10NotSafeForScripting / MsRdpClient9NotSafeForScripting
// strictly in Console Session mode (Port 2179, PCB = clean GUID without braces,
// DisableEnhancedMode = true, DisplayConnectionBar = false).
// ============================================================================
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace HvConsole.Host.Rdp;

/// <summary>
/// COM Event Sink for IMsTscAxEvents ({336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6})
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
    [DispId(3)] public void OnLoginComplete() { }
    [DispId(4)] public void OnDisconnected(int discReason) => _owner.RaiseOnDisconnected(discReason);
    [DispId(5)] public void OnEnterFullScreenMode() { }
    [DispId(6)] public void OnLeaveFullScreenMode() { }
    [DispId(7)] public void OnChannelReceivedData(string chanName, string data) { }
    [DispId(8)] public void OnRequestGoFullScreen() { }
    [DispId(9)] public void OnRequestLeaveFullScreen() { }
    [DispId(10)] public void OnFatalError(int errorCode) => _owner.RaiseOnFatalError(errorCode);
    [DispId(11)] public void OnWarning(int warningCode) { }
    [DispId(12)] public void OnRemoteDesktopSizeChange(int width, int height) => _owner.RaiseOnRemoteDesktopSizeChange(width, height);
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
}

/// <summary>
/// Direct COM interface for IMsRdpExtendedSettings ({302D8188-0052-4807-806A-362B628F9AC5})
/// used to configure "PCB" and "DisableEnhancedMode".
/// </summary>
[ComImport]
[Guid("302D8188-0052-4807-806A-362B628F9AC5")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMsRdpExtendedSettings
{
    [PreserveSig]
    int put_Property([MarshalAs(UnmanagedType.BStr)] string bstrPropertyName, [In] ref object pValue);

    [PreserveSig]
    int get_Property([MarshalAs(UnmanagedType.BStr)] string bstrPropertyName, [Out] out object pValue);
}

/// <summary>
/// Direct COM interface for IMsRdpClientNonScriptable3 ({B3378D90-0728-45C7-8ED7-B6159FB92219})
/// </summary>
[ComImport]
[Guid("B3378D90-0728-45C7-8ED7-B6159FB92219")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMsRdpClientNonScriptable3
{
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
    void NotifyRedirectDeviceChange(IntPtr wParam, IntPtr lParam);
    void SendKeys(int numKeys, ref bool pbArrayKeyUp, ref int plKeyData);
    void put_UIParentWindowHandle(IntPtr hwndUIParentWindowHandle);
    IntPtr get_UIParentWindowHandle();
    void put_ShowRedirectionWarningDialog([MarshalAs(UnmanagedType.VariantBool)] bool fShowRdrDlg);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_ShowRedirectionWarningDialog();
    void put_PromptForCredentials([MarshalAs(UnmanagedType.VariantBool)] bool fPrompt);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_PromptForCredentials();
    void put_NegotiateSecurityLayer([MarshalAs(UnmanagedType.VariantBool)] bool fNegotiate);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_NegotiateSecurityLayer();
    void put_EnableCredSspSupport([MarshalAs(UnmanagedType.VariantBool)] bool fEnableSupport);
    [return: MarshalAs(UnmanagedType.VariantBool)] bool get_EnableCredSspSupport();
}

/// <summary>
/// Hosts the native Windows mstscax.dll ActiveX Control for strict Hyper-V Console Sessions.
/// </summary>
[DesignerCategory("Code")]
public sealed class RdpConsoleControl : AxHost
{
    // MsRdpClient10NotSafeForScripting: {A0C63C30-F08D-4AB4-907C-34905D770C7D}
    // MsRdpClient9NotSafeForScripting:  {7390F3D8-0439-4C05-91E3-CF5CB290C3D0}
    private const string ClsidMsRdpClient10NotSafe = "a0c63c30-f08d-4ab4-907c-34905d770c7d";
    private const string ClsidMsRdpClient9NotSafe  = "7390f3d8-0439-4c05-91e3-cf5cb290c3d0";

    private object? _ocx;
    private ConnectionPointCookie? _eventCookie;
    private RdpActiveXEventSink? _eventSink;

    public event Action? Connecting;
    public event Action? Connected;
    public event Action<int>? Disconnected;
    public event Action<int>? FatalError;
    public event Action<int, int>? RemoteDesktopSizeChanged;

    public int RemoteDesktopWidth { get; private set; } = 1920;
    public int RemoteDesktopHeight { get; private set; } = 1080;

    public RdpConsoleControl()
        : base(SelectAvailableRdpClsid())
    {
    }

    private static string SelectAvailableRdpClsid()
    {
        if (Type.GetTypeFromCLSID(new Guid(ClsidMsRdpClient10NotSafe)) != null)
        {
            return ClsidMsRdpClient10NotSafe;
        }
        return ClsidMsRdpClient9NotSafe;
    }

    protected override void AttachInterfaces()
    {
        base.AttachInterfaces();
        _ocx = GetOcx();
    }

    protected override void CreateSink()
    {
        base.CreateSink();
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
        catch { }
        base.DetachSink();
    }

    /// <summary>
    /// Connects strictly to the Hyper-V Virtual Machine Console (raw synthetic framebuffer + GPU-PV)
    /// on localhost:2179 using the current Windows Admin / Windows Hello CredSSP token.
    /// </summary>
    public void ConnectToHyperVConsole(Guid vmGuid)
    {
        if (_ocx == null)
        {
            throw new InvalidOperationException("Underlying mstscax.dll ActiveX control is not initialized.");
        }

        // CRITICAL: PCB must be a clean GUID string WITHOUT curly braces ("d8a3f120-...").
        // Passing "{d8a3f120-...}" causes vmms.exe on port 2179 to reject with 0x80070057.
        string cleanPcbGuid = vmGuid.ToString("D").ToLowerInvariant();

        SetComProperty(_ocx, "Server", "localhost");
        SetComProperty(_ocx, "ColorDepth", 32);

        object? advSettings = GetComProperty(_ocx, "AdvancedSettings9")
                           ?? GetComProperty(_ocx, "AdvancedSettings8")
                           ?? GetComProperty(_ocx, "AdvancedSettings7")
                           ?? GetComProperty(_ocx, "AdvancedSettings2");

        if (advSettings != null)
        {
            // Port 2179 is the Hyper-V Virtual Machine Management Service (VMMS) console endpoint
            SetComProperty(advSettings, "RDPPort", 2179);
            SetComProperty(advSettings, "AuthenticationServiceClass", "Microsoft Virtual Console Service");
            SetComProperty(advSettings, "AuthenticationLevel", 0);
            SetComProperty(advSettings, "EnableCredSspSupport", true);

            // Distraction-Free Fullscreen: disable the top blue RDP connection bar
            SetComProperty(advSettings, "DisplayConnectionBar", false);
            SetComProperty(advSettings, "ConnectionBarShowMinimizeButton", false);
            SetComProperty(advSettings, "ConnectionBarShowRestoreButton", false);
            SetComProperty(advSettings, "ConnectionBarShowPinButton", false);

            // Enable dynamic viewport scaling & lowest input dispatch interval
            SetComProperty(advSettings, "SmartSizing", true);
            SetComProperty(advSettings, "minInputSendInterval", 1);
            SetComProperty(advSettings, "PerformanceFlags", 0);
            SetComProperty(advSettings, "GrabFocusOnConnect", true);
            // Disable RDP audio/drive redirection since VMStream uses AF_HYPERV WaveRT audio
            SetComProperty(advSettings, "AudioRedirectionMode", 2); // 2 = Do not play via RDP
            SetComProperty(advSettings, "AudioCaptureRedirectionMode", false);
        }

        // Configure NonScriptable interface for NegotiateSecurityLayer & CredSSP
        if (_ocx is IMsRdpClientNonScriptable3 nonScriptable)
        {
            try
            {
                nonScriptable.put_NegotiateSecurityLayer(true);
                nonScriptable.put_EnableCredSspSupport(true);
                nonScriptable.put_PromptForCredentials(false);
                nonScriptable.put_ShowRedirectionWarningDialog(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RdpConsoleControl] NonScriptable setup warning: {ex.Message}");
            }
        }

        // Configure ExtendedSettings: DisableEnhancedMode = true and PCB = "<VM_GUID>"
        if (_ocx is IMsRdpExtendedSettings extSettings)
        {
            object disableEnhanced = true;
            extSettings.put_Property("DisableEnhancedMode", ref disableEnhanced);

            object pcbValue = cleanPcbGuid;
            int hr = extSettings.put_Property("PCB", ref pcbValue);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }
        }
        else
        {
            // Fallback via IDispatch if QI for IMsRdpExtendedSettings uses a different IID
            SetExtendedSettingFallback(_ocx, "DisableEnhancedMode", true);
            SetExtendedSettingFallback(_ocx, "PCB", cleanPcbGuid);
        }

        // Also explicitly set EnhancedMode = false if exposed on the wrapper
        SetComProperty(_ocx, "EnhancedMode", false);

        // Initiate connection to localhost:2179
        InvokeComMethod(_ocx, "Connect");
    }

    public void DisconnectConsole()
    {
        if (_ocx == null) return;
        try
        {
            object? connectedVal = GetComProperty(_ocx, "Connected");
            if (connectedVal is short s && s != 0)
            {
                InvokeComMethod(_ocx, "Disconnect");
            }
        }
        catch { }
    }

    internal void RaiseOnConnecting() => Connecting?.Invoke();
    internal void RaiseOnConnected() => Connected?.Invoke();
    internal void RaiseOnDisconnected(int reason) => Disconnected?.Invoke(reason);
    internal void RaiseOnFatalError(int errorCode) => FatalError?.Invoke(errorCode);
    internal void RaiseOnRemoteDesktopSizeChange(int w, int h)
    {
        if (w > 0 && h > 0)
        {
            RemoteDesktopWidth = w;
            RemoteDesktopHeight = h;
        }
        RemoteDesktopSizeChanged?.Invoke(w, h);
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
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RdpConsoleControl] SetComProperty({propertyName}) ignored: {ex.Message}");
        }
    }

    private static void SetExtendedSettingFallback(object ocx, string name, object value)
    {
        try
        {
            ocx.GetType().InvokeMember(
                "Property",
                BindingFlags.SetProperty | BindingFlags.Public | BindingFlags.Instance,
                null,
                ocx,
                new[] { name, value }
            );
        }
        catch { }
    }

    private static void InvokeComMethod(object target, string methodName, params object[] args)
    {
        target.GetType().InvokeMember(
            methodName,
            BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance,
            null,
            target,
            args
        );
    }
}
