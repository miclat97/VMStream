// ============================================================================
// Program.cs - Entry Point & CLI Argument Parser for HvConsole.Host
// Supports:
//   HvConsole.Host.exe --vm "VM Name" [--fullscreen] [--tcp-fallback] [--audio-rate 44100|48000|auto]
//   HvConsole.Host.exe --guid "<GUID>" [--fullscreen] [--tcp-fallback] [--audio-rate 44100|48000|auto]
//   HvConsole.Host.exe (launches dark-mode Hyper-V VM Picker)
// ============================================================================
using System;
using System.Windows.Forms;
using HvConsole.Host.Audio;
using HvConsole.Host.Input;
using HvConsole.Host.Protocol;
using HvConsole.Host.UI;
using HvConsole.Host.Wmi;

namespace HvConsole.Host;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        string? targetVmNameOrGuid = null;
        bool startFullscreen = false;
        bool forceTcpFallback = false;
        AudioSampleRatePreference? audioRatePreference = null;
        DeviceRedirectionSettings deviceSettings = DeviceRedirectionSettings.Load();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if ((string.Equals(arg, "--vm", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(arg, "-v", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(arg, "--guid", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(arg, "-g", StringComparison.OrdinalIgnoreCase)) &&
                (i + 1) < args.Length)
            {
                targetVmNameOrGuid = args[++i];
            }
            else if (string.Equals(arg, "--fullscreen", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(arg, "-f", StringComparison.OrdinalIgnoreCase))
            {
                startFullscreen = true;
            }
            else if (string.Equals(arg, "--tcp-fallback", StringComparison.OrdinalIgnoreCase))
            {
                forceTcpFallback = true;
            }
            else if ((string.Equals(arg, "--audio-rate", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(arg, "--sample-rate", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(arg, "-r", StringComparison.OrdinalIgnoreCase)) &&
                     (i + 1) < args.Length)
            {
                if (AudioRateSettings.TryParse(args[++i], out AudioSampleRatePreference parsed))
                {
                    audioRatePreference = parsed;
                }
            }
            else if (arg.StartsWith("--audio-rate=", StringComparison.OrdinalIgnoreCase) ||
                     arg.StartsWith("--sample-rate=", StringComparison.OrdinalIgnoreCase))
            {
                int eqIdx = arg.IndexOf('=');
                if (eqIdx >= 0 && AudioRateSettings.TryParse(arg[(eqIdx + 1)..], out AudioSampleRatePreference parsed))
                {
                    audioRatePreference = parsed;
                }
            }
            else if (string.Equals(arg, "--no-touch", StringComparison.OrdinalIgnoreCase))
            {
                deviceSettings.EnableTouchscreen = false;
            }
            else if (string.Equals(arg, "--touch", StringComparison.OrdinalIgnoreCase))
            {
                deviceSettings.EnableTouchscreen = true;
            }
            else if (string.Equals(arg, "--no-hscroll", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(arg, "--no-touchpad-scroll", StringComparison.OrdinalIgnoreCase))
            {
                deviceSettings.EnableTouchpadHorizontalScroll = false;
            }
            else if (string.Equals(arg, "--hscroll", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(arg, "--touchpad-scroll", StringComparison.OrdinalIgnoreCase))
            {
                deviceSettings.EnableTouchpadHorizontalScroll = true;
            }
            else if (string.Equals(arg, "--no-speakers", StringComparison.OrdinalIgnoreCase))
            {
                deviceSettings.EnableSpeakers = false;
            }
            else if (string.Equals(arg, "--speakers", StringComparison.OrdinalIgnoreCase))
            {
                deviceSettings.EnableSpeakers = true;
            }
            else if (string.Equals(arg, "--no-mic", StringComparison.OrdinalIgnoreCase))
            {
                deviceSettings.EnableMicrophone = false;
            }
            else if (string.Equals(arg, "--mic", StringComparison.OrdinalIgnoreCase))
            {
                deviceSettings.EnableMicrophone = true;
            }
        }

        // Disable automatic mouse-in-pointer promotion so WM_POINTER touch events
        // intercepted on IHWindowClass never double-fire as mouse clicks.
        if (deviceSettings.EnableTouchscreen || string.IsNullOrWhiteSpace(targetVmNameOrGuid))
        {
            TouchWindowInterceptor.ConfigureProcessTouchSettings();
        }

        if (!string.IsNullOrWhiteSpace(targetVmNameOrGuid))
        {
            HyperVVmInfo? vm = null;
            try
            {
                vm = HyperVVmDiscovery.FindVmByNameOrGuid(targetVmNameOrGuid);
            }
            catch (Exception ex)
            {
                // If WMI query failed but a valid GUID was passed via --guid, allow direct connection
                if (Guid.TryParse(targetVmNameOrGuid.Trim('{', '}', ' '), out Guid directGuid))
                {
                    vm = new HyperVVmInfo
                    {
                        VmId = directGuid,
                        Name = directGuid.ToString("D"),
                        RawState = HyperVVmState.Running,
                        StateDisplay = "Running (Direct GUID)",
                        CpuCount = 4,
                        MemoryMb = 8192,
                        HasGpuPv = false,
                        Uptime = TimeSpan.Zero
                    };
                }
                else
                {
                    MessageBox.Show(
                        $"Failed to query Hyper-V WMI for '{targetVmNameOrGuid}':\n{ex.Message}",
                        "VMStream - WMI Discovery Error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error
                    );
                    return 1;
                }
            }

            if (vm == null)
            {
                MessageBox.Show(
                    $"Could not find a local Hyper-V virtual machine matching '{targetVmNameOrGuid}'.",
                    "VMStream - VM Not Found",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
                return 1;
            }

            if (!vm.IsRunning)
            {
                HyperVVmDiscovery.TryStartVirtualMachine(vm.VmId, out _);
            }

            Application.Run(new ConsoleSessionForm(vm, startFullscreen, forceTcpFallback, audioRatePreference, deviceSettings));
            return 0;
        }

        Application.Run(new VmPickerForm(startFullscreen, forceTcpFallback, audioRatePreference, deviceSettings));
        return 0;
    }
}
