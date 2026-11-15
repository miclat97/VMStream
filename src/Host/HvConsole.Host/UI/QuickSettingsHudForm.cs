// ============================================================================
// QuickSettingsHudForm.cs - Sleek Dark Frameless Floating Quick-Settings HUD
// Toggled via Ctrl + Alt + Shift + H (auto-dismissable with Esc).
// Displays VM telemetry, AF_HYPERV status, Speaker Volume/Mute, and the
// Privacy-Locked Host Microphone Toggle.
// ============================================================================
using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using HvConsole.Host.Audio;
using HvConsole.Host.Input;
using HvConsole.Host.Protocol;
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
    private readonly DeviceRedirectionSettings _deviceSettings;

    private readonly Label _lblVmHeader;
    private readonly Label _lblTransportStatus;
    private readonly Label _lblBufferStats;
    private readonly Label _lblSpeakerTitle;
    private readonly Label _lblVolumeValue;
    private readonly TrackBar _sliderVolume;
    private readonly Button _btnSpeakerMute;
    private readonly ComboBox _cmbSampleRate;
    private readonly Button _btnMicToggle;
    private readonly Label _lblMicSubstatus;
    private readonly CheckBox _chkGateKsState;
    private readonly CheckBox _chkTouchscreenRedir;
    private readonly CheckBox _chkTouchpadHScrollRedir;
    private readonly Button _btnToggleFullscreen;
    private readonly Button _btnDisconnect;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private bool _suppressEvents;

    public event Action? RequestToggleFullscreen;
    public event Action? RequestDisconnect;
    public event Action? RequestToggleMicrophone;
    public event Action? RequestTouchConfigurationRefresh;

    public QuickSettingsHudForm(
        HyperVVmInfo vmInfo,
        IHvTransport audioTransport,
        IHvTransport touchTransport,
        HostAudioEngine audioEngine,
        TouchWindowInterceptor touchInterceptor,
        DeviceRedirectionSettings? deviceSettings = null)
    {
        _vmInfo = vmInfo;
        _audioTransport = audioTransport;
        _touchTransport = touchTransport;
        _audioEngine = audioEngine;
        _touchInterceptor = touchInterceptor;
        _deviceSettings = deviceSettings ?? new DeviceRedirectionSettings();

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(15, 23, 42);
        ForeColor = Color.FromArgb(241, 245, 249);
        Size = new Size(460, 534);
        Opacity = 0.97;

        var headerFont = new Font("Segoe UI Semibold", 12f, FontStyle.Bold);
        var sectionFont = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
        var bodyFont = new Font("Segoe UI", 9.0f, FontStyle.Regular);
        var monoFont = new Font("Consolas", 8.5f, FontStyle.Regular);

        _lblVmHeader = new Label
        {
            Text = $"\u26A1 {_vmInfo.Name}   ({(_vmInfo.HasGpuPv ? "GPU-PV Accelerated" : "Synthetic Console")})",
            Font = headerFont,
            ForeColor = Color.FromArgb(56, 189, 248),
            Location = new Point(20, 18),
            Size = new Size(390, 26)
        };

        var btnCloseHud = new Button
        {
            Text = "\u2715",
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
        _lblSpeakerTitle = new Label
        {
            Text = "SPEAKER PLAYBACK (WaveRT -> Host WASAPI)",
            Font = sectionFont,
            ForeColor = Color.FromArgb(203, 213, 225),
            Location = new Point(20, 124),
            Size = new Size(420, 20)
        };

        _btnSpeakerMute = new Button
        {
            Text = "\U0001F50A Unmuted",
            Font = bodyFont,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(30, 41, 59),
            ForeColor = Color.White,
            Location = new Point(20, 150),
            Size = new Size(115, 34),
            Enabled = _deviceSettings.EnableSpeakers
        };
        _btnSpeakerMute.FlatAppearance.BorderColor = Color.FromArgb(71, 85, 105);
        _btnSpeakerMute.Click += (_, _) =>
        {
            _audioEngine.SpeakerMuted = !_audioEngine.SpeakerMuted;
            UpdateUiState();
        };

        _lblVolumeValue = new Label
        {
            Text = _deviceSettings.EnableSpeakers ? "100%" : "OFF",
            Font = sectionFont,
            ForeColor = Color.FromArgb(241, 245, 249),
            TextAlign = ContentAlignment.MiddleRight,
            Location = new Point(385, 154),
            Size = new Size(55, 24)
        };

        _sliderVolume = new TrackBar
        {
            Minimum = 0,
            Maximum = 100,
            TickFrequency = 10,
            Value = (int)Math.Round(_audioEngine.SpeakerVolume * 100),
            Location = new Point(145, 148),
            Size = new Size(235, 40),
            BackColor = Color.FromArgb(15, 23, 42),
            Enabled = _deviceSettings.EnableSpeakers
        };
        _sliderVolume.ValueChanged += (_, _) =>
        {
            _audioEngine.SpeakerVolume = _sliderVolume.Value / 100.0f;
            _lblVolumeValue.Text = $"{_sliderVolume.Value}%";
        };

        var lblSampleRate = new Label
        {
            Text = "Sample Rate (WASAPI):",
            Font = bodyFont,
            ForeColor = Color.FromArgb(148, 163, 184),
            Location = new Point(20, 197),
            Size = new Size(165, 22)
        };

        _cmbSampleRate = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            Font = bodyFont,
            BackColor = Color.FromArgb(30, 41, 59),
            ForeColor = Color.FromArgb(241, 245, 249),
            Location = new Point(195, 193),
            Size = new Size(245, 26),
            Enabled = _deviceSettings.AnyAudioChannelEnabled
        };
        _cmbSampleRate.Items.AddRange(new object[]
        {
            AudioRateSettings.FormatDisplay(AudioSampleRatePreference.Auto),
            AudioRateSettings.FormatDisplay(AudioSampleRatePreference.Hz44100),
            AudioRateSettings.FormatDisplay(AudioSampleRatePreference.Hz48000)
        });
        _cmbSampleRate.SelectedIndexChanged += (_, _) =>
        {
            if (_suppressEvents) return;
            AudioSampleRatePreference selected = _cmbSampleRate.SelectedIndex switch
            {
                1 => AudioSampleRatePreference.Hz44100,
                2 => AudioSampleRatePreference.Hz48000,
                _ => AudioSampleRatePreference.Auto
            };
            _audioEngine.SetSampleRatePreference(selected, persist: true);
            UpdateUiState();
        };

        // --- Microphone Privacy Section ---
        var lblMicTitle = new Label
        {
            Text = "HOST MICROPHONE PRIVACY LOCK (Ctrl+Alt+Shift+M)",
            Font = sectionFont,
            ForeColor = Color.FromArgb(203, 213, 225),
            Location = new Point(20, 238),
            Size = new Size(420, 20)
        };

        _btnMicToggle = new Button
        {
            Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
            FlatStyle = FlatStyle.Flat,
            Location = new Point(20, 264),
            Size = new Size(420, 44),
            Enabled = _deviceSettings.EnableMicrophone
        };
        _btnMicToggle.Click += (_, _) => RequestToggleMicrophone?.Invoke();

        _lblMicSubstatus = new Label
        {
            Font = bodyFont,
            ForeColor = Color.FromArgb(148, 163, 184),
            Location = new Point(20, 314),
            Size = new Size(420, 22)
        };

        _chkGateKsState = new CheckBox
        {
            Text = "Hardware KSSTATE_RUN Gate (open Host Mic only when Guest app records)",
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = Color.FromArgb(203, 213, 225),
            Checked = _audioEngine.GateMicOnGuestKsState,
            Location = new Point(22, 340),
            Size = new Size(420, 24),
            Enabled = _deviceSettings.EnableMicrophone
        };
        _chkGateKsState.CheckedChanged += (_, _) =>
        {
            _audioEngine.GateMicOnGuestKsState = _chkGateKsState.Checked;
            UpdateUiState();
        };

        // --- Input Redirection Section (Touch & Touchpad H-Scroll) ---
        var lblInputTitle = new Label
        {
            Text = "INPUT REDIRECTION (HvTouchHid AF_HYPERV)",
            Font = sectionFont,
            ForeColor = Color.FromArgb(203, 213, 225),
            Location = new Point(20, 376),
            Size = new Size(420, 20)
        };

        bool touchTransportActive = _deviceSettings.AnyTouchChannelEnabled;
        _chkTouchscreenRedir = new CheckBox
        {
            Text = "Touchscreen Digitizer",
            Font = bodyFont,
            ForeColor = Color.FromArgb(203, 213, 225),
            Checked = _touchInterceptor.TouchRedirectionEnabled,
            Location = new Point(22, 402),
            Size = new Size(195, 24),
            Enabled = touchTransportActive
        };
        _chkTouchscreenRedir.CheckedChanged += (_, _) =>
        {
            if (_suppressEvents) return;
            _touchInterceptor.SetTouchRedirectionEnabled(_chkTouchscreenRedir.Checked);
            if (_chkTouchscreenRedir.Checked)
            {
                RequestTouchConfigurationRefresh?.Invoke();
            }
            UpdateUiState();
        };

        _chkTouchpadHScrollRedir = new CheckBox
        {
            Text = "Touchpad H-Scroll (Left/Right)",
            Font = bodyFont,
            ForeColor = Color.FromArgb(203, 213, 225),
            Checked = _touchInterceptor.TouchpadHorizontalScrollEnabled,
            Location = new Point(222, 402),
            Size = new Size(218, 24),
            Enabled = touchTransportActive
        };
        _chkTouchpadHScrollRedir.CheckedChanged += (_, _) =>
        {
            if (_suppressEvents) return;
            _touchInterceptor.TouchpadHorizontalScrollEnabled = _chkTouchpadHScrollRedir.Checked;
            UpdateUiState();
        };

        // --- Bottom Action Bar ---
        _btnToggleFullscreen = new Button
        {
            Text = "\u26F6  Toggle Borderless Fullscreen (Ctrl+Alt+Pause)",
            Font = bodyFont,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(30, 41, 59),
            ForeColor = Color.FromArgb(241, 245, 249),
            Location = new Point(20, 444),
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
            Location = new Point(312, 444),
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
            Location = new Point(20, 496),
            Size = new Size(420, 18)
        };

        Controls.AddRange(new Control[]
        {
            _lblVmHeader,
            btnCloseHud,
            _lblTransportStatus,
            _lblBufferStats,
            _lblSpeakerTitle,
            _btnSpeakerMute,
            _sliderVolume,
            _lblVolumeValue,
            lblSampleRate,
            _cmbSampleRate,
            lblMicTitle,
            _btnMicToggle,
            _lblMicSubstatus,
            _chkGateKsState,
            lblInputTitle,
            _chkTouchscreenRedir,
            _chkTouchpadHScrollRedir,
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

        string audioStatus = !_deviceSettings.AnyAudioChannelEnabled
            ? "Disabled in Launcher Settings"
            : (_audioTransport.IsConnected
                ? $"Connected ({audioProto}, RTT {_audioTransport.LastRttMilliseconds:F2} ms)"
                : $"Waiting for Guest ({audioProto})...");

        string touchStatus = !_deviceSettings.AnyTouchChannelEnabled
            ? "Disabled in Launcher Settings"
            : (_touchTransport.IsConnected
                ? $"Connected ({touchProto}, Touch:{_touchInterceptor.ActiveTouchPoints}/10, H-Scroll:{_touchInterceptor.ForwardedHorizontalScrollCount})"
                : $"Waiting for Guest ({touchProto})...");

        _lblTransportStatus.Text =
            $"Audio Socket : {audioStatus}\n" +
            $"Input Socket : {touchStatus}";

        if (!_deviceSettings.EnableSpeakers)
        {
            _lblSpeakerTitle.Text = "SPEAKER PLAYBACK (Disabled in Launcher)";
            _lblBufferStats.Text = "Speaker redirection is disabled for this session.";
            _btnSpeakerMute.Text = "Disabled";
            _btnSpeakerMute.BackColor = Color.FromArgb(30, 41, 59);
        }
        else
        {
            double guestRateKhz = _audioEngine.ActiveGuestRenderSampleRate / 1000.0;
            double hostRateKhz = _audioEngine.ActiveRenderSampleRate / 1000.0;
            string fmtTag = _audioEngine.RenderUsesFloat32 ? "Float32" : "PCM16";
            string drvTag = _audioEngine.RenderUsesEventCallback ? "Event" : "Poll";
            _lblSpeakerTitle.Text = string.Format(
                CultureInfo.InvariantCulture,
                "SPEAKER ({0:0.#} kHz -> {1:0.#} kHz {2}/{3})",
                guestRateKhz,
                hostRateKhz,
                fmtTag,
                drvTag
            );

            var jb = _audioEngine.JitterBuffer;
            _lblBufferStats.Text =
                $"Buffer: {jb.BufferedMilliseconds:F1}ms (T:22ms) | Drift:-{jb.TotalDroppedFramesForDrift}/+{jb.TotalDuplicatedFramesForDrift} | XRun:{jb.TotalUnderruns}";

            if (_audioEngine.SpeakerMuted)
            {
                _btnSpeakerMute.Text = "\U0001F507 Muted";
                _btnSpeakerMute.BackColor = Color.FromArgb(127, 29, 29);
            }
            else
            {
                _btnSpeakerMute.Text = "\U0001F50A Unmuted";
                _btnSpeakerMute.BackColor = Color.FromArgb(30, 41, 59);
            }
        }

        if (!_cmbSampleRate.DroppedDown)
        {
            int desiredIndex = _audioEngine.SampleRatePreference switch
            {
                AudioSampleRatePreference.Hz44100 => 1,
                AudioSampleRatePreference.Hz48000 => 2,
                _ => 0
            };
            if (_cmbSampleRate.SelectedIndex != desiredIndex)
            {
                _suppressEvents = true;
                try { _cmbSampleRate.SelectedIndex = desiredIndex; }
                finally { _suppressEvents = false; }
            }
        }

        _suppressEvents = true;
        try
        {
            if (_chkTouchscreenRedir.Checked != _touchInterceptor.TouchRedirectionEnabled)
            {
                _chkTouchscreenRedir.Checked = _touchInterceptor.TouchRedirectionEnabled;
            }
            if (_chkTouchpadHScrollRedir.Checked != _touchInterceptor.TouchpadHorizontalScrollEnabled)
            {
                _chkTouchpadHScrollRedir.Checked = _touchInterceptor.TouchpadHorizontalScrollEnabled;
            }
        }
        finally
        {
            _suppressEvents = false;
        }

        // Microphone Privacy Button State
        if (!_deviceSettings.EnableMicrophone)
        {
            _btnMicToggle.Text = "\U0001F512  MICROPHONE DISABLED IN LAUNCHER SETTINGS";
            _btnMicToggle.BackColor = Color.FromArgb(30, 41, 59);
            _btnMicToggle.ForeColor = Color.FromArgb(148, 163, 184);
            _btnMicToggle.FlatAppearance.BorderColor = Color.FromArgb(71, 85, 105);
            _lblMicSubstatus.Text = "Enable Microphone in VM Launcher settings to allow Ctrl+Alt+Shift+M.";
        }
        else if (!_audioEngine.IsMicUserEnabled)
        {
            _btnMicToggle.Text = "\U0001F512  MICROPHONE OFF - PRIVACY LOCKED (Click to Enable)";
            _btnMicToggle.BackColor = Color.FromArgb(20, 83, 45);
            _btnMicToggle.ForeColor = Color.FromArgb(187, 247, 208);
            _btnMicToggle.FlatAppearance.BorderColor = Color.FromArgb(34, 197, 94);
            _lblMicSubstatus.Text = "Host WASAPI IAudioClient is CLOSED (Windows system tray mic icon OFF).";
        }
        else if (_audioEngine.IsMicHardwareOpen)
        {
            double captureKhz = _audioEngine.TargetCaptureSampleRate / 1000.0;
            _btnMicToggle.Text = "\U0001F399\uFE0F  MICROPHONE LIVE - STREAMING TO GUEST VM (Click to Mute)";
            _btnMicToggle.BackColor = Color.FromArgb(153, 27, 27);
            _btnMicToggle.ForeColor = Color.White;
            _btnMicToggle.FlatAppearance.BorderColor = Color.FromArgb(248, 113, 113);
            _lblMicSubstatus.Text = string.Format(
                CultureInfo.InvariantCulture,
                "Host WASAPI IAudioClient is OPEN ({0:0.#} kHz 16-bit PCM active).",
                captureKhz
            );
        }
        else
        {
            _btnMicToggle.Text = "\U0001F7E1  MICROPHONE ARMED - WAITING FOR GUEST APP (Click to Lock)";
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
