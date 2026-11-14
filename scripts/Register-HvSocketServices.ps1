<#
.SYNOPSIS
    Registers VMStream Hyper-V Socket (AF_HYPERV) Service GUIDs on the Hyper-V Host
    and configures non-admin SDDL permissions + optional VM Secure Boot disablement.

.DESCRIPTION
    Creates the required registry keys under:
      HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization\GuestCommunicationServices
    for:
      1. Audio Stream Service: {8B4E9A12-5C3D-4F18-9A2E-7D6C4B8E1001}
      2. Touch HID Service:    {8B4E9A12-5C3D-4F18-9A2E-7D6C4B8E1002}

    Also writes the "Sddl" REG_SZ value ("D:(A;;GA;;;AU)(A;;GA;;;SY)(A;;GA;;;BA)") so
    HvConsole.Host.exe can call bind(AF_HYPERV) as a standard user without UAC elevation
    (preventing WSAEACCES 10013).

.PARAMETER VmNameForDriverPrep
    Optional name of a local Generation 2 VM on which to disable Secure Boot
    (Set-VMFirmware -EnableSecureBoot Off) so test-signed Guest WDK drivers load cleanly.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$VmNameForDriverPrep = "",

    [Parameter(Mandatory = $false)]
    [switch]$Unregister
)

$ErrorActionPreference = "Stop"

# Verify Administrator privileges
$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "Register-HvSocketServices.ps1 must be run from an elevated PowerShell prompt (Run as Administrator)."
    exit 1
}

$BaseRegPath = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization\GuestCommunicationServices"

$Services = @(
    @{
        Guid        = "8b4e9a12-5c3d-4f18-9a2e-7d6c4b8e1001"
        ElementName = "VMStream Hyper-V Console Audio Service (WaveRT 48kHz)"
    },
    @{
        Guid        = "8b4e9a12-5c3d-4f18-9a2e-7d6c4b8e1002"
        ElementName = "VMStream Hyper-V Console Multi-Touch HID Service (VHF)"
    }
)

# SDDL granting Generic All (GA) to Authenticated Users (AU), LocalSystem (SY), and Built-in Admins (BA).
# Without this Sddl value, calling bind() on AF_HYPERV without UAC elevation fails with WSAEACCES (10013).
$NonAdminSddl = "D:(A;;GA;;;AU)(A;;GA;;;SY)(A;;GA;;;BA)"

if (-not (Test-Path -Path $BaseRegPath)) {
    New-Item -Path $BaseRegPath -Force | Out-Null
}

foreach ($svc in $Services) {
    $svcKeyPath = Join-Path -Path $BaseRegPath -ChildPath $svc.Guid

    if ($Unregister) {
        if (Test-Path -Path $svcKeyPath) {
            Remove-Item -Path $svcKeyPath -Recurse -Force
            Write-Host "[VMStream] Unregistered AF_HYPERV Service: {$($svc.Guid)} ($($svc.ElementName))" -ForegroundColor Yellow
        }
        continue
    }

    if (-not (Test-Path -Path $svcKeyPath)) {
        New-Item -Path $svcKeyPath -Force | Out-Null
    }

    New-ItemProperty -Path $svcKeyPath -Name "ElementName" -Value $svc.ElementName -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $svcKeyPath -Name "Sddl"        -Value $NonAdminSddl    -PropertyType String -Force | Out-Null

    Write-Host "[VMStream] Registered AF_HYPERV Service:" -ForegroundColor Cyan
    Write-Host "           GUID        : {$($svc.Guid)}"
    Write-Host "           ElementName : $($svc.ElementName)"
    Write-Host "           Sddl        : $NonAdminSddl (Non-Admin bind enabled)" -ForegroundColor Green
}

if (-not [string]::IsNullOrWhiteSpace($VmNameForDriverPrep)) {
    Write-Host "`n[VMStream] Configuring VM '$VmNameForDriverPrep' firmware for test-signed Guest WDK drivers..." -ForegroundColor Cyan
    $vm = Get-VM -Name $VmNameForDriverPrep -ErrorAction SilentlyContinue
    if ($null -eq $vm) {
        Write-Warning "VM '$VmNameForDriverPrep' was not found on this host."
    } elseif ($vm.Generation -eq 2) {
        Set-VMFirmware -VMName $VmNameForDriverPrep -EnableSecureBoot Off
        Write-Host "[VMStream] Disabled Secure Boot on Generation 2 VM '$VmNameForDriverPrep' (required for test-signed HvTouchHid.sys & HvVirtualAudio.sys)." -ForegroundColor Green
    } else {
        Write-Host "[VMStream] VM '$VmNameForDriverPrep' is Generation 1 (Secure Boot N/A)." -ForegroundColor Gray
    }
}

Write-Host "`n[VMStream] Host HVSocket registration complete." -ForegroundColor Green
