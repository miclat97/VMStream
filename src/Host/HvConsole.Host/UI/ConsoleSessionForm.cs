// ============================================================================
// ConsoleSessionForm.cs - Main Hyper-V Console Window & Subsystems Coordinator
// Hosts RdpConsoleControl (strictly Console Session on port 2179),
// AF_HYPERV Audio & Touch Transports, IHWindowClass Touch Subclassing,
// WASAPI Audio Engine, Low-Level Hotkey Hook, and Borderless Fullscreen.
// ============================================================================
using System;
using System.Drawing;
using System.Globalization;
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

    private const int WM_DISPLAYCHANGE = 0x007E;
    private const int WM_NCACTIVATE    = 0x0086;
    private const int WM_DEVICECHANGE  = 0x0219;

    /// <summary>
    /// Selectable dark canvas hosting the unstretched RdpConsoleControl (Dock = None).
    /// Being Selectable allows Deactivate() / Alt+Space focus release to land cleanly
    /// without WinForms bouncing focus immediately back into the ActiveX control.
    /// </summary>
    private sealed class ViewportHostPanel : Panel
    {
        public ViewportHostPanel()
        {
            SetStyle(ControlStyles.Selectable, true);
            TabStop = false;
            Dock = DockStyle.Fill;
            BackColor = Color.Black;
        }
    }

    private readonly HyperVVmInfo _vmInfo;
    private readonly DeviceRedirectionSettings _deviceSettings;
    private readonly IHvTransport _audioTransport;
    private readonly IHvTransport _touchTransport;
    private readonly HostAudioEngine _audioEngine;
    private readonly TouchWindowInterceptor _touchInterceptor;
    private readonly LowLevelKeyboardHook _keyboardHook;

    private readonly ViewportHostPanel _rdpViewportPanel;
    private readonly RdpConsoleControl _rdpControl;
    private readonly Panel _statusBannerPanel;
    private readonly Label _lblBannerMessage;
    private readonly QuickSettingsHudForm _hudForm;
    private readonly ToastOverlayForm _toastForm;
    private readonly System.Windows.Forms.Timer _subclassWatchdogTimer;
    private readonly System.Windows.Forms.Timer _hookReassertTimer;

    private bool _isBorderlessFullscreen;
    private ZoomLevel _windowedZoomPreference;
    private bool _edgeGesturePropSet;
    private bool _isClosing;
    private FormWindowState _savedWindowState = FormWindowState.Normal;
    private FormBorderStyle _savedBorderStyle = FormBorderStyle.Sizable;
    private Rectangle _savedWindowBounds = new(100, 100, 1440, 900);
    private bool _startInFullscreen;

    public ConsoleSessionForm(
        HyperVVmInfo vmInfo,
        bool startInFullscreen = false,
        bool forceTcpFallback = false,
        AudioSampleRatePreference? sampleRatePreference = null,
        DeviceRedirectionSettings? deviceSettings = null)
    {
        _vmInfo = vmInfo;
        _startInFullscreen = startInFullscreen;
        _windowedZoomPreference = ConsoleZoomSettings.LoadSavedPreference();
        _deviceSettings = (deviceSettings ?? DeviceRedirectionSettings.Load()).Clone();

        string initialMicTag = _deviceSettings.EnableMicrophone ? "Mic: OFF (Locked)" : "Mic: Disabled";
        Text = $"VMStream - {_vmInfo.Name} [Console Session | {initialMicTag}]";
        BackColor = Color.Black;
        ForeColor = Color.FromArgb(241, 245, 249);
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1440, 900);
        MinimumSize = new Size(800, 600);
        KeyPreview = true;

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

        _audioEngine = new HostAudioEngine(
            _audioTransport,
            sampleRatePreference,
            speakersEnabled: _deviceSettings.EnableSpeakers,
            microphoneAllowed: _deviceSettings.EnableMicrophone
        );
        _touchInterceptor = new TouchWindowInterceptor(
            _touchTransport,
            enableTouchRedirection: _deviceSettings.EnableTouchscreen,
            enableTouchpadHorizontalScroll: _deviceSettings.EnableTouchpadHorizontalScroll
        );
        _keyboardHook = new LowLevelKeyboardHook();

        _toastForm = new ToastOverlayForm();
        _hudForm = new QuickSettingsHudForm(
            _vmInfo,
            _audioTransport,
            _touchTransport,
            _audioEngine,
            _touchInterceptor,
            _deviceSettings
        );

        _hudForm.GetWindowedZoomPreferenceCallback = () => _windowedZoomPreference;
        _hudForm.IsFullscreenCallback = () => _isBorderlessFullscreen;
        _hudForm.GetDisplayStatsCallback = FormatDisplayStatsForHud;
        _hudForm.RequestZoomLevelChanged += level => BeginInvoke(() => SetWindowedZoomPreference(level));
        _hudForm.RequestShowRdpDiagnostics += () => BeginInvoke(() => _rdpControl.TryShowSessionDiagnostics());
        _hudForm.RequestToggleFullscreen += () => BeginInvoke(ToggleBorderlessFullscreen);
        _hudForm.RequestDisconnect += () => BeginInvoke(Close);
        _hudForm.RequestToggleMicrophone += () => BeginInvoke(ToggleMicrophoneWithFeedback);
        _hudForm.RequestTouchConfigurationRefresh += () => BeginInvoke(() =>
        {
            if (!IsDisposed && !_isClosing && _rdpControl.IsHandleCreated)
            {
                _touchInterceptor.AttachToRdpControlHierarchy(_rdpControl.Handle);
                ScheduleKeyboardHookBringToFront();
            }
        });
        _hudForm.VisibleChanged += (_, _) =>
        {
            if (!_hudForm.Visible && !_isClosing && IsHandleCreated && _rdpControl.IsConnected)
            {
                BeginInvoke(() =>
                {
                    if (!IsDisposed && !_isClosing && !_hudForm.Visible)
                    {
                        _rdpControl.CaptureKeyboardFocus();
                    }
                });
            }
            ScheduleKeyboardHookBringToFront();
        };

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

        _rdpViewportPanel = new ViewportHostPanel();
        _rdpViewportPanel.Resize += (_, _) =>
        {
            if (!IsDisposed && _rdpControl != null && _rdpControl.IsHandleCreated)
            {
                _rdpControl.ApplyViewportLayout(_rdpViewportPanel.ClientSize, _isBorderlessFullscreen);
            }
        };

        _rdpControl = new RdpConsoleControl
        {
            Name = "RdpClient",
            Dock = DockStyle.None,
            Location = Point.Empty,
            Size = RdpConsoleControl.BiosResolution
        };

        ((System.ComponentModel.ISupportInitialize)_rdpControl).BeginInit();
        _rdpViewportPanel.Controls.Add(_rdpControl);
        Controls.Add(_rdpViewportPanel);
        Controls.Add(_statusBannerPanel);
        ((System.ComponentModel.ISupportInitialize)_rdpControl).EndInit();

        _rdpControl.Connecting += OnRdpConnecting;
        _rdpControl.Connected += OnRdpConnected;
        _rdpControl.LoginCompleted += OnRdpLoginCompleted;
        _rdpControl.Disconnected += OnRdpDisconnected;
        _rdpControl.FatalError += OnRdpFatalError;
        _rdpControl.RemoteDesktopSizeChanged += OnRdpDesktopSizeChanged;
        _rdpControl.RequestEnterFullScreen += () =>
        {
            if (!IsDisposed && !_isClosing && IsHandleCreated)
                BeginInvoke(() => SetBorderlessFullscreen(true));
        };
        _rdpControl.RequestExitFullScreen += () =>
        {
            if (!IsDisposed && !_isClosing && IsHandleCreated)
                BeginInvoke(() => SetBorderlessFullscreen(false));
        };
        _rdpControl.RequestContainerMinimize += () =>
        {
            if (!IsDisposed && !_isClosing && IsHandleCreated)
                BeginInvoke(() => WindowState = FormWindowState.Minimized);
        };
        _rdpControl.RequestClose += () =>
        {
            if (!IsDisposed && !_isClosing && IsHandleCreated)
                BeginInvoke(Close);
        };
        _rdpControl.FocusReleased += _ =>
        {
            if (!IsDisposed && !_isClosing && IsHandleCreated)
                BeginInvoke(ReleaseFocusToViewportHost);
        };
        _rdpControl.AltSpaceReleased += ReleaseFocusToViewportHost;
        _rdpControl.MouseActivate += (_, _) => ScheduleKeyboardHookBringToFront();

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
        if (_deviceSettings.AnyAudioChannelEnabled)
        {
            _audioTransport.Start();
            _audioEngine.Start();
        }
        if (_deviceSettings.AnyTouchChannelEnabled)
        {
            _touchTransport.Start();
        }

        if (_startInFullscreen)
        {
            SetBorderlessFullscreen(true);
        }

        if (!_rdpControl.IsHandleCreated)
        {
            _rdpControl.CreateControl();
        }

        _rdpControl.ApplyViewportLayout(_rdpViewportPanel.ClientSize, _isBorderlessFullscreen);

        if (_touchInterceptor.AttachToRdpControlHierarchy(_rdpControl.Handle) > 0)
        {
            ScheduleKeyboardHookBringToFront();
        }
        _subclassWatchdogTimer.Start();

        BeginInvoke(() =>
        {
            if (IsDisposed || _isClosing) return;
            try
            {
                _rdpControl.ConnectToHyperVConsole(
                    _vmInfo.VmId,
                    _vmInfo.Name,
                    disableTouchRemoting: _deviceSettings.EnableTouchscreen
                );
                _rdpControl.ApplyViewportLayout(_rdpViewportPanel.ClientSize, _isBorderlessFullscreen);
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

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (IsDisposed || _isClosing) return;

        if (!_isBorderlessFullscreen && _rdpControl.IsConnected && _windowedZoomPreference == ZoomLevel.Auto)
        {
            _rdpControl.SetZoomLevel(ZoomLevel.Auto, _rdpViewportPanel.ClientSize, isFullScreen: false, force: true);
        }
        else
        {
            _rdpControl.ApplyViewportLayout(_rdpViewportPanel.ClientSize, _isBorderlessFullscreen);
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Tab && !_hudForm.Visible && _rdpControl.IsConnected)
        {
            _rdpControl.CaptureKeyboardFocus();
        }
        base.OnKeyUp(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DEVICECHANGE && !_isClosing && _rdpControl != null && _rdpControl.IsHandleCreated)
        {
            _rdpControl.NotifyRedirectDeviceChange(m.WParam, m.LParam);
        }

        base.WndProc(ref m);

        if (IsDisposed || _isClosing || _rdpControl == null || _rdpViewportPanel == null || _hudForm == null) return;

        if (m.Msg == WM_DISPLAYCHANGE)
        {
            if (_isBorderlessFullscreen)
            {
                Bounds = Screen.FromControl(this).Bounds;
            }
            _rdpControl.ApplyViewportLayout(_rdpViewportPanel.ClientSize, _isBorderlessFullscreen);
        }
        else if (m.Msg == WM_NCACTIVATE)
        {
            if (m.WParam != IntPtr.Zero)
            {
                if (!_hudForm.Visible && _rdpControl.IsHandleCreated)
                {
                    _rdpControl.CaptureKeyboardFocus();
                }
                ScheduleKeyboardHookBringToFront();
            }
            else
            {
                _rdpControl.Deactivate();
            }
        }
    }

    private void ReleaseFocusToViewportHost()
    {
        if (IsDisposed || _isClosing) return;

        if (InvokeRequired)
        {
            try
            {
                if (IsHandleCreated)
                {
                    BeginInvoke(ReleaseFocusToViewportHost);
                }
            }
            catch { }
            return;
        }

        _rdpViewportPanel.Focus();
    }

    private void OnRdpConnecting()
    {
        if (InvokeRequired)
        {
            BeginInvoke(OnRdpConnecting);
            return;
        }
        if (IsDisposed || _isClosing) return;

        _statusBannerPanel.Visible = true;
        _lblBannerMessage.ForeColor = Color.FromArgb(148, 163, 184);
        _lblBannerMessage.Text = $"Authenticating with Hyper-V VMMS (localhost:2179, PCB={_vmInfo.PcbGuidString})...";
        PerformLayout();
        _rdpControl.ApplyViewportLayout(_rdpViewportPanel.ClientSize, _isBorderlessFullscreen);

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
        if (IsDisposed || _isClosing) return;

        // Hide status banner for a zero-chrome distraction-free viewport
        _statusBannerPanel.Visible = false;
        PerformLayout();

        // Subclass internal IHWindowClass child window of mstscax.dll for WM_POINTER capture
        _touchInterceptor.AttachToRdpControlHierarchy(_rdpControl.Handle);
        _subclassWatchdogTimer.Start();

        if (!_rdpControl.HasActiveVideo)
        {
            _rdpControl.Visible = false;
            _statusBannerPanel.Visible = true;
            _lblBannerMessage.ForeColor = Color.FromArgb(148, 163, 184);
            _lblBannerMessage.Text = "Virtual machine video is not active (240x240 disabled-video state).";
            PerformLayout();
        }
        else
        {
            _rdpControl.Visible = true;
            if (_isBorderlessFullscreen)
            {
                _rdpControl.SetZoomLevel(ZoomLevel.P100, _rdpViewportPanel.ClientSize, isFullScreen: true, force: true);
                _rdpControl.SyncFullScreenState(true);
            }
            else
            {
                _rdpControl.SetZoomLevel(_windowedZoomPreference, _rdpViewportPanel.ClientSize, isFullScreen: false, force: true);
            }

            if (!_hudForm.Visible)
            {
                _rdpControl.CaptureKeyboardFocus();
            }
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

    private void OnRdpLoginCompleted()
    {
        if (InvokeRequired)
        {
            BeginInvoke(OnRdpLoginCompleted);
            return;
        }
        if (IsDisposed || _isClosing || !_rdpControl.IsConnected || !_rdpControl.HasActiveVideo) return;

        ZoomLevel desired = _isBorderlessFullscreen ? ZoomLevel.P100 : _windowedZoomPreference;
        ZoomLevel resolved = desired == ZoomLevel.Auto ? _rdpControl.ComputeAutoZoomLevel() : desired;
        if (_rdpControl.CurrentZoomLevel != resolved)
        {
            _rdpControl.SetZoomLevel(desired, _rdpViewportPanel.ClientSize, _isBorderlessFullscreen, force: true);
        }
    }

    private void OnRdpDesktopSizeChanged(int width, int height)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnRdpDesktopSizeChanged(width, height));
            return;
        }
        if (IsDisposed || _isClosing) return;

        if (!_rdpControl.HasActiveVideo && _rdpControl.IsConnected)
        {
            _rdpControl.Visible = false;
            _statusBannerPanel.Visible = true;
            _lblBannerMessage.ForeColor = Color.FromArgb(148, 163, 184);
            _lblBannerMessage.Text = "Virtual machine video is not active (240x240 disabled-video state).";
            PerformLayout();
        }
        else
        {
            if (!_rdpControl.Visible)
            {
                _rdpControl.Visible = true;
                if (_rdpControl.IsConnected && _statusBannerPanel.Visible)
                {
                    _statusBannerPanel.Visible = false;
                    PerformLayout();
                }
            }

            _rdpControl.ApplyViewportLayout(_rdpViewportPanel.ClientSize, _isBorderlessFullscreen);
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
        if (IsDisposed || _isClosing) return;

        _subclassWatchdogTimer.Stop();
        _hookReassertTimer.Stop();
        _touchInterceptor.SendTouchReset();
        _audioEngine.SetMicrophonePrivacy(false);

        _statusBannerPanel.Visible = true;
        _lblBannerMessage.ForeColor = Color.FromArgb(251, 191, 36);
        _lblBannerMessage.Text = $"Console session disconnected (Reason code: 0x{reason:X}). Close window or restart VM.";
        PerformLayout();
        _rdpControl.ApplyViewportLayout(_rdpViewportPanel.ClientSize, _isBorderlessFullscreen);
    }

    private void OnRdpFatalError(int errorCode)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnRdpFatalError(errorCode));
            return;
        }
        if (IsDisposed || _isClosing) return;

        _statusBannerPanel.Visible = true;
        _lblBannerMessage.ForeColor = Color.FromArgb(248, 113, 113);
        _lblBannerMessage.Text = $"RDP ActiveX Fatal Error: 0x{errorCode:X}";
        PerformLayout();
        _rdpControl.ApplyViewportLayout(_rdpViewportPanel.ClientSize, _isBorderlessFullscreen);
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

        SuspendLayout();
        try
        {
            if (enableFullscreen)
            {
                _savedWindowState = WindowState;
                _savedBorderStyle = FormBorderStyle;
                _savedWindowBounds = WindowState == FormWindowState.Maximized ? RestoreBounds : Bounds;

                if (WindowState == FormWindowState.Maximized)
                {
                    WindowState = FormWindowState.Normal;
                }

                FormBorderStyle = FormBorderStyle.None;
                Screen currentScreen = Screen.FromControl(this);
                Bounds = currentScreen.Bounds;
                _statusBannerPanel.Visible = false;
                _isBorderlessFullscreen = true;
            }
            else
            {
                _isBorderlessFullscreen = false;
                if (_rdpControl.IsConnected)
                {
                    _rdpControl.SyncFullScreenState(false);
                }

                FormBorderStyle = _savedBorderStyle;
                Bounds = _savedWindowBounds;
                WindowState = _savedWindowState;
                if (!_rdpControl.IsConnected)
                {
                    _statusBannerPanel.Visible = true;
                }
            }
        }
        finally
        {
            ResumeLayout(true);
        }

        if (enableFullscreen)
        {
            _rdpControl.SetZoomLevel(ZoomLevel.P100, _rdpViewportPanel.ClientSize, isFullScreen: true, force: true);
            if (_rdpControl.IsConnected)
            {
                _rdpControl.SyncFullScreenState(true);
            }

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
            _rdpControl.SetZoomLevel(_windowedZoomPreference, _rdpViewportPanel.ClientSize, isFullScreen: false, force: true);

            _toastForm.ShowToast(
                this,
                "Windowed Mode Restored",
                "Press Ctrl+Alt+Pause or Ctrl+Alt+Shift+F for Fullscreen",
                Color.FromArgb(148, 163, 184),
                1600
            );
        }

        SetFullscreenEdgeGestureDisabled(enableFullscreen);
        _rdpControl.CaptureKeyboardFocus();
        ScheduleKeyboardHookBringToFront();
        Update();
    }

    public void SetWindowedZoomPreference(ZoomLevel preference)
    {
        if (IsDisposed || _isClosing) return;

        _windowedZoomPreference = preference;
        ConsoleZoomSettings.SavePreference(preference);

        if (!_isBorderlessFullscreen && _rdpControl.IsConnected)
        {
            _rdpControl.SetZoomLevel(_windowedZoomPreference, _rdpViewportPanel.ClientSize, isFullScreen: false, force: true);
        }
    }

    private string FormatDisplayStatsForHud()
    {
        int hostDpiPct = (int)Math.Round(((DeviceDpi > 0 ? DeviceDpi : 96) / 96.0) * 100.0);
        string fbrState = _rdpControl.IsFrameBufferRedirectionActive switch
        {
            true  => "ON",
            false => "OFF",
            null  => "N/A"
        };

        if (!_rdpControl.IsConnected)
        {
            return $"Connecting... | FBR:{fbrState} | Host DPI:{hostDpiPct}%";
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}x{1} @ {2}% -> {3}x{4}px | FBR:{5} | Host DPI:{6}%",
            _rdpControl.RemoteDesktopWidth,
            _rdpControl.RemoteDesktopHeight,
            (uint)_rdpControl.CurrentZoomLevel,
            _rdpControl.Width,
            _rdpControl.Height,
            fbrState,
            hostDpiPct
        );
    }

    // ========================================================================
    // Quick-Settings HUD & Microphone Privacy Hotkey Actions
    // ========================================================================
    public void ToggleQuickSettingsHud()
    {
        if (_hudForm.Visible)
        {
            _hudForm.Hide();
            _rdpControl.CaptureKeyboardFocus();
            ScheduleKeyboardHookBringToFront();
        }
        else
        {
            _hudForm.ShowCenteredOver(this);
        }
    }

    public void ToggleMicrophoneWithFeedback()
    {
        if (!_deviceSettings.EnableMicrophone)
        {
            _audioEngine.SetMicrophonePrivacy(false);
            UpdateWindowTitleAndHud();
            _toastForm.ShowToast(
                this,
                "[DISABLED] Host Microphone: Redirect Disabled",
                "Enable Microphone in VM Launcher settings to forward Host mic",
                Color.FromArgb(148, 163, 184),
                2200
            );
            return;
        }

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
            double captureKhz = _audioEngine.TargetCaptureSampleRate / 1000.0;
            _toastForm.ShowToast(
                this,
                "[LIVE] Host Microphone: LIVE STREAMING",
                string.Format(CultureInfo.InvariantCulture, "Streaming {0:0.#} kHz PCM over AF_HYPERV to Guest VM", captureKhz),
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
        string micTag = !_deviceSettings.EnableMicrophone
            ? "Mic: Disabled"
            : (!_audioEngine.IsMicUserEnabled
                ? "Mic: OFF (Locked)"
                : (_audioEngine.IsMicHardwareOpen ? "Mic: LIVE" : "Mic: ARMED"));

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
