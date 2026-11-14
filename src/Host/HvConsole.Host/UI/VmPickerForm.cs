// ============================================================================
// VmPickerForm.cs — Sleek Dark-Mode Local Hyper-V Virtual Machine Launcher
// Enumerates VMs via WMI root\virtualization\v2 (State, CPU, RAM, GPU-PV).
// Supports starting stopped VMs and opening multiple concurrent isolated
// Console Session windows.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using HvConsole.Host.Wmi;

namespace HvConsole.Host.UI;

public sealed class VmPickerForm : Form
{
    private readonly ListView _vmListView;
    private readonly Label _lblStatus;
    private readonly CheckBox _chkStartFullscreen;
    private readonly CheckBox _chkTcpFallback;
    private readonly Button _btnConnect;
    private readonly Button _btnStartVm;
    private readonly Button _btnRefresh;

    private IReadOnlyList<HyperVVmInfo> _discoveredVms = Array.Empty<HyperVVmInfo>();

    public VmPickerForm(bool defaultFullscreen = false, bool defaultTcpFallback = false)
    {
        Text = "VMStream — Zero-Artifact Hyper-V Console Client";
        Size = new Size(920, 560);
        MinimumSize = new Size(780, 460);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(15, 23, 42);
        ForeColor = Color.FromArgb(241, 245, 249);
        Font = new Font("Segoe UI", 9.5f);

        var headerPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 76,
            BackColor = Color.FromArgb(10, 15, 29),
            Padding = new Padding(24, 14, 24, 10)
        };

        var lblTitle = new Label
        {
            Text = "⚡ VMStream — Hyper-V Console Launcher",
            Font = new Font("Segoe UI Semibold", 15f, FontStyle.Bold),
            ForeColor = Color.FromArgb(56, 189, 248),
            AutoSize = true,
            Location = new Point(22, 12)
        };

        var lblSubtitle = new Label
        {
            Text = "Strict Console Session (Port 2179 / GPU-PV)  •  AF_HYPERV 48 kHz WaveRT Audio  •  10-Point VHF Multi-Touch Digitizer",
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
            Height = 92,
            BackColor = Color.FromArgb(10, 15, 29)
        };

        _chkStartFullscreen = new CheckBox
        {
            Text = "Launch in Borderless Fullscreen (Ctrl+Alt+Pause)",
            Checked = defaultFullscreen,
            ForeColor = Color.FromArgb(203, 213, 225),
            AutoSize = true,
            Location = new Point(22, 14)
        };

        _chkTcpFallback = new CheckBox
        {
            Text = "TCP Fallback Mode",
            Checked = defaultTcpFallback,
            ForeColor = Color.FromArgb(148, 163, 184),
            AutoSize = true,
            Location = new Point(380, 14)
        };

        _lblStatus = new Label
        {
            Text = "Ready.",
            ForeColor = Color.FromArgb(148, 163, 184),
            AutoSize = false,
            Location = new Point(22, 48),
            Size = new Size(440, 28),
            TextAlign = ContentAlignment.MiddleLeft
        };

        _btnRefresh = new Button
        {
            Text = "↻ Refresh",
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(30, 41, 59),
            ForeColor = Color.FromArgb(226, 232, 240),
            Size = new Size(110, 38),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            Location = new Point(510, 36)
        };
        _btnRefresh.FlatAppearance.BorderColor = Color.FromArgb(71, 85, 105);
        _btnRefresh.Click += (_, _) => RefreshVmList();

        _btnStartVm = new Button
        {
            Text = "▶ Start VM",
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(30, 41, 59),
            ForeColor = Color.FromArgb(226, 232, 240),
            Size = new Size(120, 38),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            Location = new Point(630, 36),
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
            Location = new Point(760, 36),
            Enabled = false
        };
        _btnConnect.FlatAppearance.BorderColor = Color.FromArgb(56, 189, 248);
        _btnConnect.Click += (_, _) => ConnectSelectedVm();

        bottomPanel.Controls.AddRange(new Control[]
        {
            _chkStartFullscreen,
            _chkTcpFallback,
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
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        RefreshVmList();
    }

    private void RefreshVmList()
    {
        try
        {
            _lblStatus.ForeColor = Color.FromArgb(148, 163, 184);
            _lblStatus.Text = "Querying WMI root\\virtualization\\v2 -> Msvm_ComputerSystem...";
            _vmListView.BeginUpdate();
            _vmListView.Items.Clear();

            _discoveredVms = HyperVVmDiscovery.DiscoverLocalVirtualMachines();

            foreach (HyperVVmInfo vm in _discoveredVms)
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
                item.SubItems.Add(vm.IsRunning ? $"{(int)vm.Uptime.TotalHours:D2}:{vm.Uptime.Minutes:D2}:{vm.Uptime.Seconds:D2}" : "—");
                item.SubItems.Add(vm.PcbGuidString);

                _vmListView.Items.Add(item);
            }

            if (_vmListView.Items.Count > 0)
            {
                _vmListView.Items[0].Selected = true;
            }

            _lblStatus.Text = $"Found {_discoveredVms.Count} Hyper-V virtual machine(s). Host Microphone defaults to OFF.";
        }
        catch (Exception ex)
        {
            _lblStatus.ForeColor = Color.FromArgb(248, 113, 113);
            _lblStatus.Text = $"WMI Query Error: {ex.Message} (Ensure Hyper-V is enabled & user is in Hyper-V Administrators).";
        }
        finally
        {
            _vmListView.EndUpdate();
            UpdateActionButtons();
        }
    }

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
            RefreshVmList();
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
                "VMStream — Start Virtual Machine",
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
                RefreshVmList();
            }
        }

        var sessionForm = new ConsoleSessionForm(
            vm,
            startInFullscreen: _chkStartFullscreen.Checked,
            forceTcpFallback: _chkTcpFallback.Checked
        );
        sessionForm.Show();
    }
}
