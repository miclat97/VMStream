<#
.SYNOPSIS
    Automatically detects, downloads, and installs all build prerequisites for VMStream:
      1. .NET 8.0 SDK (x64)
      2. Visual Studio 2022 Build Tools (MSBuild + MSVC v143 x64 C++20 compiler + Spectre libs)
      3. Windows 10/11 SDK (headers & libraries for Winsock2 / AF_HYPERV / SetupAPI)
      4. Windows Driver Kit (WDK) + MSBuild WindowsKernelModeDriver10.0 VSIX integration

.DESCRIPTION
    Can be run standalone or invoked automatically by scripts\Build-All.ps1.
    Updates $env:PATH in the current PowerShell session so compilation works immediately
    without needing to restart the terminal.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [switch]$SkipWdk
)

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Test-IsAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Refresh-SessionPath {
    $machinePath = [Environment]::GetEnvironmentVariable("Path", "Machine")
    $userPath    = [Environment]::GetEnvironmentVariable("Path", "User")
    $combined    = "$machinePath;$userPath"
    foreach ($entry in ($combined -split ';')) {
        if (-not [string]::IsNullOrWhiteSpace($entry) -and ($env:PATH -notlike "*$entry*")) {
            $env:PATH = "$entry;$env:PATH"
        }
    }
}

Write-Host "======================================================================" -ForegroundColor Cyan
Write-Host " VMStream Toolchain & Dependency Bootstrapper"                          -ForegroundColor Cyan
Write-Host "======================================================================" -ForegroundColor Cyan

Refresh-SessionPath

# ============================================================================
# 1. Ensure .NET 8.0+ SDK (x64) is Installed
# ============================================================================
Write-Host "`n[Prereq 1/4] Checking .NET 8.0+ SDK (x64)..." -ForegroundColor Yellow

$DotNetCandidates = @(
    "$env:ProgramFiles\dotnet\dotnet.exe",
    "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
)
$DotNetCmd = Get-Command dotnet.exe -ErrorAction SilentlyContinue
if ($DotNetCmd) { $DotNetCandidates = @($DotNetCmd.Source) + $DotNetCandidates }

$DotNetExe = $null
$HasDotNet8Sdk = $false

foreach ($candidate in $DotNetCandidates) {
    if ($candidate -and (Test-Path $candidate)) {
        try {
            $sdks = & $candidate --list-sdks 2>$null
            if ($sdks -match "^(8|9|10)\.\d+") {
                $DotNetExe = $candidate
                $HasDotNet8Sdk = $true
                break
            }
        } catch { }
    }
}

if (-not $HasDotNet8Sdk) {
    Write-Host "      -> .NET 8.0 SDK not found. Installing .NET 8.0 SDK (x64)..." -ForegroundColor Cyan

    $installedViaWinget = $false
    $wingetCmd = Get-Command winget.exe -ErrorAction SilentlyContinue
    if ($wingetCmd) {
        try {
            Write-Host "      -> Running: winget install Microsoft.DotNet.SDK.8 ..." -ForegroundColor Gray
            & winget.exe install --id Microsoft.DotNet.SDK.8 --exact --silent --accept-package-agreements --accept-source-agreements
            Refresh-SessionPath
            if (Test-Path "$env:ProgramFiles\dotnet\dotnet.exe") {
                $sdks = & "$env:ProgramFiles\dotnet\dotnet.exe" --list-sdks 2>$null
                if ($sdks -match "^(8|9|10)\.\d+") {
                    $DotNetExe = "$env:ProgramFiles\dotnet\dotnet.exe"
                    $HasDotNet8Sdk = $true
                    $installedViaWinget = $true
                }
            }
        } catch {
            Write-Warning "winget install for .NET 8 SDK failed; falling back to official dotnet-install.ps1..."
        }
    }

    if (-not $installedViaWinget) {
        $InstallDir = if (Test-IsAdmin) { "$env:ProgramFiles\dotnet" } else { "$env:LOCALAPPDATA\Microsoft\dotnet" }
        New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

        $DotNetInstallerScript = Join-Path $env:TEMP "dotnet-install.ps1"
        Write-Host "      -> Downloading https://dot.net/v1/dotnet-install.ps1 ..." -ForegroundColor Gray
        Invoke-WebRequest -Uri "https://dot.net/v1/dotnet-install.ps1" -OutFile $DotNetInstallerScript -UseBasicParsing

        Write-Host "      -> Installing .NET 8.0 SDK to '$InstallDir'..." -ForegroundColor Gray
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $DotNetInstallerScript `
            -Channel 8.0 `
            -Architecture x64 `
            -InstallDir $InstallDir

        $DotNetExe = Join-Path $InstallDir "dotnet.exe"
        if (-not (Test-Path $DotNetExe)) {
            throw "Failed to install .NET 8.0 SDK to $InstallDir."
        }
        $HasDotNet8Sdk = $true
    }
}

$DotNetDir = Split-Path -Parent $DotNetExe
$env:DOTNET_ROOT = $DotNetDir
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
if ($env:PATH -notlike "*$DotNetDir*") {
    $env:PATH = "$DotNetDir;$env:PATH"
}
$sdkVersionLine = (& $DotNetExe --list-sdks | Select-Object -Last 1)
Write-Host "      -> Using .NET SDK: $DotNetExe ($sdkVersionLine)" -ForegroundColor Green

# ============================================================================
# 2. Ensure Visual Studio 2022 Build Tools & MSVC v143 x64 C++ Compiler
# ============================================================================
Write-Host "`n[Prereq 2/4] Checking Visual Studio 2022 C++ Build Tools (MSBuild & cl.exe)..." -ForegroundColor Yellow

function Find-VsToolchain {
    $vsWhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vsWhere)) {
        $vsWhere = Join-Path $env:TEMP "vswhere.exe"
        if (-not (Test-Path $vsWhere)) {
            Invoke-WebRequest -Uri "https://github.com/microsoft/vswhere/releases/latest/download/vswhere.exe" -OutFile $vsWhere -UseBasicParsing
        }
    }

    $installPath = & $vsWhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1
    if (-not $installPath) {
        $installPath = & $vsWhere -latest -products * -property installationPath | Select-Object -First 1
    }
    if (-not $installPath) { return $null }

    $msbuild = Join-Path $installPath "MSBuild\Current\Bin\amd64\MSBuild.exe"
    if (-not (Test-Path $msbuild)) {
        $msbuild = Join-Path $installPath "MSBuild\Current\Bin\MSBuild.exe"
    }

    $vcToolsRoot = Join-Path $installPath "VC\Tools\MSVC"
    $clExe = $null
    $linkExe = $null
    if (Test-Path $vcToolsRoot) {
        $latestMsvc = Get-ChildItem -Path $vcToolsRoot -Directory | Sort-Object Name -Descending | Select-Object -First 1
        if ($latestMsvc) {
            $clCandidate = Join-Path $latestMsvc.FullName "bin\Hostx64\x64\cl.exe"
            $linkCandidate = Join-Path $latestMsvc.FullName "bin\Hostx64\x64\link.exe"
            if (Test-Path $clCandidate) {
                $clExe = $clCandidate
                $linkExe = $linkCandidate
            }
        }
    }

    return [PSCustomObject]@{
        InstallPath = $installPath
        MsBuildExe  = if (Test-Path $msbuild) { $msbuild } else { $null }
        ClExe       = $clExe
        LinkExe     = $linkExe
        MsvcRoot    = if ($clExe) { Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $clExe)) } else { $null }
    }
}

function Invoke-SelfElevatedIfNeeded {
    if (Test-IsAdmin) { return $false }
    Write-Host "      -> Requesting Administrator privileges (UAC) to install missing C++/WDK toolchains..." -ForegroundColor Yellow
    $argList = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    if ($SkipWdk) { $argList += " -SkipWdk" }
    $elevatedProc = Start-Process -FilePath "powershell.exe" -ArgumentList $argList -Verb RunAs -Wait -PassThru
    if ($elevatedProc.ExitCode -ne 0) {
        throw "Elevated prerequisite installation exited with code $($elevatedProc.ExitCode)."
    }
    Refresh-SessionPath
    return $true
}

$VsInfo = Find-VsToolchain

if (-not $VsInfo -or -not $VsInfo.MsBuildExe -or -not $VsInfo.ClExe) {
    if (-not (Test-IsAdmin)) {
        Invoke-SelfElevatedIfNeeded | Out-Null
        $VsInfo = Find-VsToolchain
    }
}

if (-not $VsInfo -or -not $VsInfo.MsBuildExe -or -not $VsInfo.ClExe) {
    Write-Host "      -> MSVC x64 C++ Compiler not found. Downloading Visual Studio 2022 Build Tools..." -ForegroundColor Cyan
    $VsBootstrapper = Join-Path $env:TEMP "vs_buildtools.exe"
    Invoke-WebRequest -Uri "https://aka.ms/vs/17/release/vs_buildtools.exe" -OutFile $VsBootstrapper -UseBasicParsing

    Write-Host "      -> Installing MSVC v143 x64 C++ Build Tools, Spectre libs & Windows 11 SDK (this may take several minutes)..." -ForegroundColor Cyan
    $vsArgs = @(
        "--quiet",
        "--wait",
        "--norestart",
        "--nocache",
        "--add", "Microsoft.VisualStudio.Workload.VCTools",
        "--add", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
        "--add", "Microsoft.VisualStudio.Component.VC.Runtimes.x86.x64.Spectre",
        "--add", "Microsoft.VisualStudio.Component.Windows11SDK.22621",
        "--includeRecommended"
    )

    $proc = Start-Process -FilePath $VsBootstrapper -ArgumentList $vsArgs -Wait -PassThru
    if ($proc.ExitCode -ne 0 -and $proc.ExitCode -ne 3010) {
        Write-Warning "vs_buildtools.exe exited with code $($proc.ExitCode). Checking if toolchain was installed..."
    }

    Refresh-SessionPath
    $VsInfo = Find-VsToolchain
    if (-not $VsInfo -or -not $VsInfo.ClExe) {
        throw "Failed to locate MSVC cl.exe after running vs_buildtools.exe."
    }
}

Write-Host "      -> Using MSBuild : $($VsInfo.MsBuildExe)" -ForegroundColor Green
Write-Host "      -> Using MSVC cl : $($VsInfo.ClExe)"      -ForegroundColor Green

# ============================================================================
# 3. Ensure Windows 10/11 SDK is Installed
# ============================================================================
Write-Host "`n[Prereq 3/4] Checking Windows 10/11 SDK..." -ForegroundColor Yellow

$KitsRoot = "${env:ProgramFiles(x86)}\Windows Kits\10"

function Find-WindowsSdkVersion {
    $incRoot = Join-Path $KitsRoot "Include"
    if (-not (Test-Path $incRoot)) { return $null }
    $vers = Get-ChildItem -Path $incRoot -Directory | Where-Object {
        $_.Name -match "^10\.0\.\d+\.\d+$" -and (Test-Path (Join-Path $_.FullName "um\winsock2.h"))
    } | Sort-Object Name -Descending
    if ($vers) { return $vers[0].Name }
    return $null
}

$SdkVersion = Find-WindowsSdkVersion
if (-not $SdkVersion) {
    if (-not (Test-IsAdmin)) {
        Invoke-SelfElevatedIfNeeded | Out-Null
        $SdkVersion = Find-WindowsSdkVersion
    }
}

if (-not $SdkVersion) {
    Write-Host "      -> Windows SDK not found. Installing Windows 11 SDK (10.0.22621.0)..." -ForegroundColor Cyan
    $SdkSetup = Join-Path $env:TEMP "winsdksetup.exe"
    Invoke-WebRequest -Uri "https://go.microsoft.com/fwlink/?linkid=2196241" -OutFile $SdkSetup -UseBasicParsing
    Start-Process -FilePath $SdkSetup -ArgumentList "/features OptionId.DesktopCPPx64 OptionId.SigningTools /quiet /norestart" -Wait

    $SdkVersion = Find-WindowsSdkVersion
    if (-not $SdkVersion) {
        throw "Failed to install Windows 10/11 SDK under $KitsRoot."
    }
}

Write-Host "      -> Using Windows SDK Version: $SdkVersion" -ForegroundColor Green

# ============================================================================
# 4. Ensure Windows Driver Kit (WDK) & MSBuild Kernel Toolset Integration
# ============================================================================
$WdkVersion = $null
$KmdfVersion = "1.31"

if (-not $SkipWdk) {
    Write-Host "`n[Prereq 4/4] Checking Windows Driver Kit (WDK) for HvTouchHid.sys & HvVirtualAudio.sys..." -ForegroundColor Yellow

    function Find-WdkVersion {
        $incRoot = Join-Path $KitsRoot "Include"
        if (-not (Test-Path $incRoot)) { return $null }
        $vers = Get-ChildItem -Path $incRoot -Directory | Where-Object {
            $_.Name -match "^10\.0\.\d+\.\d+$" -and
            (Test-Path (Join-Path $_.FullName "km\ntddk.h")) -and
            (Test-Path (Join-Path $_.FullName "km\vhf.h")) -and
            (Test-Path (Join-Path $_.FullName "km\portcls.h"))
        } | Sort-Object Name -Descending
        if ($vers) { return $vers[0].Name }
        return $null
    }

    $WdkVersion = Find-WdkVersion

    if (-not $WdkVersion) {
        if (-not (Test-IsAdmin)) {
            Invoke-SelfElevatedIfNeeded | Out-Null
            $WdkVersion = Find-WdkVersion
        }
    }

    if (-not $WdkVersion) {

        Write-Host "      -> Windows Driver Kit (WDK) not found. Downloading & installing WDK (10.0.22621.0)..." -ForegroundColor Cyan
        $installedWdkWinget = $false
        $wingetCmd = Get-Command winget.exe -ErrorAction SilentlyContinue
        if ($wingetCmd) {
            try {
                & winget.exe install --id Microsoft.WindowsWDK.10.0.22621 --exact --silent --accept-package-agreements --accept-source-agreements
                $WdkVersion = Find-WdkVersion
                if ($WdkVersion) { $installedWdkWinget = $true }
            } catch { }
        }

        if (-not $installedWdkWinget) {
            $WdkSetup = Join-Path $env:TEMP "wdksetup.exe"
            Invoke-WebRequest -Uri "https://go.microsoft.com/fwlink/?linkid=2249371" -OutFile $WdkSetup -UseBasicParsing
            Write-Host "      -> Running wdksetup.exe /features + /quiet /norestart ..." -ForegroundColor Cyan
            Start-Process -FilePath $WdkSetup -ArgumentList "/features + /quiet /norestart" -Wait
            $WdkVersion = Find-WdkVersion
        }
    }

    if ($WdkVersion) {
        Write-Host "      -> Using WDK Version: $WdkVersion" -ForegroundColor Green

        # Detect installed KMDF version (e.g. 1.31 or 1.33)
        $kmdfIncBase = Join-Path $KitsRoot "Include\wdf\kmdf"
        if (Test-Path $kmdfIncBase) {
            $latestKmdf = Get-ChildItem -Path $kmdfIncBase -Directory | Sort-Object Name -Descending | Select-Object -First 1
            if ($latestKmdf) { $KmdfVersion = $latestKmdf.Name }
        }

        # Ensure WindowsKernelModeDriver10.0 MSBuild PlatformToolset is registered in VS/BuildTools
        $VcTargetsDir = Join-Path $VsInfo.InstallPath "MSBuild\Microsoft\VC\v170"
        $KernelToolsetTargets = Join-Path $VcTargetsDir "Platforms\x64\PlatformToolsets\WindowsKernelModeDriver10.0\Toolset.targets"

        if (-not (Test-Path $KernelToolsetTargets)) {
            Write-Host "      -> Integrating WDK MSBuild Toolset (WindowsKernelModeDriver10.0) into Visual Studio..." -ForegroundColor Cyan
            $VsixFile = Get-ChildItem -Path (Join-Path $KitsRoot "Vsix") -Recurse -Filter "WDK.vsix" -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($VsixFile -and (Test-IsAdmin)) {
                $ExtractDir = Join-Path $env:TEMP "VMStream_WDK_Vsix_Extract"
                if (Test-Path $ExtractDir) { Remove-Item -Path $ExtractDir -Recurse -Force }
                New-Item -ItemType Directory -Path $ExtractDir -Force | Out-Null

                $TempZip = Join-Path $env:TEMP "WDK_vsix.zip"
                Copy-Item -Path $VsixFile.FullName -Destination $TempZip -Force
                Expand-Archive -Path $TempZip -DestinationPath $ExtractDir -Force
                Remove-Item -Path $TempZip -Force -ErrorAction SilentlyContinue

                $VsixMsBuildSrc = Join-Path $ExtractDir '$MSBuild\Microsoft\VC\v170'
                if (-not (Test-Path $VsixMsBuildSrc)) {
                    $VsixMsBuildSrc = Get-ChildItem -Path $ExtractDir -Recurse -Directory | Where-Object { $_.FullName -like "*Microsoft\VC\v170" } | Select-Object -First 1 -ExpandProperty FullName
                }

                if ($VsixMsBuildSrc -and (Test-Path $VsixMsBuildSrc)) {
                    Copy-Item -Path "$VsixMsBuildSrc\*" -Destination $VcTargetsDir -Recurse -Force
                    Write-Host "      -> Extracted WindowsKernelModeDriver10.0 MSBuild targets to: $VcTargetsDir" -ForegroundColor Green
                }
                Remove-Item -Path $ExtractDir -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    } else {
        Write-Warning "WDK kernel headers were not found under $KitsRoot. Kernel drivers (.sys) will be skipped unless WDK is installed."
    }
}

# Return discovered toolchain metadata for Build-All.ps1
return [PSCustomObject]@{
    DotNetExe    = $DotNetExe
    VsInstallDir = $VsInfo.InstallPath
    MsBuildExe   = $VsInfo.MsBuildExe
    ClExe        = $VsInfo.ClExe
    LinkExe      = $VsInfo.LinkExe
    MsvcRoot     = $VsInfo.MsvcRoot
    KitsRoot     = $KitsRoot
    SdkVersion   = $SdkVersion
    WdkVersion   = $WdkVersion
    KmdfVersion  = $KmdfVersion
}
