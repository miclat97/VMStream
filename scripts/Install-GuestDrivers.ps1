<#
.SYNOPSIS
    Installs the VMStream Guest WDK Drivers (HvTouchHid.sys & HvVirtualAudio.sys)
    and registers the HvGuestBridge.exe Windows Service inside a Hyper-V Guest VM.

.DESCRIPTION
    1. Verifies that Hyper-V Secure Boot is OFF on the VM (Set-VMFirmware -EnableSecureBoot Off).
    2. Enables bcdedit /set testsigning on.
    3. Creates a self-signed Code Signing test certificate and imports it into
       Trusted Root Certification Authorities and Trusted Publishers.
    4. Signs HvTouchHid.sys and HvVirtualAudio.sys with Set-AuthenticodeSignature.
    5. Creates Root-enumerated PnP device nodes (Root\HvTouchHid and Root\HvVirtualAudio)
       via native SetupAPI + newdev.dll and installs the drivers via pnputil.exe.
    6. Installs and starts HvGuestBridge.exe as a LocalSystem auto-start Windows Service.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$PackageDir = $PSScriptRoot
)

$ErrorActionPreference = "Stop"

# Verify Administrator privileges inside Guest VM
$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "Install-GuestDrivers.ps1 must be run as Administrator inside the Guest VM."
    exit 1
}

Write-Host "======================================================================" -ForegroundColor Cyan
Write-Host " VMStream Guest Installer (HvTouchHid + HvVirtualAudio + HvGuestBridge)" -ForegroundColor Cyan
Write-Host "======================================================================" -ForegroundColor Cyan

# ----------------------------------------------------------------------------
# 1. Check Hyper-V Generation 2 Secure Boot Status (Section 2.E)
# ----------------------------------------------------------------------------
try {
    $secureBootEnabled = Confirm-SecureBootUEFI -ErrorAction SilentlyContinue
    if ($secureBootEnabled -eq $true) {
        Write-Warning "Hyper-V Secure Boot is currently ENABLED for this Virtual Machine!"
        Write-Warning "Even with testsigning enabled, the hypervisor will block test-signed WDK drivers (HvTouchHid.sys / HvVirtualAudio.sys) while Secure Boot is active."
        Write-Host "`nRun this command on your Hyper-V HOST PowerShell (as Admin) after shutting down the VM:" -ForegroundColor Yellow
        Write-Host '    Set-VMFirmware -VMName "<YourVMName>" -EnableSecureBoot Off' -ForegroundColor White
        Write-Host ""
    }
} catch {
    # Generation 1 BIOS VM or non-UEFI
}

# ----------------------------------------------------------------------------
# 2. Enable Kernel Test-Signing Mode (bcdedit /set testsigning on)
# ----------------------------------------------------------------------------
Write-Host "`n[1/5] Enabling Windows Kernel Test-Signing (bcdedit /set testsigning on)..." -ForegroundColor Yellow
& bcdedit.exe /set testsigning on | Out-Host
& bcdedit.exe /set nointegritychecks off | Out-Null

# ----------------------------------------------------------------------------
# 3. Create & Trust Self-Signed Driver Test Certificate
# ----------------------------------------------------------------------------
Write-Host "`n[2/5] Creating & installing VMStream WDK Test Certificate..." -ForegroundColor Yellow
$CertSubject = "CN=VMStream WDK Test Certificate"
$cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -eq $CertSubject } | Select-Object -First 1

if ($null -eq $cert) {
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $CertSubject `
        -KeyUsage DigitalSignature `
        -FriendlyName "VMStream WDK Driver Signing Certificate" `
        -CertStoreLocation "Cert:\LocalMachine\My" `
        -NotAfter (Get-Date).AddYears(10)
}

$TempCerFile = Join-Path $env:TEMP "VMStreamTestCert.cer"
Export-Certificate -Cert $cert -FilePath $TempCerFile -Force | Out-Null
Import-Certificate -FilePath $TempCerFile -CertStoreLocation "Cert:\LocalMachine\Root"             | Out-Null
Import-Certificate -FilePath $TempCerFile -CertStoreLocation "Cert:\LocalMachine\TrustedPublisher" | Out-Null
Remove-Item -Path $TempCerFile -Force -ErrorAction SilentlyContinue
Write-Host "      -> Trusted Thumbprint: $($cert.Thumbprint)" -ForegroundColor Green

# ----------------------------------------------------------------------------
# 4. Sign Driver Binaries (.sys / .cat)
# ----------------------------------------------------------------------------
Write-Host "`n[3/5] Signing driver binaries with test certificate..." -ForegroundColor Yellow
$DriverFiles = Get-ChildItem -Path $PackageDir -Recurse -Include "HvTouchHid.sys", "HvVirtualAudio.sys", "HvTouchHid.cat", "HvVirtualAudio.cat" -ErrorAction SilentlyContinue
foreach ($file in $DriverFiles) {
    Set-AuthenticodeSignature -FilePath $file.FullName -Certificate $cert -HashAlgorithm SHA256 | Out-Null
    Write-Host "      -> Signed: $($file.FullName)" -ForegroundColor Green
}

# ----------------------------------------------------------------------------
# 5. Embedded SetupAPI Helper to Create Root\HvTouchHid & Root\HvVirtualAudio
#    DevNodes without requiring external devcon.exe
# ----------------------------------------------------------------------------
$SetupApiCode = @"
using System;
using System.Runtime.InteropServices;

public static class RootDevNodeInstaller
{
    private const int DICD_GENERATE_ID = 0x00000001;
    private const uint SPDRP_HARDWAREID = 0x00000001;
    private const uint DIF_REGISTERDEVICE = 0x00000019;
    private const uint INSTALLFLAG_FORCE = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetINFClassW(
        string InfName,
        out Guid ClassGuid,
        [Out] char[] ClassName,
        uint ClassNameSize,
        out uint RequiredSize
    );

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid ClassGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiCreateDeviceInfoW(
        IntPtr DeviceInfoSet,
        string DeviceName,
        ref Guid ClassGuid,
        string DeviceDescription,
        IntPtr hwndParent,
        uint CreationFlags,
        ref SP_DEVINFO_DATA DeviceInfoData
    );

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiSetDeviceRegistryPropertyW(
        IntPtr DeviceInfoSet,
        ref SP_DEVINFO_DATA DeviceInfoData,
        uint Property,
        byte[] PropertyBuffer,
        uint PropertyBufferSize
    );

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(
        uint InstallFunction,
        IntPtr DeviceInfoSet,
        ref SP_DEVINFO_DATA DeviceInfoData
    );

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UpdateDriverForPlugAndPlayDevicesW(
        IntPtr hwndParent,
        string HardwareId,
        string FullInfPath,
        uint InstallFlags,
        out bool bRebootRequired
    );

    public static bool InstallRootEnumeratedDriver(string infPath, string hardwareId)
    {
        char[] className = new char[64];
        if (!SetupDiGetINFClassW(infPath, out Guid classGuid, className, (uint)className.Length, out _))
        {
            return false;
        }

        IntPtr devInfoSet = SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
        if (devInfoSet == new IntPtr(-1))
        {
            return false;
        }

        try
        {
            var devInfoData = new SP_DEVINFO_DATA
            {
                cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>()
            };

            string classStr = new string(className).TrimEnd('\0');
            if (!SetupDiCreateDeviceInfoW(devInfoSet, classStr, ref classGuid, null, IntPtr.Zero, DICD_GENERATE_ID, ref devInfoData))
            {
                return false;
            }

            // REG_MULTI_SZ double-null-terminated UTF-16 byte buffer
            byte[] hwidBytes = System.Text.Encoding.Unicode.GetBytes(hardwareId + "\0\0");
            if (!SetupDiSetDeviceRegistryPropertyW(devInfoSet, ref devInfoData, SPDRP_HARDWAREID, hwidBytes, (uint)hwidBytes.Length))
            {
                return false;
            }

            if (!SetupDiCallClassInstaller(DIF_REGISTERDEVICE, devInfoSet, ref devInfoData))
            {
                return false;
            }

            UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, hardwareId, infPath, INSTALLFLAG_FORCE, out _);
            return true;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devInfoSet);
        }
    }
}
"@

Add-Type -TypeDefinition $SetupApiCode -Language CSharp

Write-Host "`n[4/5] Installing Root\HvTouchHid and Root\HvVirtualAudio WDK drivers..." -ForegroundColor Yellow

$BridgeExe = Get-ChildItem -Path $PackageDir -Recurse -Filter "HvGuestBridge.exe" -ErrorAction SilentlyContinue | Select-Object -First 1

$TouchInf = Get-ChildItem -Path $PackageDir -Recurse -Filter "HvTouchHid.inf" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -ne $TouchInf) {
    & pnputil.exe /add-driver "$($TouchInf.FullName)" /install | Out-Host
    if ($null -ne $BridgeExe) {
        & $BridgeExe.FullName --install-driver "Root\HvTouchHid" "$($TouchInf.FullName)" | Out-Host
    } else {
        [RootDevNodeInstaller]::InstallRootEnumeratedDriver($TouchInf.FullName, "Root\HvTouchHid") | Out-Null
    }
    Write-Host "      -> Installed Multi-Touch Digitizer Driver (Root\HvTouchHid)" -ForegroundColor Green
} else {
    Write-Warning "HvTouchHid.inf not found under $PackageDir. Build WDK drivers first with Setup-And-Build.cmd."
}

$AudioInf = Get-ChildItem -Path $PackageDir -Recurse -Filter "HvVirtualAudio.inf" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -ne $AudioInf) {
    & pnputil.exe /add-driver "$($AudioInf.FullName)" /install | Out-Host
    if ($null -ne $BridgeExe) {
        & $BridgeExe.FullName --install-driver "Root\HvVirtualAudio" "$($AudioInf.FullName)" | Out-Host
    } else {
        [RootDevNodeInstaller]::InstallRootEnumeratedDriver($AudioInf.FullName, "Root\HvVirtualAudio") | Out-Null
    }
    Write-Host "      -> Installed Virtual Audio Driver (Root\HvVirtualAudio)" -ForegroundColor Green
} else {
    Write-Warning "HvVirtualAudio.inf not found under $PackageDir. Build WDK drivers first with Setup-And-Build.cmd."
}

# ----------------------------------------------------------------------------
# 6. Install & Start HvGuestBridge.exe Service (LocalSystem)
# ----------------------------------------------------------------------------
Write-Host "`n[5/5] Installing HvGuestBridge.exe Windows Service (LocalSystem)..." -ForegroundColor Yellow

if ($null -ne $BridgeExe) {
    $InstallDir = "C:\Program Files\VMStream\Guest"
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

    Stop-Service -Name "HvGuestBridge" -Force -ErrorAction SilentlyContinue
    $TargetExe = Join-Path $InstallDir "HvGuestBridge.exe"
    Copy-Item -Path $BridgeExe.FullName -Destination $TargetExe -Force

    if ($null -ne $TouchInf) {
        $TargetTouchDir = Join-Path $InstallDir "HvTouchHid"
        New-Item -ItemType Directory -Path $TargetTouchDir -Force | Out-Null
        Copy-Item -Path "$($TouchInf.DirectoryName)\*" -Destination $TargetTouchDir -Force
    }
    if ($null -ne $AudioInf) {
        $TargetAudioDir = Join-Path $InstallDir "HvVirtualAudio"
        New-Item -ItemType Directory -Path $TargetAudioDir -Force | Out-Null
        Copy-Item -Path "$($AudioInf.DirectoryName)\*" -Destination $TargetAudioDir -Force
    }

    & $TargetExe --install | Out-Host
    Write-Host "      -> HvGuestBridge service installed and running from: $TargetExe" -ForegroundColor Green
} else {
    Write-Warning "HvGuestBridge.exe not found under $PackageDir."
}

Write-Host "`n======================================================================" -ForegroundColor Green
Write-Host " Guest Installation Complete!"                                          -ForegroundColor Green
Write-Host " NOTE: If 'bcdedit /set testsigning on' was enabled for the first time," -ForegroundColor Yellow
Write-Host "       reboot the Guest VM once so Windows loads the test-signed drivers." -ForegroundColor Yellow
Write-Host "======================================================================" -ForegroundColor Green
