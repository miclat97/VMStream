// ============================================================================
// ConsoleSessionForm.cs - Main Hyper-V Console Window & Subsystems Coordinator
// Hosts RdpConsoleControl (strictly Console Session on port 2179),
// AF_HYPERV Audio & Touch Transports, IHWindowClass Touch Subclassing,
// WASAPI Audio Engine, Low-Level Hotkey Hook, and Borderless Fullscreen.
// ============================================================================
using System;
using System.Drawing;
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

    private bool _isBorderlessFullscreen;
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

        _keyboardHook.IsHudVisibleCallback = () => _hudForm.Visible;
        _keyboardHook.ToggleFullscreenRequested += () => BeginInvoke(ToggleBorderlessFullscreen);
        _keyboardHook.ToggleHudRequested += () => BeginInvoke(ToggleQuickSettingsHud);
        _keyboardHook.DismissHudRequested += () => BeginInvoke(() =>
        {
            if (_hudForm.Visible) _hudForm.Hide();
        });
        _keyboardHook.ToggleMicrophoneRequested += () => BeginInvoke(ToggleMicrophoneWithFeedback);

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
            Text = $"Connecting to Hyper-V Console '{_vmInfo.Name}' ({_vmInfo.PcbGuidString}) on port 2179...  |  Hotkeys: Ctrl+Alt+Shift+H (HUD)  \u2022  Ctrl+Alt+Shift+M (Mic)  \u2022  Ctrl+Alt+Pause (Fullscreen)",
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

        // Watchdog timer ensures IHWindowClass remains subclassed even if mstscax.dll
        // recreates its internal child windows after a guest resolution change.
        _subclassWatchdogTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _subclassWatchdogTimer.Tick += (_, _) =>
        {
            if (_rdpControl.IsHandleCreated)
            {
                _touchInterceptor.AttachToRdpControlHierarchy(_rdpControl.Handle);
            }
        };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        _keyboardHook.Install();
        _audioTransport.Start();
        _touchTransport.Start();
        _audioEngine.Start();

        if (_startInFullscreen)
        {
            SetBorderlessFullscreen(true);
        }

        try
        {
            _rdpControl.ConnectToHyperVConsole(_vmInfo.VmId);
        }
        catch (Exception ex)
        {
            _lblBannerMessage.ForeColor = Color.FromArgb(248, 113, 113);
            _lblBannerMessage.Text = $"Console connection failed: {ex.Message}";
        }
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

        if (_rdpControl.IsHandleCreated)
        {
            _touchInterceptor.AttachToRdpControlHierarchy(_rdpControl.Handle);
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

        _rdpControl.Focus();
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
                "\U0001F512 Host Microphone: MUTED & LOCKED",
                "Host WASAPI IAudioClient closed - Windows mic tray icon OFF",
                Color.FromArgb(34, 197, 94),
                2400
            );
        }
        else if (_audioEngine.IsMicHardwareOpen)
        {
            _toastForm.ShowToast(
                this,
                "\U0001F399\uFE0F Host Microphone: LIVE STREAMING",
                "Streaming 48 kHz PCM over AF_HYPERV to Guest VM",
                Color.FromArgb(239, 68, 68),
                2400
            );
        }
        else
        {
            _toastForm.ShowToast(
                this,
                "\U0001F7E1 Host Microphone: ARMED (KSSTATE Gate)",
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

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _subclassWatchdogTimer.Stop();

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

        base.OnFormClosing(e);
    }
}
