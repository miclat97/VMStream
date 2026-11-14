// ============================================================================
// HyperVVmDiscovery.cs - Hyper-V WMI v2 VM Enumeration & Control
// Queries root\virtualization\v2 -> Msvm_ComputerSystem, CPU, RAM, and GPU-PV
// Project: VMStream - Host Console Client (HvConsole.Host)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;

namespace HvConsole.Host.Wmi;

public enum HyperVVmState : ushort
{
    Unknown   = 0,
    Running   = 2,
    Off       = 3,
    Stopping  = 4,
    Saved     = 6,
    Paused    = 9,
    Starting  = 10,
    Reset     = 11,
    Saving    = 32773,
    Pausing   = 32776,
    Resuming  = 32777,
    FastSaved = 32769,
    FastPaused = 32768
}

public sealed record HyperVVmInfo
{
    public required Guid VmId { get; init; }
    public required string Name { get; init; }
    public required HyperVVmState RawState { get; init; }
    public required string StateDisplay { get; init; }
    public required int CpuCount { get; init; }
    public required long MemoryMb { get; init; }
    public required bool HasGpuPv { get; init; }
    public required TimeSpan Uptime { get; init; }

    public bool IsRunning => RawState == HyperVVmState.Running;

    /// <summary>
    /// Formats the VM GUID strictly WITHOUT curly braces ("d8a3f120-...")
    /// as required by vmms.exe on port 2179 for ExtendedSettings["PCB"].
    /// Passing braces "{...}" causes silent connection rejection (0x80070057).
    /// </summary>
    public string PcbGuidString => VmId.ToString("D").ToLowerInvariant();
}

public static class HyperVVmDiscovery
{
    private const string VirtualizationNamespace = @"\\.\root\virtualization\v2";

    public static IReadOnlyList<HyperVVmInfo> DiscoverLocalVirtualMachines()
    {
        var results = new List<HyperVVmInfo>();
        var scope = new ManagementScope(VirtualizationNamespace);
        scope.Connect();

        // Map VM InstanceID/Name -> GPU-PV status, vCPU count, and Memory MB
        var gpuPartitionVms = QueryVmsWithGpuPartition(scope);
        var (cpuByVmId, memByVmId) = QueryVmHardwareSettings(scope);

        using var searcher = new ManagementObjectSearcher(
            scope,
            new ObjectQuery("SELECT Name, ElementName, Caption, EnabledState, OnTimeInMilliseconds FROM Msvm_ComputerSystem")
        );

        foreach (ManagementObject vmObj in searcher.Get().OfType<ManagementObject>())
        {
            using (vmObj)
            {
                string? nameProp = vmObj["Name"]?.ToString();
                string? elementName = vmObj["ElementName"]?.ToString();
                string? caption = vmObj["Caption"]?.ToString();

                // Skip the Host computer system record (where Name == Environment.MachineName)
                if (string.IsNullOrWhiteSpace(nameProp) || !Guid.TryParse(nameProp, out Guid vmGuid))
                {
                    continue;
                }

                if (caption != null &&
                    !caption.Contains("Virtual Machine", StringComparison.OrdinalIgnoreCase) &&
                    !caption.Contains("Maszyna wirtualna", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(nameProp, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ushort enabledStateRaw = Convert.ToUInt16(vmObj["EnabledState"] ?? 0);
                ulong uptimeMs = Convert.ToUInt64(vmObj["OnTimeInMilliseconds"] ?? 0UL);

                var state = Enum.IsDefined(typeof(HyperVVmState), enabledStateRaw)
                    ? (HyperVVmState)enabledStateRaw
                    : HyperVVmState.Unknown;

                string key = vmGuid.ToString("D").ToUpperInvariant();
                int vCpus = cpuByVmId.TryGetValue(key, out int c) ? c : 2;
                long ramMb = memByVmId.TryGetValue(key, out long m) ? m : 4096;
                bool hasGpuPv = gpuPartitionVms.Contains(key);

                results.Add(new HyperVVmInfo
                {
                    VmId = vmGuid,
                    Name = string.IsNullOrWhiteSpace(elementName) ? vmGuid.ToString("D") : elementName,
                    RawState = state,
                    StateDisplay = FormatStateDisplay(state),
                    CpuCount = vCpus,
                    MemoryMb = ramMb,
                    HasGpuPv = hasGpuPv,
                    Uptime = TimeSpan.FromMilliseconds(uptimeMs)
                });
            }
        }

        return results
            .OrderByDescending(v => v.IsRunning)
            .ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static HyperVVmInfo? FindVmByNameOrGuid(string nameOrGuid)
    {
        var allVms = DiscoverLocalVirtualMachines();
        if (Guid.TryParse(nameOrGuid.Trim('{', '}', ' '), out Guid parsedGuid))
        {
            return allVms.FirstOrDefault(v => v.VmId == parsedGuid);
        }

        return allVms.FirstOrDefault(v =>
            string.Equals(v.Name, nameOrGuid, StringComparison.OrdinalIgnoreCase));
    }

    public static bool TryStartVirtualMachine(Guid vmGuid, out string? errorMessage)
    {
        errorMessage = null;
        try
        {
            var scope = new ManagementScope(VirtualizationNamespace);
            scope.Connect();

            string guidStr = vmGuid.ToString("D").ToUpperInvariant();
            using var searcher = new ManagementObjectSearcher(
                scope,
                new ObjectQuery($"SELECT * FROM Msvm_ComputerSystem WHERE Name = '{guidStr}'")
            );

            foreach (ManagementObject vm in searcher.Get().OfType<ManagementObject>())
            {
                using (vm)
                {
                    using ManagementBaseObject inParams = vm.GetMethodParameters("RequestStateChange");
                    inParams["RequestedState"] = (ushort)2; // Enabled / Running
                    using ManagementBaseObject outParams = vm.InvokeMethod("RequestStateChange", inParams, null!);
                    uint returnValue = Convert.ToUInt32(outParams["ReturnValue"] ?? 1u);
                    // 0 = Completed with No Error, 4096 = Job Started
                    if (returnValue is 0 or 4096)
                    {
                        return true;
                    }
                    errorMessage = $"WMI RequestStateChange returned code {returnValue}.";
                    return false;
                }
            }

            errorMessage = $"VM {vmGuid:D} not found in root\\virtualization\\v2.";
            return false;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static HashSet<string> QueryVmsWithGpuPartition(ManagementScope scope)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                scope,
                new ObjectQuery("SELECT InstanceID FROM Msvm_GpuPartitionSettingData")
            );
            foreach (ManagementObject obj in searcher.Get().OfType<ManagementObject>())
            {
                using (obj)
                {
                    string? instanceId = obj["InstanceID"]?.ToString();
                    string? vmGuid = ExtractVmGuidFromInstanceId(instanceId);
                    if (vmGuid != null)
                    {
                        set.Add(vmGuid);
                    }
                }
            }
        }
        catch
        {
            // Msvm_GpuPartitionSettingData may not exist on older Hyper-V builds
        }
        return set;
    }

    private static (Dictionary<string, int> CpuByVm, Dictionary<string, long> MemByVm) QueryVmHardwareSettings(ManagementScope scope)
    {
        var cpuByVm = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var memByVm = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var procSearcher = new ManagementObjectSearcher(
                scope,
                new ObjectQuery("SELECT InstanceID, VirtualQuantity FROM Msvm_ProcessorSettingData")
            );
            foreach (ManagementObject obj in procSearcher.Get().OfType<ManagementObject>())
            {
                using (obj)
                {
                    string? vmGuid = ExtractVmGuidFromInstanceId(obj["InstanceID"]?.ToString());
                    if (vmGuid != null && obj["VirtualQuantity"] != null)
                    {
                        cpuByVm[vmGuid] = Convert.ToInt32(obj["VirtualQuantity"]);
                    }
                }
            }

            using var memSearcher = new ManagementObjectSearcher(
                scope,
                new ObjectQuery("SELECT InstanceID, VirtualQuantity FROM Msvm_MemorySettingData")
            );
            foreach (ManagementObject obj in memSearcher.Get().OfType<ManagementObject>())
            {
                using (obj)
                {
                    string? vmGuid = ExtractVmGuidFromInstanceId(obj["InstanceID"]?.ToString());
                    if (vmGuid != null && obj["VirtualQuantity"] != null)
                    {
                        memByVm[vmGuid] = Convert.ToInt64(obj["VirtualQuantity"]);
                    }
                }
            }
        }
        catch
        {
            // Ignore partial hardware query failures
        }

        return (cpuByVm, memByVm);
    }

    private static string? ExtractVmGuidFromInstanceId(string? instanceId)
    {
        // Typical format: "Microsoft:D8A3F120-5C3D-4F18-9A2E-7D6C4B8E1001\\..."
        if (string.IsNullOrWhiteSpace(instanceId)) return null;

        int colonIdx = instanceId.IndexOf(':');
        if (colonIdx < 0 || colonIdx + 36 >= instanceId.Length) return null;

        string candidate = instanceId.Substring(colonIdx + 1, 36);
        return Guid.TryParse(candidate, out Guid g) ? g.ToString("D").ToUpperInvariant() : null;
    }

    private static string FormatStateDisplay(HyperVVmState state) => state switch
    {
        HyperVVmState.Running               => "Running",
        HyperVVmState.Off                   => "Off",
        HyperVVmState.Paused or
        HyperVVmState.FastPaused            => "Paused",
        HyperVVmState.Saved or
        HyperVVmState.FastSaved             => "Saved",
        HyperVVmState.Starting              => "Starting...",
        HyperVVmState.Stopping              => "Stopping...",
        _                                   => state.ToString()
    };
}
