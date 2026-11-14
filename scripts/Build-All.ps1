<#
.SYNOPSIS
    Builds the complete VMStream solution (C# Host, C++20 Guest Service, and WDK Drivers).

.DESCRIPTION
    1. Builds src\Host\HvConsole.Host\HvConsole.Host.csproj via dotnet build (x64).
    2. Locates Visual Studio MSBuild via vswhere.exe and builds:
       - src\Guest\HvGuestBridge\HvGuestBridge.vcxproj
       - src\Guest\HvTouchHid\HvTouchHid.vcxproj (WDK KMDF + VHF)
       - src\Guest\HvVirtualAudio\HvVirtualAudio.vcxproj (WDK PortCls WaveRT)
    3. Stages compiled Host and Guest deployment bundles into build\dist\Host and build\dist\Guest.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [Parameter(Mandatory = $false)]
    [switch]$SkipWdkDrivers
)

$ErrorActionPreference = "Stop"

$RepoRoot  = Split-Path -Parent $PSScriptRoot
$DistHost  = Join-Path $RepoRoot "build\dist\Host"
$DistGuest = Join-Path $RepoRoot "build\dist\Guest"

New-Item -ItemType Directory -Path $DistHost  -Force | Out-Null
New-Item -ItemType Directory -Path $DistGuest -Force | Out-Null

Write-Host "======================================================================" -ForegroundColor Cyan
Write-Host " VMStream Build Pipeline — Configuration: $Configuration (x64)"         -ForegroundColor Cyan
Write-Host "======================================================================" -ForegroundColor Cyan

# ----------------------------------------------------------------------------
# 1. Build C# Host Application (HvConsole.Host)
# ----------------------------------------------------------------------------
$HostProject = Join-Path $RepoRoot "src\Host\HvConsole.Host\HvConsole.Host.csproj"
Write-Host "`n[1/4] Building Host Application (HvConsole.Host)..." -ForegroundColor Yellow

dotnet publish $HostProject `
    -c $Configuration `
    -r win-x64 `
    --self-contained false `
    -o $DistHost

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed for HvConsole.Host.csproj (Exit code: $LASTEXITCODE)."
}
Copy-Item -Path (Join-Path $PSScriptRoot "Register-HvSocketServices.ps1") -Destination $DistHost -Force
Write-Host "      -> Host binaries staged to: $DistHost" -ForegroundColor Green

# ----------------------------------------------------------------------------
# Locate MSBuild for C++20 Guest Service & WDK Kernel Drivers
# ----------------------------------------------------------------------------
$MsBuildExe = $null
$VsWherePath = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $VsWherePath) {
    $MsBuildExe = & $VsWherePath -latest -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
}
if (-not $MsBuildExe) {
    $MsBuildCmd = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($MsBuildCmd) { $MsBuildExe = $MsBuildCmd.Source }
}

if (-not $MsBuildExe) {
    Write-Warning "MSBuild.exe was not found on PATH. Install Visual Studio 2022 with C++ Desktop & Windows Driver Kit (WDK) workloads to compile Guest C++ projects."
    exit 0
}

Write-Host "      Using MSBuild: $MsBuildExe" -ForegroundColor Gray

# ----------------------------------------------------------------------------
# 2. Build C++20 Guest Service (HvGuestBridge.exe)
# ----------------------------------------------------------------------------
$BridgeProj = Join-Path $RepoRoot "src\Guest\HvGuestBridge\HvGuestBridge.vcxproj"
Write-Host "`n[2/4] Building Guest Service Daemon (HvGuestBridge.exe)..." -ForegroundColor Yellow

& $MsBuildExe $BridgeProj `
    /p:Configuration=$Configuration `
    /p:Platform=x64 `
    /p:SolutionDir="$RepoRoot\" `
    /m /v:minimal

if ($LASTEXITCODE -ne 0) {
    throw "MSBuild failed for HvGuestBridge.vcxproj (Exit code: $LASTEXITCODE)."
}

$BridgeBinDir = Join-Path $RepoRoot "build\bin\$Configuration\HvGuestBridge"
if (Test-Path $BridgeBinDir) {
    Copy-Item -Path (Join-Path $BridgeBinDir "HvGuestBridge.exe") -Destination $DistGuest -Force -ErrorAction SilentlyContinue
}

# ----------------------------------------------------------------------------
# 3. Build WDK Drivers (HvTouchHid.sys & HvVirtualAudio.sys)
# ----------------------------------------------------------------------------
if (-not $SkipWdkDrivers) {
    $TouchDriverProj = Join-Path $RepoRoot "src\Guest\HvTouchHid\HvTouchHid.vcxproj"
    Write-Host "`n[3/4] Building WDK Multi-Touch VHF Driver (HvTouchHid.sys)..." -ForegroundColor Yellow

    & $MsBuildExe $TouchDriverProj `
        /p:Configuration=$Configuration `
        /p:Platform=x64 `
        /p:SolutionDir="$RepoRoot\" `
        /p:SignMode=Off `
        /m /v:minimal

    if ($LASTEXITCODE -ne 0) {
        throw "MSBuild failed for HvTouchHid.vcxproj. Ensure Windows 10/11 WDK is installed."
    }

    $AudioDriverProj = Join-Path $RepoRoot "src\Guest\HvVirtualAudio\HvVirtualAudio.vcxproj"
    Write-Host "`n[4/4] Building WDK PortCls/WaveRT Virtual Audio Driver (HvVirtualAudio.sys)..." -ForegroundColor Yellow

    & $MsBuildExe $AudioDriverProj `
        /p:Configuration=$Configuration `
        /p:Platform=x64 `
        /p:SolutionDir="$RepoRoot\" `
        /p:SignMode=Off `
        /m /v:minimal

    if ($LASTEXITCODE -ne 0) {
        throw "MSBuild failed for HvVirtualAudio.vcxproj. Ensure Windows 10/11 WDK is installed."
    }

    # Stage Driver Packages into build\dist\Guest
    $TouchOut = Join-Path $DistGuest "HvTouchHid"
    $AudioOut = Join-Path $DistGuest "HvVirtualAudio"
    New-Item -ItemType Directory -Path $TouchOut -Force | Out-Null
    New-Item -ItemType Directory -Path $AudioOut -Force | Out-Null

    Copy-Item -Path (Join-Path $RepoRoot "src\Guest\HvTouchHid\HvTouchHid.inf") -Destination $TouchOut -Force
    Copy-Item -Path (Join-Path $RepoRoot "build\bin\$Configuration\HvTouchHid\*") -Destination $TouchOut -Recurse -Force -ErrorAction SilentlyContinue

    Copy-Item -Path (Join-Path $RepoRoot "src\Guest\HvVirtualAudio\HvVirtualAudio.inf") -Destination $AudioOut -Force
    Copy-Item -Path (Join-Path $RepoRoot "build\bin\$Configuration\HvVirtualAudio\*") -Destination $AudioOut -Recurse -Force -ErrorAction SilentlyContinue
}

Copy-Item -Path (Join-Path $PSScriptRoot "Install-GuestDrivers.ps1") -Destination $DistGuest -Force

Write-Host "`n======================================================================" -ForegroundColor Green
Write-Host " Build Complete!"                                                       -ForegroundColor Green
Write-Host "   Host Bundle  : $DistHost"                                            -ForegroundColor Green
Write-Host "   Guest Bundle : $DistGuest"                                           -ForegroundColor Green
Write-Host "======================================================================" -ForegroundColor Green
