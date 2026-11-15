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

[StructLayout(LayoutKind.Sequential)]
public struct RemotableHandle
{
    public int fContext;
    public int hInproc;
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
    void put_UIParentWindowHandle(ref RemotableHandle hwndUIParentWindowHandle);
    void get_UIParentWindowHandle(out IntPtr phwndUIParentWindowHandle);
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
    // MsRdpClient9NotSafeForScripting:  {8B918B82-7985-4C24-89DF-C33AD2BBFBCD} (preferred by VMConnect)
    // MsRdpClient10NotSafeForScripting: {A0C63C30-F08D-4AB4-907C-34905D770C7D}
    // MsRdpClient8NotSafeForScripting:  {A3BC03A0-041D-42E3-AD22-882B7865C9C5}
    private const string ClsidMsRdpClient9NotSafe  = "8b918b82-7985-4c24-89df-c33ad2bbfbcd";
    private const string ClsidMsRdpClient10NotSafe = "a0c63c30-f08d-4ab4-907c-34905d770c7d";
    private const string ClsidMsRdpClient8NotSafe  = "a3bc03a0-041d-42e3-ad22-882b7865c9c5";

    private const uint GA_ROOT = 2;
    private const int WDT_INPROC_CALL = 0x48746457;

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

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
        catch { }
        base.DetachSink();
    }

    /// <summary>
    /// Connects strictly to the Hyper-V Virtual Machine Console (raw synthetic framebuffer + GPU-PV)
    /// on localhost:2179 using the current Windows Admin / Windows Hello CredSSP token without credential delegation.
    /// </summary>
    public void ConnectToHyperVConsole(Guid vmGuid)
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

        EnsureLocalCredSspPolicyDefaults();

        // CRITICAL:
        // 1) PCB must be a clean 36-char uppercase GUID string WITHOUT curly braces ("D8A3F120-...").
        //    Passing "{D8A3F120-...}" causes vmms.exe on port 2179 to reject with 0x80070057.
        // 2) Do NOT append ";EnhancedMode=1" to PCB. A clean GUID without ";EnhancedMode=1"
        //    instructs vmms.exe on port 2179 to open a strict Basic Console Session
        //    (synthetic framebuffer / GPU-PV) rather than an Enhanced Session.
        string cleanPcbGuid = vmGuid.ToString("D").ToUpperInvariant();

        SetComProperty(_ocx, "Server", "localhost");
        SetComProperty(_ocx, "ColorDepth", 32);

        // Set UIParentWindowHandle via wireHWND (RemotableHandle with WDT_INPROC_CALL = 0x48746457)
        if (_ocx is IMsRdpClientNonScriptable3 nonScriptableParent)
        {
            try
            {
                IntPtr rootHwnd = GetAncestor(Handle, GA_ROOT);
                if (rootHwnd == IntPtr.Zero) rootHwnd = Handle;
                var wireHwnd = new RemotableHandle
                {
                    fContext = WDT_INPROC_CALL,
                    hInproc = rootHwnd.ToInt32()
                };
                nonScriptableParent.put_UIParentWindowHandle(ref wireHwnd);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RdpConsoleControl] put_UIParentWindowHandle ignored: {ex.Message}");
            }
        }

        object? primaryAdv =
            GetComProperty(_ocx, "AdvancedSettings9") ??
            GetComProperty(_ocx, "AdvancedSettings8") ??
            GetComProperty(_ocx, "AdvancedSettings7") ??
            GetComProperty(_ocx, "AdvancedSettings6") ??
            GetComProperty(_ocx, "AdvancedSettings2") ??
            GetComProperty(_ocx, "AdvancedSettings");

        if (primaryAdv != null)
        {
            // Port 2179 is the Hyper-V Virtual Machine Management Service (VMMS) console endpoint
            SetComProperty(primaryAdv, "RDPPort", 2179);
            SetComProperty(primaryAdv, "AuthenticationLevel", 0u);
            SetComProperty(primaryAdv, "AuthenticationServiceClass", "Microsoft Virtual Console Service");
        }

        // CRITICAL ORDER (matches official VMConnect & ExHyperV):
        // EnableCredSspSupport(true) and NegotiateSecurityLayer(false) MUST be called on the SAME
        // IMsRdpClientNonScriptable3 interface, and EnableCredSspSupport(true) MUST be set FIRST
        // before NegotiateSecurityLayer(false). Calling them in reverse order or across AdvancedSettings
        // causes NegotiateSecurityLayer(false) to fail with E_INVALIDARG!
        if (_ocx is IMsRdpClientNonScriptable3 nonScriptable)
        {
            try { nonScriptable.put_EnableCredSspSupport(true); } catch { }
            try { nonScriptable.put_NegotiateSecurityLayer(false); } catch { }
        }

        // CRITICAL FOR HYPER-V CONSOLE AUTHENTICATION (avoids RDP error 3848 / password prompt):
        // Set "DisableCredentialsDelegation" = true via IMsRdpExtendedSettings.
        // Without this, CredSSP attempts to delegate credentials to vmms.exe (which uses a self-signed
        // NTLM-only certificate), prompting for localhost credentials and failing with:
        // "Your system administrator does not allow you to connect to this remote computer."
        if (_ocx is IMsRdpExtendedSettings extSettings)
        {
            TrySetExtendedProperty(extSettings, "DisableCredentialsDelegation", true);
            TrySetExtendedProperty(extSettings, "DisableEnhancedMode", true);
            TrySetExtendedProperty(extSettings, "DisableTouchRemoting", true);
            TrySetExtendedProperty(extSettings, "EnableFrameBufferRedirection", true);
            TrySetExtendedProperty(extSettings, "EnableHardwareMode", true);
            TrySetExtendedProperty(extSettings, "DesktopScaleFactor", 100u);
            TrySetExtendedProperty(extSettings, "DeviceScaleFactor", 100u);
        }

        // CRITICAL FOR KEYBOARD SHORTCUTS (Win, Win+R, Alt+Tab, Win+E, etc.):
        // SecuredSettings.KeyboardHookMode = 1 instructs mstscax.dll to always forward
        // Windows key shortcuts to the Guest VM whenever the console window has focus
        // (both in Windowed mode and Borderless Fullscreen mode).
        object? securedSettings =
            GetComProperty(_ocx, "SecuredSettings3") ??
            GetComProperty(_ocx, "SecuredSettings2") ??
            GetComProperty(_ocx, "SecuredSettings");

        if (securedSettings != null)
        {
            SetComProperty(securedSettings, "KeyboardHookMode", 1);
            SetComProperty(securedSettings, "AudioRedirectionMode", 2); // 2 = Do not play via RDP
        }

        if (primaryAdv != null)
        {
            // Distraction-Free Fullscreen: disable the top blue RDP connection bar
            SetComProperty(primaryAdv, "DisplayConnectionBar", false);
            SetComProperty(primaryAdv, "ConnectionBarShowMinimizeButton", false);
            SetComProperty(primaryAdv, "ConnectionBarShowRestoreButton", false);
            SetComProperty(primaryAdv, "ConnectionBarShowPinButton", false);
            SetComProperty(primaryAdv, "ContainerHandledFullScreen", 1);

            // Optimize local VMBus Console Session framerate & input latency:
            // Compress = 0 disables CPU bulk compression bottleneck over localhost:2179.
            SetComProperty(primaryAdv, "Compress", 0);
            SetComProperty(primaryAdv, "BitmapPeristence", 1);
            SetComProperty(primaryAdv, "SmartSizing", true);
            SetComProperty(primaryAdv, "EnableAutoReconnect", true);
            SetComProperty(primaryAdv, "BandwidthDetection", false);
            SetComProperty(primaryAdv, "NetworkConnectionType", 6u); // 6 = CONNECTION_TYPE_LAN (10 Gbps+)
            SetComProperty(primaryAdv, "minInputSendInterval", 1);
            SetComProperty(primaryAdv, "PerformanceFlags", 0x00000180); // Font Smoothing + Desktop Composition
            SetComProperty(primaryAdv, "GrabFocusOnConnect", true);

            // Disable RDP audio/drive redirection since VMStream uses AF_HYPERV WaveRT audio
            SetComProperty(primaryAdv, "AudioRedirectionMode", 2); // 2 = Do not play via RDP
            SetComProperty(primaryAdv, "AudioCaptureRedirectionMode", false);
            SetComProperty(primaryAdv, "RedirectDrives", false);
            SetComProperty(primaryAdv, "RedirectPrinters", false);
            SetComProperty(primaryAdv, "RedirectSmartCards", false);
            SetComProperty(primaryAdv, "RedirectPorts", false);
            SetComProperty(primaryAdv, "RedirectPOSDevices", false);

            // Set Pre-Connection Blob (PCB) to the clean uppercase VM GUID right before Connect()
            SetComProperty(primaryAdv, "PCB", cleanPcbGuid);
        }

        // Initiate connection to localhost:2179
        InvokeComMethod(_ocx, "Connect");
    }

    public void SyncFullScreenState(bool fullScreen)
    {
        if (_ocx == null) return;
        try
        {
            SetComProperty(_ocx, "FullScreen", fullScreen);
        }
        catch { }
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
                using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey($@"{basePath}\{sub}", writable: true);
                key?.SetValue("Hyper-V", "Microsoft Virtual Console Service/*", Microsoft.Win32.RegistryValueKind.String);
                key?.SetValue("Hyper-V-Local", "Microsoft Virtual Console Service/localhost", Microsoft.Win32.RegistryValueKind.String);
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
            extSettings.put_Property(name, ref boxed);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RdpConsoleControl] TrySetExtendedProperty({name}) ignored: {ex.Message}");
        }
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
            else if (connectedVal is int i && i != 0)
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
