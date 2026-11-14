// ============================================================================
// QuickSettingsHudForm.cs — Sleek Dark Frameless Floating Quick-Settings HUD
// Toggled via Ctrl + Alt + Shift + H (auto-dismissable with Esc).
// Displays VM telemetry, AF_HYPERV status, Speaker Volume/Mute, and the
// Privacy-Locked Host Microphone Toggle.
// ============================================================================
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using HvConsole.Host.Audio;
using HvConsole.Host.Input;
using HvConsole.Host.Transport;
using HvConsole.Host.Wmi;

namespace HvConsole.Host.UI;

public sealed class QuickSettingsHudForm : Form
{
    private readonly HyperVVmInfo _vmInfo;
    private readonly IHvTransport _audioTransport;
    private readonly IHvTransport _touchTransport;
    private readonly HostAudioEngine _audioEngine;
    private readonly TouchWindowInterceptor _touchInterceptor;

    private readonly Label _lblVmHeader;
    private readonly Label _lblTransportStatus;
    private readonly Label _lblBufferStats;
    private readonly Label _lblVolumeValue;
    private readonly TrackBar _sliderVolume;
    private readonly Button _btnSpeakerMute;
    private readonly Button _btnMicToggle;
    private readonly Label _lblMicSubstatus;
    private readonly CheckBox _chkGateKsState;
    private readonly Button _btnToggleFullscreen;
    private readonly Button _btnDisconnect;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    public event Action? RequestToggleFullscreen;
    public event Action? RequestDisconnect;
    public event Action? RequestToggleMicrophone;

    public QuickSettingsHudForm(
        HyperVVmInfo vmInfo,
        IHvTransport audioTransport,
        IHvTransport touchTransport,
        HostAudioEngine audioEngine,
        TouchWindowInterceptor touchInterceptor)
    {
        _vmInfo = vmInfo;
        _audioTransport = audioTransport;
        _touchTransport = touchTransport;
        _audioEngine = audioEngine;
        _touchInterceptor = touchInterceptor;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(15, 23, 42);
        ForeColor = Color.FromArgb(241, 245, 249);
        Size = new Size(460, 430);
        Opacity = 0.97;

        var headerFont = new Font("Segoe UI Semibold", 12f, FontStyle.Bold);
        var sectionFont = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
        var bodyFont = new Font("Segoe UI", 9.0f, FontStyle.Regular);
        var monoFont = new Font("Consolas", 8.75f, FontStyle.Regular);

        _lblVmHeader = new Label
        {
            Text = $"⚡ {_vmInfo.Name}   ({(_vmInfo.HasGpuPv ? "GPU-PV Accelerated" : "Synthetic Console")})",
            Font = headerFont,
            ForeColor = Color.FromArgb(56, 189, 248),
            Location = new Point(20, 18),
            Size = new Size(390, 26)
        };

        var btnCloseHud = new Button
        {
            Text = "✕",
            Font = bodyFont,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(30, 41, 59),
            ForeColor = Color.FromArgb(203, 213, 225),
            Location = new Point(414, 14),
            Size = new Size(30, 28),
            TabStop = false
        };
        btnCloseHud.FlatAppearance.BorderSize = 0;
        btnCloseHud.Click += (_, _) => Hide();

        _lblTransportStatus = new Label
        {
            Font = monoFont,
            ForeColor = Color.FromArgb(148, 163, 184),
            Location = new Point(20, 50),
            Size = new Size(420, 38)
        };

        _lblBufferStats = new Label
        {
            Font = monoFont,
            ForeColor = Color.FromArgb(148, 163, 184),
            Location = new Point(20, 92),
            Size = new Size(420, 22)
        };

        // --- Speaker Section ---
        var lblSpeakerTitle = new Label
        {
            Text = "SPEAKER PLAYBACK (48 kHz WaveRT -> Host WASAPI)",
            Font = sectionFont,
            ForeColor = Color.FromArgb(203, 213, 225),
            Location = new Point(20, 126),
            Size = new Size(340, 20)
        };

        _btnSpeakerMute = new Button
        {
            Text = "🔊 Unmuted",
            Font = bodyFont,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(30, 41, 59),
            ForeColor = Color.White,
            Location = new Point(20, 152),
            Size = new Size(115, 34)
        };
        _btnSpeakerMute.FlatAppearance.BorderColor = Color.FromArgb(71, 85, 105);
        _btnSpeakerMute.Click += (_, _) =>
        {
            _audioEngine.SpeakerMuted = !_audioEngine.SpeakerMuted;
            UpdateUiState();
        };

        _sliderVolume = new TrackBar
        {
            Minimum = 0,
            Maximum = 100,
            TickFrequency = 10,
            Value = (int)Math.Round(_audioEngine.SpeakerVolume * 100),
            Location = new Point(145, 150),
            Size = new Size(235, 40),
            BackColor = Color.FromArgb(15, 23, 42)
        };
        _sliderVolume.ValueChanged += (_, _) =>
        {
            _audioEngine.SpeakerVolume = _sliderVolume.Value / 100.0f;
            _lblVolumeValue.Text = $"{_sliderVolume.Value}%";
        };

        _lblVolumeValue = new Label
        {
            Text = "100%",
            Font = sectionFont,
            ForeColor = Color.FromArgb(241, 245, 249),
            TextAlign = ContentAlignment.MiddleRight,
            Location = new Point(385, 156),
            Size = new Size(55, 24)
        };

        // --- Microphone Privacy Section ---
        var lblMicTitle = new Label
        {
            Text = "HOST MICROPHONE PRIVACY LOCK (Ctrl+Alt+Shift+M)",
            Font = sectionFont,
            ForeColor = Color.FromArgb(203, 213, 225),
            Location = new Point(20, 204),
            Size = new Size(420, 20)
        };

        _btnMicToggle = new Button
        {
            Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
            FlatStyle = FlatStyle.Flat,
            Location = new Point(20, 230),
            Size = new Size(420, 44)
        };
        _btnMicToggle.Click += (_, _) => RequestToggleMicrophone?.Invoke();

        _lblMicSubstatus = new Label
        {
            Font = bodyFont,
            ForeColor = Color.FromArgb(148, 163, 184),
            Location = new Point(20, 280),
            Size = new Size(420, 22)
        };

        _chkGateKsState = new CheckBox
        {
            Text = "Hardware KSSTATE_RUN Gate (open Host Mic only when Guest app records)",
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = Color.FromArgb(203, 213, 225),
            Checked = _audioEngine.GateMicOnGuestKsState,
            Location = new Point(22, 306),
            Size = new Size(420, 24)
        };
        _chkGateKsState.CheckedChanged += (_, _) =>
        {
            _audioEngine.GateMicOnGuestKsState = _chkGateKsState.Checked;
            UpdateUiState();
        };

        // --- Bottom Action Bar ---
        _btnToggleFullscreen = new Button
        {
            Text = "⛶  Toggle Borderless Fullscreen (Ctrl+Alt+Pause)",
            Font = bodyFont,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(30, 41, 59),
            ForeColor = Color.FromArgb(241, 245, 249),
            Location = new Point(20, 348),
            Size = new Size(280, 38)
        };
        _btnToggleFullscreen.FlatAppearance.BorderColor = Color.FromArgb(71, 85, 105);
        _btnToggleFullscreen.Click += (_, _) =>
        {
            Hide();
            RequestToggleFullscreen?.Invoke();
        };

        _btnDisconnect = new Button
        {
            Text = "Disconnect",
            Font = bodyFont,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(127, 29, 29),
            ForeColor = Color.White,
            Location = new Point(312, 348),
            Size = new Size(128, 38)
        };
        _btnDisconnect.FlatAppearance.BorderColor = Color.FromArgb(185, 28, 28);
        _btnDisconnect.Click += (_, _) => RequestDisconnect?.Invoke();

        var lblFooterHint = new Label
        {
            Text = "Press Esc or Ctrl+Alt+Shift+H to dismiss HUD",
            Font = new Font("Segoe UI", 8.0f),
            ForeColor = Color.FromArgb(100, 116, 139),
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(20, 398),
            Size = new Size(420, 18)
        };

        Controls.AddRange(new Control[]
        {
            _lblVmHeader,
            btnCloseHud,
            _lblTransportStatus,
            _lblBufferStats,
            lblSpeakerTitle,
            _btnSpeakerMute,
            _sliderVolume,
            _lblVolumeValue,
            lblMicTitle,
            _btnMicToggle,
            _lblMicSubstatus,
            _chkGateKsState,
            _btnToggleFullscreen,
            _btnDisconnect,
            lblFooterHint
        });

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 200 };
        _refreshTimer.Tick += (_, _) => UpdateUiState();
    }

    public void ShowCenteredOver(Form parentForm)
    {
        Rectangle b = parentForm.Bounds;
        Location = new Point(
            b.Left + (b.Width - Width) / 2,
            b.Top + Math.Max(40, (b.Height - Height) / 5)
        );

        UpdateUiState();
        _refreshTimer.Start();
        Show(parentForm);
        Activate();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible)
        {
            _refreshTimer.Stop();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            e.Handled = true;
            Hide();
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (Visible)
        {
            Hide();
        }
    }

    public void UpdateUiState()
    {
        if (IsDisposed) return;

        string audioProto = _audioTransport.IsUsingTcpFallback ? "TCP-Fallback" : "AF_HYPERV";
        string touchProto = _touchTransport.IsUsingTcpFallback ? "TCP-Fallback" : "AF_HYPERV";

        string audioStatus = _audioTransport.IsConnected
            ? $"Connected ({audioProto}, RTT {_audioTransport.LastRttMilliseconds:F2} ms)"
            : $"Waiting for Guest ({audioProto})...";

        string touchStatus = _touchTransport.IsConnected
            ? $"Connected ({touchProto}, Active Fingers: {_touchInterceptor.ActiveTouchPoints}/10)"
            : $"Waiting for Guest ({touchProto})...";

        _lblTransportStatus.Text =
            $"Audio Socket : {audioStatus}\n" +
            $"Touch Socket : {touchStatus}";

        var jb = _audioEngine.JitterBuffer;
        _lblBufferStats.Text =
            $"Jitter Buffer: {jb.BufferedMilliseconds:F1} ms (Target 25ms) | Drift: -{jb.TotalDroppedFramesForDrift} / +{jb.TotalDuplicatedFramesForDrift}";

        if (_audioEngine.SpeakerMuted)
        {
            _btnSpeakerMute.Text = "🔇 Muted";
            _btnSpeakerMute.BackColor = Color.FromArgb(127, 29, 29);
        }
        else
        {
            _btnSpeakerMute.Text = "🔊 Unmuted";
            _btnSpeakerMute.BackColor = Color.FromArgb(30, 41, 59);
        }

        // Microphone Privacy Button State
        if (!_audioEngine.IsMicUserEnabled)
        {
            _btnMicToggle.Text = "🔒  MICROPHONE OFF — PRIVACY LOCKED (Click to Enable)";
            _btnMicToggle.BackColor = Color.FromArgb(20, 83, 45);
            _btnMicToggle.ForeColor = Color.FromArgb(187, 247, 208);
            _btnMicToggle.FlatAppearance.BorderColor = Color.FromArgb(34, 197, 94);
            _lblMicSubstatus.Text = "Host WASAPI IAudioClient is CLOSED (Windows system tray mic icon OFF).";
        }
        else if (_audioEngine.IsMicHardwareOpen)
        {
            _btnMicToggle.Text = "🎙️  MICROPHONE LIVE — STREAMING TO GUEST VM (Click to Mute)";
            _btnMicToggle.BackColor = Color.FromArgb(153, 27, 27);
            _btnMicToggle.ForeColor = Color.White;
            _btnMicToggle.FlatAppearance.BorderColor = Color.FromArgb(248, 113, 113);
            _lblMicSubstatus.Text = "Host WASAPI IAudioClient is OPEN (48 kHz 16-bit PCM active).";
        }
        else
        {
            _btnMicToggle.Text = "🟡  MICROPHONE ARMED — WAITING FOR GUEST APP (Click to Lock)";
            _btnMicToggle.BackColor = Color.FromArgb(113, 63, 18);
            _btnMicToggle.ForeColor = Color.FromArgb(254, 240, 138);
            _btnMicToggle.FlatAppearance.BorderColor = Color.FromArgb(234, 179, 8);
            _lblMicSubstatus.Text = "Guest Mic Pin is idle (KSSTATE_STOP) -> Host IAudioClient remains closed.";
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var borderPen = new Pen(Color.FromArgb(56, 189, 248), 1.5f);
        e.Graphics.DrawRectangle(borderPen, 1, 1, Width - 2, Height - 2);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
