// ============================================================================
// ConsoleSessionForm.cs - Main Hyper-V Console Window & Subsystems Coordinator
// Hosts RdpConsoleControl (strictly Console Session on port 2179),
// AF_HYPERV Audio & Touch Transports, IHWindowClass Touch Subclassing,
// WASAPI Audio Engine, Low-Level Hotkey Hook, and Borderless Fullscreen.
// ============================================================================
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using HvConsole.Host.Audio;
using HvConsole.Host.Input;
using HvConsole.Host.Protocol;
using HvConsole.Host.Rdp;
using HvConsole.Host.Transport;
using HvConsole.Host.Wmi;

namespace HvConsole.Host.UI;

public sealed class ConsoleSessionForm : Form
{
    private const ushort VT_EMPTY     = 0;
    private const ushort VT_BOOL      = 11;
    private const short VARIANT_TRUE  = -1;
    private const short VARIANT_FALSE = 0;

    private static readonly Guid IidIPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly PROPERTYKEY PkeyEdgeGestureDisableTouchWhenFullscreen = new()
    {
        fmtid = new Guid("32CE38B2-2C9A-41B1-9BC5-B3784394AA44"),
        pid = 2
    };

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(2)] public ushort wReserved1;
        [FieldOffset(4)] public ushort wReserved2;
        [FieldOffset(6)] public ushort wReserved3;
        [FieldOffset(8)] public short boolVal;
        [FieldOffset(8)] public IntPtr ptr1;
        [FieldOffset(16)] public IntPtr ptr2;
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount([Out] out uint cProps);
        [PreserveSig] int GetAt([In] uint iProp, [Out] out PROPERTYKEY pkey);
        [PreserveSig] int GetValue([In] ref PROPERTYKEY key, [Out] out PROPVARIANT pv);
        [PreserveSig] int SetValue([In] ref PROPERTYKEY key, [In] ref PROPVARIANT propvar);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(
        IntPtr hwnd,
        [In] ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? propertyStore
    );

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint uPeriod);

    private readonly HyperVVmInfo _vmInfo;
    private readonly IHvTransport _audioTransport;
    private readonly IHvTransport _touchTransport;
    private readonly HostAudioEngine _audioEngine;
    private readonly TouchWindowInterceptor _touchInterceptor;
    private readonly LowLevelKeyboardHook _keyboardHook;

    private readonly RdpConsoleControl _rdpControl;
    private readonly Panel _statusBannerPanel;
    private readonly Label _lblBannerMessage;
    private readonly QuickSettingsHudForm _hudForm;
    private readonly ToastOverlayForm _toastForm;
    private readonly System.Windows.Forms.Timer _subclassWatchdogTimer;
    private readonly System.Windows.Forms.Timer _hookReassertTimer;

    private bool _isBorderlessFullscreen;
    private bool _edgeGesturePropSet;
    private bool _isClosing;
    private FormWindowState _savedWindowState = FormWindowState.Normal;
    private FormBorderStyle _savedBorderStyle = FormBorderStyle.Sizable;
    private Rectangle _savedWindowBounds = new(100, 100, 1440, 900);
    private bool _startInFullscreen;

    public ConsoleSessionForm(HyperVVmInfo vmInfo, bool startInFullscreen = false, bool forceTcpFallback = false)
    {
        _vmInfo = vmInfo;
        _startInFullscreen = startInFullscreen;

        Text = $"VMStream - {_vmInfo.Name} [Console Session | Mic: OFF (Locked)]";
        BackColor = Color.FromArgb(10, 14, 23);
        ForeColor = Color.FromArgb(241, 245, 249);
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1440, 900);
        MinimumSize = new Size(800, 600);

        // Initialize VM-isolated AF_HYPERV transports (bound to exact VmId, never wildcard)
        _audioTransport = new HvSocketTransport(
            _vmInfo.VmId,
            HvProtocolConstants.AudioServiceGuid,
            HvProtocolConstants.TcpFallbackPortAudio,
            forceTcpFallback
        );

        _touchTransport = new HvSocketTransport(
            _vmInfo.VmId,
            HvProtocolConstants.TouchServiceGuid,
            HvProtocolConstants.TcpFallbackPortTouch,
            forceTcpFallback
        );

        _audioEngine = new HostAudioEngine(_audioTransport);
        _touchInterceptor = new TouchWindowInterceptor(_touchTransport);
        _keyboardHook = new LowLevelKeyboardHook();

        _toastForm = new ToastOverlayForm();
        _hudForm = new QuickSettingsHudForm(
            _vmInfo,
            _audioTransport,
            _touchTransport,
            _audioEngine,
            _touchInterceptor
        );

        _hudForm.RequestToggleFullscreen += () => BeginInvoke(ToggleBorderlessFullscreen);
        _hudForm.RequestDisconnect += () => BeginInvoke(Close);
        _hudForm.RequestToggleMicrophone += () => BeginInvoke(ToggleMicrophoneWithFeedback);
        _hudForm.VisibleChanged += (_, _) => ScheduleKeyboardHookBringToFront();

        _keyboardHook.IsHudVisibleCallback = () => _hudForm.Visible;
        _keyboardHook.ToggleFullscreenRequested += () => BeginInvoke(ToggleBorderlessFullscreen);
        _keyboardHook.ToggleHudRequested += () => BeginInvoke(ToggleQuickSettingsHud);
        _keyboardHook.DismissHudRequested += () => BeginInvoke(() =>
        {
            if (_hudForm.Visible) _hudForm.Hide();
        });
        _keyboardHook.ToggleMicrophoneRequested += () => BeginInvoke(ToggleMicrophoneWithFeedback);

        _touchInterceptor.RdpFocusAcquired += ScheduleKeyboardHookBringToFront;
        Activated += (_, _) => ScheduleKeyboardHookBringToFront();

        _audioEngine.StateChanged += () =>
        {
            if (!IsDisposed && IsHandleCreated)
            {
                BeginInvoke(UpdateWindowTitleAndHud);
            }
        };

        // Top non-intrusive connection status banner (hidden automatically once connected)
        _statusBannerPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 32,
            BackColor = Color.FromArgb(15, 23, 42)
        };

        _lblBannerMessage = new Label
        {
            Dock = DockStyle.Fill,
            Text = $"Connecting to Hyper-V Console '{_vmInfo.Name}' ({_vmInfo.PcbGuidString}) on port 2179...  |  Hotkeys: Ctrl+Alt+Shift+H (HUD)  *  Ctrl+Alt+Shift+M (Mic)  *  Ctrl+Alt+Pause (Fullscreen)",
            Font = new Font("Segoe UI", 9.0f),
            ForeColor = Color.FromArgb(148, 163, 184),
            TextAlign = ContentAlignment.MiddleCenter
        };
        _statusBannerPanel.Controls.Add(_lblBannerMessage);

        _rdpControl = new RdpConsoleControl
        {
            Dock = DockStyle.Fill
        };
        ((System.ComponentModel.ISupportInitialize)_rdpControl).BeginInit();
        Controls.Add(_rdpControl);
        Controls.Add(_statusBannerPanel);
        ((System.ComponentModel.ISupportInitialize)_rdpControl).EndInit();

        _rdpControl.Connecting += OnRdpConnecting;
        _rdpControl.Connected += OnRdpConnected;
        _rdpControl.Disconnected += OnRdpDisconnected;
        _rdpControl.FatalError += OnRdpFatalError;
        _rdpControl.RemoteDesktopSizeChanged += OnRdpDesktopSizeChanged;

        // Reassert WH_KEYBOARD_LL chain priority shortly after focus/connect/fullscreen transitions
        // so Ctrl+Alt+Shift+* hotkeys stay ahead of mstscax.dll's KeyboardHookMode=1 hook.
        _hookReassertTimer = new System.Windows.Forms.Timer { Interval = 150 };
        _hookReassertTimer.Tick += (_, _) =>
        {
            _hookReassertTimer.Stop();
            if (!IsDisposed && !_isClosing)
            {
                _keyboardHook.BringToFront();
            }
        };

        // Watchdog timer ensures IHWindowClass and all child windows remain subclassed
        // and unregistered from legacy WM_TOUCH even if mstscax.dll recreates them.
        _subclassWatchdogTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _subclassWatchdogTimer.Tick += (_, _) =>
        {
            if (_rdpControl.IsHandleCreated &&
                _touchInterceptor.AttachToRdpControlHierarchy(_rdpControl.Handle) > 0)
            {
                ScheduleKeyboardHookBringToFront();
            }
        };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        try { timeBeginPeriod(1); } catch { }

        _keyboardHook.Install();
        _audioTransport.Start();
        _touchTransport.Start();
        _audioEngine.Start();

        if (_startInFullscreen)
        {
            SetBorderlessFullscreen(true);
        }

        if (!_rdpControl.IsHandleCreated)
        {
            _rdpControl.CreateControl();
        }

        if (_touchInterceptor.AttachToRdpControlHierarchy(_rdpControl.Handle) > 0)
        {
            ScheduleKeyboardHookBringToFront();
        }
        _subclassWatchdogTimer.Start();

        BeginInvoke(() =>
        {
            try
            {
                _rdpControl.ConnectToHyperVConsole(_vmInfo.VmId);
                _touchInterceptor.AttachToRdpControlHierarchy(_rdpControl.Handle);
                ScheduleKeyboardHookBringToFront();
            }
            catch (Exception ex)
            {
                _lblBannerMessage.ForeColor = Color.FromArgb(248, 113, 113);
                _lblBannerMessage.Text = $"Console connection failed: {ex.Message}";
            }
        });
    }

    private void OnRdpConnecting()
    {
        if (InvokeRequired)
        {
            BeginInvoke(OnRdpConnecting);
            return;
        }
        _statusBannerPanel.Visible = true;
        _lblBannerMessage.Text = $"Authenticating with Hyper-V VMMS (localhost:2179, PCB={_vmInfo.PcbGuidString})...";
        if (_rdpControl.IsHandleCreated &&
            _touchInterceptor.AttachToRdpControlHierarchy(_rdpControl.Handle) > 0)
        {
            ScheduleKeyboardHookBringToFront();
        }
    }

    private void OnRdpConnected()
    {
        if (InvokeRequired)
        {
            BeginInvoke(OnRdpConnected);
            return;
        }

        // Hide status banner for a zero-chrome distraction-free viewport
        _statusBannerPanel.Visible = false;

        // Subclass internal IHWindowClass child window of mstscax.dll for WM_POINTER capture
        _touchInterceptor.AttachToRdpControlHierarchy(_rdpControl.Handle);
        _subclassWatchdogTimer.Start();

        if (_isBorderlessFullscreen)
        {
            _rdpControl.SyncFullScreenState(true);
        }

        ScheduleKeyboardHookBringToFront();
        UpdateWindowTitleAndHud();

        _toastForm.ShowToast(
            this,
            $"Connected to {_vmInfo.Name}",
            "Ctrl+Alt+Shift+H: Quick HUD  |  Ctrl+Alt+Shift+M: Mic Toggle",
            Color.FromArgb(56, 189, 248),
            2600
        );
    }

    private void OnRdpDesktopSizeChanged(int width, int height)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnRdpDesktopSizeChanged(width, height));
            return;
        }

        if (_rdpControl.IsHandleCreated &&
            _touchInterceptor.AttachToRdpControlHierarchy(_rdpControl.Handle) > 0)
        {
            ScheduleKeyboardHookBringToFront();
        }
    }

    private void OnRdpDisconnected(int reason)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnRdpDisconnected(reason));
            return;
        }

        _subclassWatchdogTimer.Stop();
        _hookReassertTimer.Stop();
        _touchInterceptor.SendTouchReset();
        _audioEngine.SetMicrophonePrivacy(false);

        _statusBannerPanel.Visible = true;
        _lblBannerMessage.ForeColor = Color.FromArgb(251, 191, 36);
        _lblBannerMessage.Text = $"Console session disconnected (Reason code: 0x{reason:X}). Close window or restart VM.";
    }

    private void OnRdpFatalError(int errorCode)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnRdpFatalError(errorCode));
            return;
        }

        _statusBannerPanel.Visible = true;
        _lblBannerMessage.ForeColor = Color.FromArgb(248, 113, 113);
        _lblBannerMessage.Text = $"RDP ActiveX Fatal Error: 0x{errorCode:X}";
    }

    // ========================================================================
    // Distraction-Free Borderless Fullscreen (Ctrl+Alt+Pause / Ctrl+Alt+Shift+F)
    // ========================================================================
    public void ToggleBorderlessFullscreen()
    {
        SetBorderlessFullscreen(!_isBorderlessFullscreen);
    }

    public void SetBorderlessFullscreen(bool enableFullscreen)
    {
        if (_isBorderlessFullscreen == enableFullscreen) return;
        _isBorderlessFullscreen = enableFullscreen;

        if (enableFullscreen)
        {
            _savedWindowState = WindowState;
            _savedBorderStyle = FormBorderStyle;
            _savedWindowBounds = Bounds;

            if (WindowState == FormWindowState.Maximized)
            {
                WindowState = FormWindowState.Normal;
            }

            FormBorderStyle = FormBorderStyle.None;
            Screen currentScreen = Screen.FromControl(this);
            Bounds = currentScreen.Bounds;
            _statusBannerPanel.Visible = false;

            _toastForm.ShowToast(
                this,
                "Borderless Fullscreen Enabled",
                "Press Ctrl+Alt+Pause or Ctrl+Alt+Shift+F to return to Windowed mode",
                Color.FromArgb(56, 189, 248),
                1800
            );
        }
        else
        {
            FormBorderStyle = _savedBorderStyle;
            Bounds = _savedWindowBounds;
            WindowState = _savedWindowState;

            _toastForm.ShowToast(
                this,
                "Windowed Mode Restored",
                "Press Ctrl+Alt+Pause or Ctrl+Alt+Shift+F for Fullscreen",
                Color.FromArgb(148, 163, 184),
                1600
            );
        }

        SetFullscreenEdgeGestureDisabled(enableFullscreen);
        _rdpControl.SyncFullScreenState(enableFullscreen);
        _rdpControl.Focus();
        ScheduleKeyboardHookBringToFront();
    }

    // ========================================================================
    // Quick-Settings HUD & Microphone Privacy Hotkey Actions
    // ========================================================================
    public void ToggleQuickSettingsHud()
    {
        if (_hudForm.Visible)
        {
            _hudForm.Hide();
            _rdpControl.Focus();
            ScheduleKeyboardHookBringToFront();
        }
        else
        {
            _hudForm.ShowCenteredOver(this);
        }
    }

    public void ToggleMicrophoneWithFeedback()
    {
        bool enabled = _audioEngine.ToggleMicrophonePrivacy();
        UpdateWindowTitleAndHud();

        if (!enabled)
        {
            _toastForm.ShowToast(
                this,
                "[LOCKED] Host Microphone: MUTED & LOCKED",
                "Host WASAPI IAudioClient closed - Windows mic tray icon OFF",
                Color.FromArgb(34, 197, 94),
                2400
            );
        }
        else if (_audioEngine.IsMicHardwareOpen)
        {
            _toastForm.ShowToast(
                this,
                "[LIVE] Host Microphone: LIVE STREAMING",
                "Streaming 48 kHz PCM over AF_HYPERV to Guest VM",
                Color.FromArgb(239, 68, 68),
                2400
            );
        }
        else
        {
            _toastForm.ShowToast(
                this,
                "[ARMED] Host Microphone: ARMED (KSSTATE Gate)",
                "Host mic will open automatically when a Guest app records",
                Color.FromArgb(234, 179, 8),
                2400
            );
        }
    }

    private void UpdateWindowTitleAndHud()
    {
        string micTag = !_audioEngine.IsMicUserEnabled
            ? "Mic: OFF (Locked)"
            : (_audioEngine.IsMicHardwareOpen ? "Mic: LIVE" : "Mic: ARMED");

        Text = $"VMStream - {_vmInfo.Name} [Console Session | {micTag}]";

        if (_hudForm.Visible)
        {
            _hudForm.UpdateUiState();
        }
    }

    private void ScheduleKeyboardHookBringToFront()
    {
        if (IsDisposed || _isClosing) return;

        if (InvokeRequired)
        {
            try
            {
                if (IsHandleCreated)
                {
                    BeginInvoke(ScheduleKeyboardHookBringToFront);
                }
            }
            catch { }
            return;
        }

        _keyboardHook.BringToFront();
        _hookReassertTimer.Stop();
        _hookReassertTimer.Start();
    }

    private void SetFullscreenEdgeGestureDisabled(bool? disabled)
    {
        if (IsDisposed || !IsHandleCreated) return;

        IPropertyStore? store = null;
        try
        {
            Guid iid = IidIPropertyStore;
            int hr = SHGetPropertyStoreForWindow(Handle, ref iid, out store);
            if (hr < 0 || store == null) return;

            PROPERTYKEY key = PkeyEdgeGestureDisableTouchWhenFullscreen;
            PROPVARIANT pv = default;
            if (disabled.HasValue)
            {
                pv.vt = VT_BOOL;
                pv.boolVal = disabled.Value ? VARIANT_TRUE : VARIANT_FALSE;
                _edgeGesturePropSet = true;
            }
            else
            {
                pv.vt = VT_EMPTY;
                _edgeGesturePropSet = false;
            }

            store.SetValue(ref key, ref pv);
            store.Commit();
        }
        catch
        {
        }
        finally
        {
            if (store != null)
            {
                try { Marshal.FinalReleaseComObject(store); } catch { }
            }
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _isClosing = true;
        _subclassWatchdogTimer.Stop();
        _hookReassertTimer.Stop();

        if (_edgeGesturePropSet)
        {
            SetFullscreenEdgeGestureDisabled(null);
        }

        try { _hudForm.Hide(); } catch { }
        try { _toastForm.Hide(); } catch { }

        // Release all touch contacts and microphone handle before closing
        _touchInterceptor.SendTouchReset();
        _touchInterceptor.Dispose();
        _keyboardHook.Dispose();
        _audioEngine.Dispose();
        _audioTransport.Dispose();
        _touchTransport.Dispose();
        _rdpControl.DisconnectConsole();

        try { timeEndPeriod(1); } catch { }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _subclassWatchdogTimer.Dispose();
            _hookReassertTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
