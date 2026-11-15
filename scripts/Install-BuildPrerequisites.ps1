<#
.SYNOPSIS
    Automatically detects, downloads, and installs all build prerequisites for VMStream:
      1. .NET 8.0 SDK (x64)
      2. Visual Studio 2022 Build Tools (MSBuild + MSVC v143 x64 C++20 compiler)
      3. Windows 10/11 SDK (headers & libraries for Winsock2 / AF_HYPERV / SetupAPI)
      4. Windows Driver Kit (WDK) headers & libraries (checks installed Windows Kits first,
         and automatically downloads & extracts official Microsoft.Windows.WDK.x64 +
         Microsoft.Windows.SDK.CPP NuGet packages if ntddk.h / vhf.h / portcls.h are missing).

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
$ProgressPreference = "SilentlyContinue"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$RepoRoot = Split-Path -Parent $PSScriptRoot
$LocalWdkKitRoot = Join-Path $RepoRoot "build\wdk_kit"

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

function Extract-NuGetPackageContent {
    param(
        [Parameter(Mandatory = $true)][string]$NupkgFile,
        [Parameter(Mandatory = $true)][string]$TargetRoot
    )
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($NupkgFile)
    try {
        foreach ($entry in $archive.Entries) {
            $fullName = $entry.FullName -replace '\\', '/'
            if ([string]::IsNullOrEmpty($entry.Name)) { continue }

            $relPath = $null
            if ($fullName.StartsWith("c/", [StringComparison]::OrdinalIgnoreCase)) {
                $relPath = $fullName.Substring(2)
            } elseif ($fullName.StartsWith("build/", [StringComparison]::OrdinalIgnoreCase)) {
                $relPath = $fullName
            }

            if ($relPath) {
                $destPath = Join-Path $TargetRoot ($relPath -replace '/', '\')
                $destDir  = Split-Path -Parent $destPath
                if (-not (Test-Path $destDir)) {
                    New-Item -ItemType Directory -Path $destDir -Force | Out-Null
                }
                [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destPath, $true)
            }
        }
    } finally {
        $archive.Dispose()
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
    $msvcRoot = $null
    if (Test-Path $vcToolsRoot) {
        $latestMsvc = Get-ChildItem -Path $vcToolsRoot -Directory | Sort-Object Name -Descending | Select-Object -First 1
        if ($latestMsvc) {
            $clCandidate = Join-Path $latestMsvc.FullName "bin\Hostx64\x64\cl.exe"
            $linkCandidate = Join-Path $latestMsvc.FullName "bin\Hostx64\x64\link.exe"
            if (Test-Path $clCandidate) {
                $clExe = $clCandidate
                $linkExe = $linkCandidate
                $msvcRoot = $latestMsvc.FullName
            }
        }
    }

    return [PSCustomObject]@{
        InstallPath = $installPath
        MsBuildExe  = if (Test-Path $msbuild) { $msbuild } else { $null }
        ClExe       = $clExe
        LinkExe     = $linkExe
        MsvcRoot    = $msvcRoot
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
# 3. Ensure Windows 10/11 User-Mode SDK is Installed
# ============================================================================
Write-Host "`n[Prereq 3/4] Checking Windows 10/11 SDK..." -ForegroundColor Yellow

$SystemKitsRoot = "${env:ProgramFiles(x86)}\Windows Kits\10"

function Find-WindowsSdkLayout {
    foreach ($root in @($SystemKitsRoot, $LocalWdkKitRoot)) {
        $incRoot = Join-Path $root "Include"
        if (-not (Test-Path $incRoot)) { continue }
        $vers = Get-ChildItem -Path $incRoot -Directory | Where-Object {
            $_.Name -match "^10\.0\.\d+\.\d+$" -and (Test-Path (Join-Path $_.FullName "um\winsock2.h"))
        } | Sort-Object Name -Descending
        if ($vers) {
            return [PSCustomObject]@{
                KitsRoot   = $root
                SdkVersion = $vers[0].Name
            }
        }
    }
    return $null
}

$SdkLayout = Find-WindowsSdkLayout
if (-not $SdkLayout) {
    Write-Host "      -> Windows SDK not found in system Kits; downloading official Microsoft.Windows.SDK.CPP NuGet packages..." -ForegroundColor Cyan
    New-Item -ItemType Directory -Path $LocalWdkKitRoot -Force | Out-Null
    $SdkCppNupkg = Join-Path $env:TEMP "microsoft.windows.sdk.cpp.10.0.26100.4204.nupkg"
    $SdkX64Nupkg = Join-Path $env:TEMP "microsoft.windows.sdk.cpp.x64.10.0.26100.4204.nupkg"
    Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.cpp/10.0.26100.4204/microsoft.windows.sdk.cpp.10.0.26100.4204.nupkg" -OutFile $SdkCppNupkg -UseBasicParsing
    Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.cpp.x64/10.0.26100.4204/microsoft.windows.sdk.cpp.x64.10.0.26100.4204.nupkg" -OutFile $SdkX64Nupkg -UseBasicParsing
    Extract-NuGetPackageContent -NupkgFile $SdkCppNupkg -TargetRoot $LocalWdkKitRoot
    Extract-NuGetPackageContent -NupkgFile $SdkX64Nupkg -TargetRoot $LocalWdkKitRoot
    $SdkLayout = Find-WindowsSdkLayout
}

if (-not $SdkLayout) {
    throw "Failed to locate or install Windows 10/11 SDK."
}

$KitsRoot   = $SdkLayout.KitsRoot
$SdkVersion = $SdkLayout.SdkVersion
Write-Host "      -> Using Windows SDK: $SdkVersion ($KitsRoot)" -ForegroundColor Green

# ============================================================================
# 4. Ensure Windows Driver Kit (WDK) Kernel Headers & Libraries
# ============================================================================
$WdkLayout = $null

if (-not $SkipWdk) {
    Write-Host "`n[Prereq 4/4] Checking Windows Driver Kit (WDK) for HvTouchHid.sys & HvVirtualAudio.sys..." -ForegroundColor Yellow

    function Find-WdkLayout {
        foreach ($root in @($SystemKitsRoot, $LocalWdkKitRoot)) {
            if (-not (Test-Path $root)) { continue }
            $incRoot = Join-Path $root "Include"
            $libRoot = Join-Path $root "Lib"
            if (-not (Test-Path $incRoot) -or -not (Test-Path $libRoot)) { continue }

            # In WDK, ntddk.h and portcls.h are in Include\<ver>\km, while vhf.h and hidport.h are in Include\<ver>\shared
            $incVerDir = Get-ChildItem -Path $incRoot -Directory -ErrorAction SilentlyContinue | Where-Object {
                (Test-Path (Join-Path $_.FullName "km\ntddk.h")) -and
                (Test-Path (Join-Path $_.FullName "km\portcls.h")) -and
                ((Test-Path (Join-Path $_.FullName "shared\vhf.h")) -or (Test-Path (Join-Path $_.FullName "km\vhf.h")))
            } | Sort-Object Name -Descending | Select-Object -First 1

            if (-not $incVerDir) { continue }

            $libVerDir = Get-ChildItem -Path $libRoot -Directory -ErrorAction SilentlyContinue | Where-Object {
                (Test-Path (Join-Path $_.FullName "km\x64\ntoskrnl.lib")) -and
                (Test-Path (Join-Path $_.FullName "km\x64\vhfkm.lib")) -and
                (Test-Path (Join-Path $_.FullName "km\x64\portcls.lib"))
            } | Sort-Object Name -Descending | Select-Object -First 1

            if (-not $libVerDir) { continue }

            # Locate KMDF headers (wdf.h) and libraries (WdfDriverEntry.lib) - prefer 1.31 if present, else latest matching version
            $kmdfIncRoot = Join-Path $incRoot "wdf\kmdf"
            $kmdfLibRoot = Join-Path $libRoot "wdf\kmdf\x64"
            if (-not (Test-Path $kmdfIncRoot) -or -not (Test-Path $kmdfLibRoot)) { continue }

            $kmdfVer = "1.31"
            if (-not (Test-Path (Join-Path $kmdfIncRoot "1.31\wdf.h")) -or -not (Test-Path (Join-Path $kmdfLibRoot "1.31\WdfDriverEntry.lib"))) {
                $latestKmdfDir = Get-ChildItem -Path $kmdfIncRoot -Directory -ErrorAction SilentlyContinue | Where-Object {
                    (Test-Path (Join-Path $_.FullName "wdf.h")) -and
                    (Test-Path (Join-Path $kmdfLibRoot "$($_.Name)\WdfDriverEntry.lib"))
                } | Sort-Object Name -Descending | Select-Object -First 1
                if ($latestKmdfDir) {
                    $kmdfVer = $latestKmdfDir.Name
                } else {
                    continue
                }
            }

            # Keep WDK shared directory pointing to $incVerDir\shared (which contains vhf.h and hidport.h)
            $sharedIncDir = Join-Path $incVerDir.FullName "shared"

            return [PSCustomObject]@{
                WdkRoot       = $root
                WdkVersion    = $incVerDir.Name
                KmIncDir      = Join-Path $incVerDir.FullName "km"
                KmCrtIncDir   = Join-Path $incVerDir.FullName "km\crt"
                SharedIncDir  = $sharedIncDir
                KmLibDir      = Join-Path $libVerDir.FullName "km\x64"
                KmdfVersion   = $kmdfVer
                KmdfIncDir    = Join-Path $kmdfIncRoot $kmdfVer
                KmdfLibDir    = Join-Path $kmdfLibRoot $kmdfVer
            }
        }
        return $null
    }

    $WdkLayout = Find-WdkLayout

    if (-not $WdkLayout) {
        Write-Host "      -> WDK kernel headers/libs not found in '$SystemKitsRoot'." -ForegroundColor Cyan
        Write-Host "      -> Auto-downloading official Microsoft WDK & SDK C++ NuGet packages to '$LocalWdkKitRoot'..." -ForegroundColor Cyan

        New-Item -ItemType Directory -Path $LocalWdkKitRoot -Force | Out-Null

        $NuGetPackages = @(
            @{
                Name = "Microsoft.Windows.SDK.CPP (10.0.26100.4204)"
                Url  = "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.cpp/10.0.26100.4204/microsoft.windows.sdk.cpp.10.0.26100.4204.nupkg"
                File = Join-Path $env:TEMP "microsoft.windows.sdk.cpp.10.0.26100.4204.nupkg"
            },
            @{
                Name = "Microsoft.Windows.WDK.x64 (10.0.26100.4204)"
                Url  = "https://api.nuget.org/v3-flatcontainer/microsoft.windows.wdk.x64/10.0.26100.4204/microsoft.windows.wdk.x64.10.0.26100.4204.nupkg"
                File = Join-Path $env:TEMP "microsoft.windows.wdk.x64.10.0.26100.4204.nupkg"
            }
        )

        foreach ($pkg in $NuGetPackages) {
            if (-not (Test-Path $pkg.File) -or ((Get-Item $pkg.File).Length -lt 1000000)) {
                Write-Host "      -> Downloading $($pkg.Name) from api.nuget.org ..." -ForegroundColor Cyan
                Invoke-WebRequest -Uri $pkg.Url -OutFile $pkg.File -UseBasicParsing
            }
            Write-Host "      -> Extracting $($pkg.Name) into '$LocalWdkKitRoot'..." -ForegroundColor Cyan
            Extract-NuGetPackageContent -NupkgFile $pkg.File -TargetRoot $LocalWdkKitRoot
        }

        $WdkLayout = Find-WdkLayout
    }

    if ($WdkLayout) {
        Write-Host "      -> Using WDK Version : $($WdkLayout.WdkVersion) ($($WdkLayout.WdkRoot))" -ForegroundColor Green
        Write-Host "      -> Using KMDF Version: $($WdkLayout.KmdfVersion)" -ForegroundColor Green
        Write-Host "      -> Kernel Include    : $($WdkLayout.KmIncDir)" -ForegroundColor Green
        Write-Host "      -> Kernel Libs (x64) : $($WdkLayout.KmLibDir)" -ForegroundColor Green
    } else {
        throw "Failed to locate or extract WDK kernel headers (ntddk.h, vhf.h, portcls.h)."
    }
}

# Return discovered toolchain metadata for Build-All.ps1
return [PSCustomObject]@{
    DotNetExe     = $DotNetExe
    VsInstallDir  = $VsInfo.InstallPath
    MsBuildExe    = $VsInfo.MsBuildExe
    ClExe         = $VsInfo.ClExe
    LinkExe       = $VsInfo.LinkExe
    MsvcRoot      = $VsInfo.MsvcRoot
    KitsRoot      = $KitsRoot
    SdkVersion    = $SdkVersion
    WdkVersion    = if ($WdkLayout) { $WdkLayout.WdkVersion }   else { $null }
    KmdfVersion   = if ($WdkLayout) { $WdkLayout.KmdfVersion }  else { "1.31" }
    WdkKmInc      = if ($WdkLayout) { $WdkLayout.KmIncDir }     else { $null }
    WdkKmCrtInc   = if ($WdkLayout) { $WdkLayout.KmCrtIncDir }  else { $null }
    WdkSharedInc  = if ($WdkLayout) { $WdkLayout.SharedIncDir } else { $null }
    WdkKmLib      = if ($WdkLayout) { $WdkLayout.KmLibDir }     else { $null }
    KmdfInc       = if ($WdkLayout) { $WdkLayout.KmdfIncDir }   else { $null }
    KmdfLib       = if ($WdkLayout) { $WdkLayout.KmdfLibDir }   else { $null }
}
