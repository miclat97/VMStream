// ============================================================================
// VmPickerForm.cs - Sleek Dark-Mode Local Hyper-V Virtual Machine Launcher
// Enumerates VMs via WMI root\virtualization\v2 (State, CPU, RAM, GPU-PV).
// Supports starting stopped VMs and opening multiple concurrent isolated
// Console Session windows.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using HvConsole.Host.Audio;
using HvConsole.Host.Protocol;
using HvConsole.Host.Wmi;

namespace HvConsole.Host.UI;

public sealed class VmPickerForm : Form
{
    private readonly ListView _vmListView;
    private readonly Label _lblStatus;
    private readonly CheckBox _chkStartFullscreen;
    private readonly CheckBox _chkTcpFallback;
    private readonly ComboBox _cmbSampleRate;
    private readonly CheckBox _chkRedirectTouchscreen;
    private readonly CheckBox _chkRedirectTouchpadHScroll;
    private readonly CheckBox _chkRedirectSpeakers;
    private readonly CheckBox _chkRedirectMicrophone;
    private readonly Button _btnConnect;
    private readonly Button _btnStartVm;
    private readonly Button _btnRefresh;
    private readonly ToolTip _toolTip;
    private readonly System.Windows.Forms.Timer _autoRefreshTimer;
    private readonly DeviceRedirectionSettings _deviceSettings;

    private IReadOnlyList<HyperVVmInfo> _discoveredVms = Array.Empty<HyperVVmInfo>();
    private bool _isRefreshing;
    private bool _lastRefreshHadError;
    private string _lastIdleStatusText = string.Empty;

    public VmPickerForm(
        bool defaultFullscreen = false,
        bool defaultTcpFallback = false,
        AudioSampleRatePreference? initialSampleRate = null,
        DeviceRedirectionSettings? initialDeviceSettings = null)
    {
        _deviceSettings = (initialDeviceSettings ?? DeviceRedirectionSettings.Load()).Clone();

        Text = "VMStream - Zero-Artifact Hyper-V Console Client";
        ClientSize = new Size(940, 580);
        MinimumSize = new Size(860, 480);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(15, 23, 42);
        ForeColor = Color.FromArgb(241, 245, 249);
        Font = new Font("Segoe UI", 9.5f);

        _toolTip = new ToolTip
        {
            AutoPopDelay = 8000,
            InitialDelay = 350,
            ReshowDelay = 200
        };

        var headerPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 76,
            BackColor = Color.FromArgb(10, 15, 29),
            Padding = new Padding(24, 14, 24, 10)
        };

        var lblTitle = new Label
        {
            Text = "\u26A1 VMStream - Hyper-V Console Launcher",
            Font = new Font("Segoe UI Semibold", 15f, FontStyle.Bold),
            ForeColor = Color.FromArgb(56, 189, 248),
            AutoSize = true,
            Location = new Point(22, 12)
        };

        var lblSubtitle = new Label
        {
            Text = "Strict Console Session (Port 2179 / GPU-PV)  \u2022  AF_HYPERV 44.1 / 48 kHz WaveRT Audio  \u2022  Multi-Touch HID & H-Scroll",
            Font = new Font("Segoe UI", 9.0f),
            ForeColor = Color.FromArgb(148, 163, 184),
            AutoSize = true,
            Location = new Point(24, 44)
        };

        headerPanel.Controls.Add(lblTitle);
        headerPanel.Controls.Add(lblSubtitle);

        _vmListView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            GridLines = false,
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(15, 23, 42),
            ForeColor = Color.FromArgb(241, 245, 249),
            Font = new Font("Segoe UI", 10.0f)
        };

        _vmListView.Columns.Add("Virtual Machine", 220);
        _vmListView.Columns.Add("State", 110);
        _vmListView.Columns.Add("vCPU", 70);
        _vmListView.Columns.Add("RAM", 100);
        _vmListView.Columns.Add("GPU-PV", 110);
        _vmListView.Columns.Add("Uptime", 110);
        _vmListView.Columns.Add("VM GUID (PCB)", 260);

        _vmListView.SelectedIndexChanged += (_, _) => UpdateActionButtons();
        _vmListView.DoubleClick += (_, _) => ConnectSelectedVm();

        var bottomPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 132,
            Width = ClientSize.Width,
            BackColor = Color.FromArgb(10, 15, 29)
        };

        // --- Row 1: Display, Transport & Audio Sample Rate ---
        _chkStartFullscreen = new CheckBox
        {
            Text = "Launch in Borderless Fullscreen (Ctrl+Alt+Pause)",
            Checked = defaultFullscreen,
            ForeColor = Color.FromArgb(203, 213, 225),
            AutoSize = true,
            Location = new Point(22, 12)
        };

        _chkTcpFallback = new CheckBox
        {
            Text = "TCP Fallback Mode",
            Checked = defaultTcpFallback,
            ForeColor = Color.FromArgb(148, 163, 184),
            AutoSize = true,
            Location = new Point(365, 12)
        };

        var lblAudioRate = new Label
        {
            Text = "Audio:",
            ForeColor = Color.FromArgb(148, 163, 184),
            AutoSize = true,
            Location = new Point(525, 13)
        };

        AudioSampleRatePreference effectiveRate = initialSampleRate ?? AudioRateSettings.LoadSavedPreference();
        _cmbSampleRate = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.0f),
            BackColor = Color.FromArgb(30, 41, 59),
            ForeColor = Color.FromArgb(241, 245, 249),
            Location = new Point(575, 9),
            Size = new Size(195, 25)
        };
        _cmbSampleRate.Items.AddRange(new object[]
        {
            AudioRateSettings.FormatDisplay(AudioSampleRatePreference.Auto),
            AudioRateSettings.FormatDisplay(AudioSampleRatePreference.Hz44100),
            AudioRateSettings.FormatDisplay(AudioSampleRatePreference.Hz48000)
        });
        _cmbSampleRate.SelectedIndex = effectiveRate switch
        {
            AudioSampleRatePreference.Hz44100 => 1,
            AudioSampleRatePreference.Hz48000 => 2,
            _ => 0
        };
        _cmbSampleRate.SelectedIndexChanged += (_, _) => AudioRateSettings.SavePreference(GetSelectedSampleRate());

        // --- Row 2: Device Redirection Configuration ---
        var lblRedirect = new Label
        {
            Text = "Redirect:",
            ForeColor = Color.FromArgb(56, 189, 248),
            Font = new Font("Segoe UI Semibold", 9.0f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(22, 46)
        };

        _chkRedirectTouchscreen = new CheckBox
        {
            Text = "Touchscreen",
            Checked = _deviceSettings.EnableTouchscreen,
            ForeColor = Color.FromArgb(203, 213, 225),
            AutoSize = true,
            Location = new Point(98, 45)
        };
        _toolTip.SetToolTip(
            _chkRedirectTouchscreen,
            "Forward Host touchscreen contacts over AF_HYPERV to HvTouchHid (disable on laptops without a touchscreen)."
        );

        _chkRedirectTouchpadHScroll = new CheckBox
        {
            Text = "Touchpad H-Scroll",
            Checked = _deviceSettings.EnableTouchpadHorizontalScroll,
            ForeColor = Color.FromArgb(203, 213, 225),
            AutoSize = true,
            Location = new Point(226, 45)
        };
        _toolTip.SetToolTip(
            _chkRedirectTouchpadHScroll,
            "Forward left/right touchpad scroll (WM_MOUSEHWHEEL) natively to the VM while keeping Host 4-finger desktop & Win+Tab gestures free."
        );

        _chkRedirectSpeakers = new CheckBox
        {
            Text = "Speakers",
            Checked = _deviceSettings.EnableSpeakers,
            ForeColor = Color.FromArgb(203, 213, 225),
            AutoSize = true,
            Location = new Point(392, 45)
        };
        _toolTip.SetToolTip(
            _chkRedirectSpeakers,
            "Forward Guest VM audio playback (WaveRT Speakers) to Host WASAPI output."
        );

        _chkRedirectMicrophone = new CheckBox
        {
            Text = "Microphone (Privacy Locked)",
            Checked = _deviceSettings.EnableMicrophone,
            ForeColor = Color.FromArgb(203, 213, 225),
            AutoSize = true,
            Location = new Point(498, 45)
        };
        _toolTip.SetToolTip(
            _chkRedirectMicrophone,
            "Allow enabling Host Microphone forwarding during a session via Ctrl+Alt+Shift+M (always starts muted/locked)."
        );

        void OnRedirectionSettingChanged(object? sender, EventArgs args)
        {
            _deviceSettings.EnableTouchscreen = _chkRedirectTouchscreen.Checked;
            _deviceSettings.EnableTouchpadHorizontalScroll = _chkRedirectTouchpadHScroll.Checked;
            _deviceSettings.EnableSpeakers = _chkRedirectSpeakers.Checked;
            _deviceSettings.EnableMicrophone = _chkRedirectMicrophone.Checked;
            DeviceRedirectionSettings.Save(_deviceSettings);
        }

        _chkRedirectTouchscreen.CheckedChanged += OnRedirectionSettingChanged;
        _chkRedirectTouchpadHScroll.CheckedChanged += OnRedirectionSettingChanged;
        _chkRedirectSpeakers.CheckedChanged += OnRedirectionSettingChanged;
        _chkRedirectMicrophone.CheckedChanged += OnRedirectionSettingChanged;

        // --- Row 3: Status & Action Buttons ---
        _lblStatus = new Label
        {
            Text = "Ready.",
            ForeColor = Color.FromArgb(148, 163, 184),
            AutoSize = false,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Location = new Point(22, 86),
            Size = new Size(496, 28),
            TextAlign = ContentAlignment.MiddleLeft
        };

        _btnRefresh = new Button
        {
            Text = "\u21BB Refresh",
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(30, 41, 59),
            ForeColor = Color.FromArgb(226, 232, 240),
            Size = new Size(110, 38),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            Location = new Point(528, 80)
        };
        _btnRefresh.FlatAppearance.BorderColor = Color.FromArgb(71, 85, 105);
        _btnRefresh.Click += (_, _) => _ = RefreshVmListAsync(isBackgroundAutoRefresh: false);

        _btnStartVm = new Button
        {
            Text = "\u25B6 Start VM",
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(30, 41, 59),
            ForeColor = Color.FromArgb(226, 232, 240),
            Size = new Size(120, 38),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            Location = new Point(648, 80),
            Enabled = false
        };
        _btnStartVm.FlatAppearance.BorderColor = Color.FromArgb(71, 85, 105);
        _btnStartVm.Click += (_, _) => StartSelectedVm();

        _btnConnect = new Button
        {
            Text = "Connect Console",
            Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(2, 132, 199),
            ForeColor = Color.White,
            Size = new Size(140, 38),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            Location = new Point(778, 80),
            Enabled = false
        };
        _btnConnect.FlatAppearance.BorderColor = Color.FromArgb(56, 189, 248);
        _btnConnect.Click += (_, _) => ConnectSelectedVm();

        bottomPanel.Controls.AddRange(new Control[]
        {
            _chkStartFullscreen,
            _chkTcpFallback,
            lblAudioRate,
            _cmbSampleRate,
            lblRedirect,
            _chkRedirectTouchscreen,
            _chkRedirectTouchpadHScroll,
            _chkRedirectSpeakers,
            _chkRedirectMicrophone,
            _lblStatus,
            _btnRefresh,
            _btnStartVm,
            _btnConnect
        });

        var listContainer = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 12, 20, 12),
            BackColor = Color.FromArgb(15, 23, 42)
        };
        listContainer.Controls.Add(_vmListView);

        Controls.Add(listContainer);
        Controls.Add(bottomPanel);
        Controls.Add(headerPanel);

        _autoRefreshTimer = new System.Windows.Forms.Timer { Interval = 2500 };
        _autoRefreshTimer.Tick += (_, _) =>
        {
            if (Visible && WindowState != FormWindowState.Minimized)
            {
                _ = RefreshVmListAsync(isBackgroundAutoRefresh: true);
            }
        };

        Activated += (_, _) =>
        {
            if (IsHandleCreated)
            {
                _ = RefreshVmListAsync(isBackgroundAutoRefresh: true);
            }
        };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _ = RefreshVmListAsync(isBackgroundAutoRefresh: false);
        _autoRefreshTimer.Start();
    }

    private async Task RefreshVmListAsync(bool isBackgroundAutoRefresh)
    {
        if (_isRefreshing || IsDisposed) return;
        _isRefreshing = true;

        try
        {
            if (!isBackgroundAutoRefresh)
            {
                _lblStatus.ForeColor = Color.FromArgb(148, 163, 184);
                _lblStatus.Text = "Querying WMI root\\virtualization\\v2 -> Msvm_ComputerSystem...";
            }

            IReadOnlyList<HyperVVmInfo> vms = await Task.Run(HyperVVmDiscovery.DiscoverLocalVirtualMachines);
            if (IsDisposed) return;

            ApplyVmListToUi(vms, isBackgroundAutoRefresh);
            _lastRefreshHadError = false;
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                _lastRefreshHadError = true;
                _lblStatus.ForeColor = Color.FromArgb(248, 113, 113);
                _lblStatus.Text = $"WMI Query Error: {ex.Message} (Ensure Hyper-V is enabled & user is in Hyper-V Administrators).";
            }
        }
        finally
        {
            _isRefreshing = false;
            if (!IsDisposed)
            {
                UpdateActionButtons();
            }
        }
    }

    private void ApplyVmListToUi(IReadOnlyList<HyperVVmInfo> vms, bool isBackgroundAutoRefresh)
    {
        Guid? selectedVmId = GetSelectedVm()?.VmId;
        _discoveredVms = vms;

        bool canUpdateInPlace = _vmListView.Items.Count == vms.Count;
        if (canUpdateInPlace)
        {
            for (int i = 0; i < vms.Count; i++)
            {
                if (_vmListView.Items[i].Tag is not HyperVVmInfo existing || existing.VmId != vms[i].VmId)
                {
                    canUpdateInPlace = false;
                    break;
                }
            }
        }

        int runningCount = 0;
        foreach (HyperVVmInfo vm in vms)
        {
            if (vm.IsRunning) runningCount++;
        }

        if (canUpdateInPlace)
        {
            for (int i = 0; i < vms.Count; i++)
            {
                HyperVVmInfo vm = vms[i];
                ListViewItem item = _vmListView.Items[i];
                item.Tag = vm;

                Color targetColor = vm.IsRunning
                    ? Color.FromArgb(74, 222, 128)
                    : Color.FromArgb(203, 213, 225);

                if (item.ForeColor != targetColor)
                {
                    item.ForeColor = targetColor;
                }

                string uptimeText = vm.IsRunning
                    ? $"{(int)vm.Uptime.TotalHours:D2}:{vm.Uptime.Minutes:D2}:{vm.Uptime.Seconds:D2}"
                    : "-";

                if (item.Text != vm.Name) item.Text = vm.Name;
                if (item.SubItems[1].Text != vm.StateDisplay) item.SubItems[1].Text = vm.StateDisplay;
                string cpuText = $"{vm.CpuCount} vCPU";
                if (item.SubItems[2].Text != cpuText) item.SubItems[2].Text = cpuText;
                string memText = $"{vm.MemoryMb:N0} MB";
                if (item.SubItems[3].Text != memText) item.SubItems[3].Text = memText;
                string gpuText = vm.HasGpuPv ? "Yes (GPU-PV)" : "Synthetic";
                if (item.SubItems[4].Text != gpuText) item.SubItems[4].Text = gpuText;
                if (item.SubItems[5].Text != uptimeText) item.SubItems[5].Text = uptimeText;
                if (item.SubItems[6].Text != vm.PcbGuidString) item.SubItems[6].Text = vm.PcbGuidString;
            }
        }
        else
        {
            _vmListView.BeginUpdate();
            try
            {
                _vmListView.Items.Clear();
                ListViewItem? itemToSelect = null;

                foreach (HyperVVmInfo vm in vms)
                {
                    var item = new ListViewItem(vm.Name)
                    {
                        Tag = vm,
                        ForeColor = vm.IsRunning
                            ? Color.FromArgb(74, 222, 128)
                            : Color.FromArgb(203, 213, 225)
                    };

                    item.SubItems.Add(vm.StateDisplay);
                    item.SubItems.Add($"{vm.CpuCount} vCPU");
                    item.SubItems.Add($"{vm.MemoryMb:N0} MB");
                    item.SubItems.Add(vm.HasGpuPv ? "Yes (GPU-PV)" : "Synthetic");
                    item.SubItems.Add(vm.IsRunning ? $"{(int)vm.Uptime.TotalHours:D2}:{vm.Uptime.Minutes:D2}:{vm.Uptime.Seconds:D2}" : "-");
                    item.SubItems.Add(vm.PcbGuidString);

                    _vmListView.Items.Add(item);

                    if (selectedVmId.HasValue && vm.VmId == selectedVmId.Value)
                    {
                        itemToSelect = item;
                    }
                }

                if (itemToSelect != null)
                {
                    itemToSelect.Selected = true;
                }
                else if (_vmListView.Items.Count > 0)
                {
                    _vmListView.Items[0].Selected = true;
                }
            }
            finally
            {
                _vmListView.EndUpdate();
            }
        }

        string statusText = $"Found {vms.Count} Hyper-V VM(s) ({runningCount} running)  \u2022  Auto-refresh: 2.5s  \u2022  Host Mic defaults to OFF.";
        if (!isBackgroundAutoRefresh || _lastRefreshHadError || _lastIdleStatusText != statusText)
        {
            _lblStatus.ForeColor = Color.FromArgb(148, 163, 184);
            _lblStatus.Text = statusText;
            _lastIdleStatusText = statusText;
        }
    }

    private AudioSampleRatePreference GetSelectedSampleRate() => _cmbSampleRate.SelectedIndex switch
    {
        1 => AudioSampleRatePreference.Hz44100,
        2 => AudioSampleRatePreference.Hz48000,
        _ => AudioSampleRatePreference.Auto
    };

    private HyperVVmInfo? GetSelectedVm()
    {
        if (_vmListView.SelectedItems.Count == 0) return null;
        return _vmListView.SelectedItems[0].Tag as HyperVVmInfo;
    }

    private void UpdateActionButtons()
    {
        HyperVVmInfo? selected = GetSelectedVm();
        _btnConnect.Enabled = selected != null;
        _btnStartVm.Enabled = selected is { IsRunning: false };
    }

    private void StartSelectedVm()
    {
        HyperVVmInfo? vm = GetSelectedVm();
        if (vm == null) return;

        _lblStatus.ForeColor = Color.FromArgb(56, 189, 248);
        _lblStatus.Text = $"Starting virtual machine '{vm.Name}'...";
        Application.DoEvents();

        if (HyperVVmDiscovery.TryStartVirtualMachine(vm.VmId, out string? error))
        {
            _ = RefreshVmListAsync(isBackgroundAutoRefresh: false);
            _lblStatus.ForeColor = Color.FromArgb(74, 222, 128);
            _lblStatus.Text = $"Started '{vm.Name}'.";
        }
        else
        {
            _lblStatus.ForeColor = Color.FromArgb(248, 113, 113);
            _lblStatus.Text = $"Failed to start '{vm.Name}': {error}";
        }
    }

    private void ConnectSelectedVm()
    {
        HyperVVmInfo? vm = GetSelectedVm();
        if (vm == null) return;

        if (!vm.IsRunning)
        {
            DialogResult dr = MessageBox.Show(
                this,
                $"Virtual machine '{vm.Name}' is currently {vm.StateDisplay}.\nWould you like to start it now before connecting?",
                "VMStream - Start Virtual Machine",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question
            );

            if (dr == DialogResult.Cancel) return;
            if (dr == DialogResult.Yes)
            {
                if (!HyperVVmDiscovery.TryStartVirtualMachine(vm.VmId, out string? err))
                {
                    MessageBox.Show(this, $"Could not start VM: {err}", "VMStream Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                _ = RefreshVmListAsync(isBackgroundAutoRefresh: false);
            }
        }

        var sessionForm = new ConsoleSessionForm(
            vm,
            startInFullscreen: _chkStartFullscreen.Checked,
            forceTcpFallback: _chkTcpFallback.Checked,
            sampleRatePreference: GetSelectedSampleRate(),
            deviceSettings: _deviceSettings.Clone()
        );
        sessionForm.FormClosed += (_, _) =>
        {
            if (!IsDisposed && IsHandleCreated)
            {
                BeginInvoke(() => _ = RefreshVmListAsync(isBackgroundAutoRefresh: true));
            }
        };
        sessionForm.Show();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _autoRefreshTimer.Stop();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _autoRefreshTimer.Dispose();
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
    }
}
