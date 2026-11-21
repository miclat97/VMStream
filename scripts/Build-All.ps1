<#
.SYNOPSIS
    Builds the complete VMStream solution (C# Host, C++20 Guest Service, and WDK Drivers)
    and automatically installs any missing prerequisites (.NET 8 SDK, MSVC C++ Build Tools,
    Windows SDK, and WDK) before compiling.

.DESCRIPTION
    1. Runs scripts\Install-BuildPrerequisites.ps1 (unless -SkipPrerequisites is passed)
       to verify or automatically install .NET 8 SDK, Visual Studio C++ Build Tools,
       Windows 10/11 SDK, and Windows Driver Kit (WDK).
    2. Compiles src\Host\HvConsole.Host\HvConsole.Host.csproj via dotnet publish (x64).
    3. Compiles src\Guest\HvGuestBridge\HvGuestBridge.vcxproj via MSBuild (with a direct
       MSVC cl.exe + link.exe response-file fallback).
    4. Compiles src\Guest\HvTouchHid (KMDF + VHF) and src\Guest\HvVirtualAudio (PortCls WaveRT)
       via MSBuild or direct MSVC /kernel cl.exe + link.exe /DRIVER against WDK headers and libs.
    5. Test-signs HvTouchHid.sys and HvVirtualAudio.sys and stages Host and Guest deployment
       bundles into build\dist\Host and build\dist\Guest.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [Parameter(Mandatory = $false)]
    [switch]$SkipWdkDrivers,

    [Parameter(Mandatory = $false)]
    [switch]$SkipPrerequisites
)

$ErrorActionPreference = "Stop"

$RepoRoot  = Split-Path -Parent $PSScriptRoot
$DistHost  = Join-Path $RepoRoot "build\dist\Host"
$DistGuest = Join-Path $RepoRoot "build\dist\Guest"

New-Item -ItemType Directory -Path $DistHost  -Force | Out-Null
New-Item -ItemType Directory -Path $DistGuest -Force | Out-Null

# ============================================================================
# 0. Bootstrap Missing Prerequisites (.NET 8 SDK, MSVC C++, WinSDK, WDK)
# ============================================================================
$PrereqScript = Join-Path $PSScriptRoot "Install-BuildPrerequisites.ps1"
if (-not $SkipPrerequisites -and (Test-Path $PrereqScript)) {
    $Toolchain = & $PrereqScript -SkipWdk:$SkipWdkDrivers
} else {
    $DotNetCmd  = Get-Command dotnet.exe  -ErrorAction SilentlyContinue
    $MsBuildCmd = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    $Toolchain = [PSCustomObject]@{
        DotNetExe    = if ($DotNetCmd)  { $DotNetCmd.Source }  else { "dotnet" }
        MsBuildExe   = if ($MsBuildCmd) { $MsBuildCmd.Source } else { $null }
        ClExe        = $null
        LinkExe      = $null
        MsvcRoot     = $null
        KitsRoot     = "${env:ProgramFiles(x86)}\Windows Kits\10"
        SdkVersion   = $null
        WdkVersion   = $null
        KmdfVersion  = "1.31"
        WdkKmInc     = $null
        WdkKmCrtInc  = $null
        WdkSharedInc = $null
        WdkKmLib     = $null
        KmdfInc      = $null
        KmdfLib      = $null
    }
}

function Find-RcExe {
    param(
        [string]$KitsRoot,
        [string]$SdkVersion
    )
    $candidates = @()
    if ($KitsRoot -and $SdkVersion) {
        $candidates += (Join-Path $KitsRoot "bin\$SdkVersion\x64\rc.exe")
    }
    if ($KitsRoot) {
        $candidates += (Join-Path $KitsRoot "bin\x64\rc.exe")
        $binRoot = Join-Path $KitsRoot "bin"
        if (Test-Path $binRoot) {
            Get-ChildItem -Path $binRoot -Directory -ErrorAction SilentlyContinue |
                Sort-Object Name -Descending |
                ForEach-Object { $candidates += (Join-Path $_.FullName "x64\rc.exe") }
        }
    }
    $sysBinRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    if (Test-Path $sysBinRoot) {
        Get-ChildItem -Path $sysBinRoot -Directory -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending |
            ForEach-Object { $candidates += (Join-Path $_.FullName "x64\rc.exe") }
    }
    $rcCmd = Get-Command rc.exe -ErrorAction SilentlyContinue
    if ($rcCmd) { $candidates += $rcCmd.Source }

    foreach ($c in $candidates) {
        if ($c -and (Test-Path $c)) { return $c }
    }
    return $null
}

function Invoke-CompileResource {
    param(
        [string]$RcExe,
        [string]$RcFile,
        [string]$OutRes,
        [string]$SharedIncludeDir,
        [string]$BuildConfig
    )
    if (-not $RcExe -or -not (Test-Path $RcFile)) { return $null }
    $rcArgs = @("/nologo", "/I", $SharedIncludeDir, "/fo", $OutRes)
    if ($BuildConfig -eq "Debug") {
        $rcArgs += "/d"
        $rcArgs += "_DEBUG"
    }
    $rcArgs += $RcFile
    & $RcExe @rcArgs
    if ($LASTEXITCODE -eq 0 -and (Test-Path $OutRes)) {
        return $OutRes
    }
    Write-Warning "rc.exe failed for $RcFile (exit code $LASTEXITCODE); continuing without embedded VERSIONINFO."
    return $null
}

$RcExe = Find-RcExe -KitsRoot $Toolchain.KitsRoot -SdkVersion $Toolchain.SdkVersion

Write-Host "`n======================================================================" -ForegroundColor Cyan
Write-Host " VMStream Build Pipeline - Configuration: $Configuration (x64)"           -ForegroundColor Cyan
Write-Host "======================================================================" -ForegroundColor Cyan

# ============================================================================
# 1. Build C# Host Application (HvConsole.Host)
# ============================================================================
$HostProject = Join-Path $RepoRoot "src\Host\HvConsole.Host\HvConsole.Host.csproj"
Write-Host "`n[1/4] Building Host Application (HvConsole.Host)..." -ForegroundColor Yellow

& $Toolchain.DotNetExe restore $HostProject
if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed for HvConsole.Host.csproj (Exit code: $LASTEXITCODE)."
}

& $Toolchain.DotNetExe publish $HostProject `
    -c $Configuration `
    -r win-x64 `
    --self-contained false `
    -o $DistHost

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed for HvConsole.Host.csproj (Exit code: $LASTEXITCODE)."
}

Copy-Item -Path (Join-Path $PSScriptRoot "Register-HvSocketServices.ps1") -Destination $DistHost -Force
Write-Host "      -> Host binaries staged to: $DistHost" -ForegroundColor Green

# ============================================================================
# 2. Build C++20 Guest Service (HvGuestBridge.exe)
# ============================================================================
$BridgeProj   = Join-Path $RepoRoot "src\Guest\HvGuestBridge\HvGuestBridge.vcxproj"
$BridgeBinDir = Join-Path $RepoRoot "build\bin\$Configuration\HvGuestBridge"
$BridgeObjDir = Join-Path $RepoRoot "build\obj\$Configuration\HvGuestBridge"
New-Item -ItemType Directory -Path $BridgeBinDir -Force | Out-Null
New-Item -ItemType Directory -Path $BridgeObjDir -Force | Out-Null

Write-Host "`n[2/4] Building Guest Service Daemon (HvGuestBridge.exe)..." -ForegroundColor Yellow

$BridgeBuilt = $false
if ($Toolchain.MsBuildExe) {
    try {
        & $Toolchain.MsBuildExe $BridgeProj `
            /p:Configuration=$Configuration `
            /p:Platform=x64 `
            /p:SolutionDir="$RepoRoot\" `
            /m /v:minimal
        if ($LASTEXITCODE -eq 0 -and (Test-Path (Join-Path $BridgeBinDir "HvGuestBridge.exe"))) {
            $BridgeBuilt = $true
        }
    } catch { }
}

if (-not $BridgeBuilt -and $Toolchain.ClExe -and $Toolchain.SdkVersion) {
    Write-Host "      -> Invoking direct MSVC cl.exe C++20 compiler for HvGuestBridge.exe..." -ForegroundColor Cyan
    $SdkInc  = Join-Path $Toolchain.KitsRoot "Include\$($Toolchain.SdkVersion)"
    $SdkLib  = Join-Path $Toolchain.KitsRoot "Lib\$($Toolchain.SdkVersion)"
    $MsvcInc = Join-Path $Toolchain.MsvcRoot "include"
    $MsvcLib = Join-Path $Toolchain.MsvcRoot "lib\x64"
    $SrcDir  = Join-Path $RepoRoot "src\Guest\HvGuestBridge"
    $ShdDir  = Join-Path $RepoRoot "src\Shared"
    $OutExe  = Join-Path $BridgeBinDir "HvGuestBridge.exe"

    $BridgeRes = Invoke-CompileResource `
        -RcExe $RcExe `
        -RcFile (Join-Path $SrcDir "HvGuestBridge.rc") `
        -OutRes (Join-Path $BridgeObjDir "HvGuestBridge.res") `
        -SharedIncludeDir $ShdDir `
        -BuildConfig $Configuration

    $RspFile = Join-Path $BridgeObjDir "compile_bridge.rsp"
    [string[]]$RspContent = @(
        "/nologo"
        "/std:c++20"
        "/EHsc"
        "/O2"
        "/MD"
        "/W3"
        "/DUNICODE"
        "/D_UNICODE"
        "/DWIN32_LEAN_AND_MEAN"
        "/DNOMINMAX"
        "/DNDEBUG"
        "/I`"$SrcDir`""
        "/I`"$ShdDir`""
        "/I`"$MsvcInc`""
        "/I`"$(Join-Path $SdkInc 'ucrt')`""
        "/I`"$(Join-Path $SdkInc 'um')`""
        "/I`"$(Join-Path $SdkInc 'shared')`""
        "/Fo`"$BridgeObjDir\\`""
        "/Fe`"$OutExe`""
        "`"$(Join-Path $SrcDir 'HvTransport.cpp')`""
        "`"$(Join-Path $SrcDir 'TouchBridgeWorker.cpp')`""
        "`"$(Join-Path $SrcDir 'AudioBridgeWorker.cpp')`""
        "`"$(Join-Path $SrcDir 'ServiceMain.cpp')`""
        "/link"
        "/LIBPATH:`"$MsvcLib`""
        "/LIBPATH:`"$(Join-Path $SdkLib 'ucrt\x64')`""
        "/LIBPATH:`"$(Join-Path $SdkLib 'um\x64')`""
        "ws2_32.lib"
        "advapi32.lib"
        "user32.lib"
        "setupapi.lib"
        "newdev.lib"
        "ole32.lib"
        "winmm.lib"
        "kernel32.lib"
    )
    if ($BridgeRes) { $RspContent += "`"$BridgeRes`"" }
    [System.IO.File]::WriteAllLines($RspFile, $RspContent)

    & $Toolchain.ClExe "@$RspFile"
    if ($LASTEXITCODE -eq 0 -and (Test-Path $OutExe)) {
        $BridgeBuilt = $true
    }
}

if (-not $BridgeBuilt) {
    throw "Failed to build HvGuestBridge.exe."
}

Copy-Item -Path (Join-Path $BridgeBinDir "HvGuestBridge.exe") -Destination $DistGuest -Force
Write-Host "      -> Built and staged: $DistGuest\HvGuestBridge.exe" -ForegroundColor Green

# ============================================================================
# 3 & 4. Build WDK Drivers (HvTouchHid.sys & HvVirtualAudio.sys)
# ============================================================================
if (-not $SkipWdkDrivers -and $Toolchain.WdkKmInc -and $Toolchain.WdkKmLib) {
    $WdkKmInc     = $Toolchain.WdkKmInc
    $WdkKmCrtInc  = $Toolchain.WdkKmCrtInc
    $WdkSharedInc = $Toolchain.WdkSharedInc
    $WdkKmLib     = $Toolchain.WdkKmLib
    $KmdfInc      = $Toolchain.KmdfInc
    $KmdfLib      = $Toolchain.KmdfLib
    $MsvcInc      = Join-Path $Toolchain.MsvcRoot "include"
    $MsvcLib      = Join-Path $Toolchain.MsvcRoot "lib\x64"
    $ShdDir       = Join-Path $RepoRoot "src\Shared"
    $SysSdkInc    = Join-Path $Toolchain.KitsRoot "Include\$($Toolchain.SdkVersion)"

    # ------------------------------------------------------------------------
    # 3. HvTouchHid.sys (KMDF + Virtual HID Framework VHF)
    # ------------------------------------------------------------------------
    $TouchBinDir = Join-Path $RepoRoot "build\bin\$Configuration\HvTouchHid"
    $TouchObjDir = Join-Path $RepoRoot "build\obj\$Configuration\HvTouchHid"
    New-Item -ItemType Directory -Path $TouchBinDir -Force | Out-Null
    New-Item -ItemType Directory -Path $TouchObjDir -Force | Out-Null

    Write-Host "`n[3/4] Building WDK Multi-Touch VHF Driver (HvTouchHid.sys)..." -ForegroundColor Yellow

    $TouchBuilt  = $false
    $TouchSrcDir = Join-Path $RepoRoot "src\Guest\HvTouchHid"
    $TouchObj    = Join-Path $TouchObjDir "Driver.obj"
    $TouchSys    = Join-Path $TouchBinDir "HvTouchHid.sys"
    $KmdfMinor   = ($Toolchain.KmdfVersion -split '\.')[1]

    if ($Toolchain.ClExe -and $Toolchain.LinkExe) {
        Write-Host "      -> Compiling HvTouchHid.sys with MSVC Kernel Compiler (cl.exe /kernel)..." -ForegroundColor Cyan

        $ClTouchRsp = Join-Path $TouchObjDir "cl_touch.rsp"
        [string[]]$ClTouchLines = @(
            "/nologo", "/c", "/Zp8", "/Gy", "/GF", "/Zl", "/GR-", "/EHs-c-", "/Zc:sizedDealloc-", "/kernel", "/GS", "/O2", "/std:c++20",
            "/D_WIN64", "/D_AMD64_", "/DAMD64", "/DPOOL_NX_OPTIN=1",
            "/DKMDF_VERSION_MAJOR=1", "/DKMDF_VERSION_MINOR=$KmdfMinor",
            "/I`"$TouchSrcDir`"",
            "/I`"$ShdDir`"",
            "/I`"$WdkKmInc`"",
            "/I`"$WdkSharedInc`"",
            "/I`"$WdkKmCrtInc`"",
            "/I`"$(Join-Path $SysSdkInc 'shared')`"",
            "/I`"$KmdfInc`"",
            "/I`"$MsvcInc`"",
            "/Fo`"$TouchObj`"",
            "`"$(Join-Path $TouchSrcDir 'Driver.cpp')`""
        )
        [System.IO.File]::WriteAllLines($ClTouchRsp, $ClTouchLines)

        & $Toolchain.ClExe "@$ClTouchRsp"
        if ($LASTEXITCODE -eq 0) {
            $TouchRes = Invoke-CompileResource `
                -RcExe $RcExe `
                -RcFile (Join-Path $TouchSrcDir "HvTouchHid.rc") `
                -OutRes (Join-Path $TouchObjDir "HvTouchHid.res") `
                -SharedIncludeDir $ShdDir `
                -BuildConfig $Configuration

            $LinkTouchRsp = Join-Path $TouchObjDir "link_touch.rsp"
            [string[]]$LinkTouchLines = @(
                "/NOLOGO", "/DRIVER", "/SUBSYSTEM:NATIVE", "/NODEFAULTLIB", "/INCREMENTAL:NO",
                "/OPT:REF", "/OPT:ICF", "/RELEASE", "/ENTRY:FxDriverEntry",
                "/MERGE:_TEXT=.text;_PAGE=PAGE",
                "/LIBPATH:`"$WdkKmLib`"",
                "/LIBPATH:`"$KmdfLib`"",
                "/LIBPATH:`"$MsvcLib`"",
                "/OUT:`"$TouchSys`"",
                "`"$TouchObj`"",
                "ntoskrnl.lib", "hal.lib", "wmilib.lib", "BufferOverflowFastFailK.lib",
                "WdfLdr.lib", "WdfDriverEntry.lib", "vhfkm.lib", "wdmsec.lib"
            )
            if ($TouchRes) { $LinkTouchLines += "`"$TouchRes`"" }
            [System.IO.File]::WriteAllLines($LinkTouchRsp, $LinkTouchLines)
            & $Toolchain.LinkExe "@$LinkTouchRsp"
            if ($LASTEXITCODE -eq 0 -and (Test-Path $TouchSys)) {
                $TouchBuilt = $true
            }
        }
    }

    if (-not $TouchBuilt) {
        throw "Failed to compile HvTouchHid.sys."
    }

    $TouchOut = Join-Path $DistGuest "HvTouchHid"
    New-Item -ItemType Directory -Path $TouchOut -Force | Out-Null
    $TouchInfDest = Join-Path $TouchOut "HvTouchHid.inf"
    Copy-Item -Path (Join-Path $RepoRoot "src\Guest\HvTouchHid\HvTouchHid.inf") -Destination $TouchInfDest -Force
    # Ensure KmdfLibraryVersion in staged INF matches the linked KMDF version
    $infText = [System.IO.File]::ReadAllText($TouchInfDest)
    $infText = $infText -replace 'KmdfLibraryVersion\s*=\s*\d+\.\d+', "KmdfLibraryVersion = $($Toolchain.KmdfVersion)"
    [System.IO.File]::WriteAllText($TouchInfDest, $infText)
    Copy-Item -Path $TouchSys -Destination $TouchOut -Force
    Write-Host "      -> Built and staged: $TouchOut\HvTouchHid.sys" -ForegroundColor Green

    # ------------------------------------------------------------------------
    # 4. HvVirtualAudio.sys (PortCls / WaveRT Virtual Audio Driver)
    # ------------------------------------------------------------------------
    $AudioBinDir = Join-Path $RepoRoot "build\bin\$Configuration\HvVirtualAudio"
    $AudioObjDir = Join-Path $RepoRoot "build\obj\$Configuration\HvVirtualAudio"
    New-Item -ItemType Directory -Path $AudioBinDir -Force | Out-Null
    New-Item -ItemType Directory -Path $AudioObjDir -Force | Out-Null

    Write-Host "`n[4/4] Building WDK PortCls/WaveRT Virtual Audio Driver (HvVirtualAudio.sys)..." -ForegroundColor Yellow

    $AudioBuilt  = $false
    $AudioSrcDir = Join-Path $RepoRoot "src\Guest\HvVirtualAudio"
    $AudioSys    = Join-Path $AudioBinDir "HvVirtualAudio.sys"

    if ($Toolchain.ClExe -and $Toolchain.LinkExe) {
        Write-Host "      -> Compiling HvVirtualAudio.sys with MSVC Kernel Compiler (cl.exe /kernel)..." -ForegroundColor Cyan

        $ClAudioRsp = Join-Path $AudioObjDir "cl_audio.rsp"
        [string[]]$ClAudioLines = @(
            "/nologo", "/c", "/Zp8", "/Gy", "/GF", "/Zl", "/GR-", "/EHs-c-", "/Zc:sizedDealloc-", "/kernel", "/GS", "/O2", "/std:c++20",
            "/D_WIN64", "/D_AMD64_", "/DAMD64", "/DPOOL_NX_OPTIN=1",
            "/I`"$AudioSrcDir`"",
            "/I`"$ShdDir`"",
            "/I`"$WdkKmInc`"",
            "/I`"$WdkSharedInc`"",
            "/I`"$WdkKmCrtInc`"",
            "/I`"$(Join-Path $SysSdkInc 'shared')`"",
            "/I`"$MsvcInc`"",
            "/Fo`"$AudioObjDir/`"",
            "`"$(Join-Path $AudioSrcDir 'Adapter.cpp')`"",
            "`"$(Join-Path $AudioSrcDir 'TopologyMiniport.cpp')`"",
            "`"$(Join-Path $AudioSrcDir 'WaveRTMiniport.cpp')`""
        )
        [System.IO.File]::WriteAllLines($ClAudioRsp, $ClAudioLines)

        & $Toolchain.ClExe "@$ClAudioRsp"
        if ($LASTEXITCODE -eq 0) {
            $AudioRes = Invoke-CompileResource `
                -RcExe $RcExe `
                -RcFile (Join-Path $AudioSrcDir "HvVirtualAudio.rc") `
                -OutRes (Join-Path $AudioObjDir "HvVirtualAudio.res") `
                -SharedIncludeDir $ShdDir `
                -BuildConfig $Configuration

            $LinkAudioRsp = Join-Path $AudioObjDir "link_audio.rsp"
            [string[]]$LinkAudioLines = @(
                "/NOLOGO", "/DRIVER:WDM", "/SUBSYSTEM:NATIVE", "/NODEFAULTLIB", "/INCREMENTAL:NO",
                "/OPT:REF", "/OPT:ICF", "/RELEASE", "/ENTRY:GsDriverEntry",
                "/MERGE:_TEXT=.text;_PAGE=PAGE",
                "/LIBPATH:`"$WdkKmLib`"",
                "/LIBPATH:`"$MsvcLib`"",
                "/OUT:`"$AudioSys`"",
                "`"$(Join-Path $AudioObjDir 'Adapter.obj')`"",
                "`"$(Join-Path $AudioObjDir 'TopologyMiniport.obj')`"",
                "`"$(Join-Path $AudioObjDir 'WaveRTMiniport.obj')`"",
                "ntoskrnl.lib", "hal.lib", "wmilib.lib", "BufferOverflowFastFailK.lib",
                "portcls.lib", "stdunk.lib", "ksguid.lib", "wdmsec.lib"
            )
            if ($AudioRes) { $LinkAudioLines += "`"$AudioRes`"" }
            [System.IO.File]::WriteAllLines($LinkAudioRsp, $LinkAudioLines)
            & $Toolchain.LinkExe "@$LinkAudioRsp"
            if ($LASTEXITCODE -eq 0 -and (Test-Path $AudioSys)) {
                $AudioBuilt = $true
            }
        }
    }

    if (-not $AudioBuilt) {
        throw "Failed to compile HvVirtualAudio.sys."
    }

    $AudioOut = Join-Path $DistGuest "HvVirtualAudio"
    New-Item -ItemType Directory -Path $AudioOut -Force | Out-Null
    Copy-Item -Path (Join-Path $RepoRoot "src\Guest\HvVirtualAudio\HvVirtualAudio.inf") -Destination $AudioOut -Force
    Copy-Item -Path $AudioSys -Destination $AudioOut -Force
    Write-Host "      -> Built and staged: $AudioOut\HvVirtualAudio.sys" -ForegroundColor Green

    # Automatically test-sign the staged .sys drivers so they are ready for Guest installation
    try {
        $CertSubject = "CN=VMStream WDK Test Certificate"
        $cert = Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue | Where-Object { $_.Subject -eq $CertSubject } | Select-Object -First 1
        if ($null -eq $cert) {
            $cert = New-SelfSignedCertificate `
                -Type CodeSigningCert `
                -Subject $CertSubject `
                -KeyUsage DigitalSignature `
                -FriendlyName "VMStream WDK Driver Signing Certificate" `
                -CertStoreLocation "Cert:\CurrentUser\My" `
                -NotAfter (Get-Date).AddYears(10)
        }
        if ($null -ne $cert) {
            Export-Certificate -Cert $cert -FilePath (Join-Path $DistGuest "VMStreamTestCert.cer") -Force | Out-Null
            Set-AuthenticodeSignature -FilePath (Join-Path $TouchOut "HvTouchHid.sys") -Certificate $cert -HashAlgorithm SHA256 | Out-Null
            Set-AuthenticodeSignature -FilePath (Join-Path $AudioOut "HvVirtualAudio.sys") -Certificate $cert -HashAlgorithm SHA256 | Out-Null
            Write-Host "      -> Test-signed HvTouchHid.sys and HvVirtualAudio.sys (Cert: $DistGuest\VMStreamTestCert.cer)" -ForegroundColor Green
        }
    } catch {
        Write-Host "      -> Note: Drivers will be test-signed inside the Guest VM by Install-GuestDrivers.ps1." -ForegroundColor Gray
    }
}

Copy-Item -Path (Join-Path $PSScriptRoot "Install-GuestDrivers.ps1") -Destination $DistGuest -Force

Write-Host "`n======================================================================" -ForegroundColor Green
Write-Host " VMStream Build Complete!"                                              -ForegroundColor Green
Write-Host "   Host Bundle  : $DistHost"                                            -ForegroundColor Green
Write-Host "   Guest Bundle : $DistGuest"                                           -ForegroundColor Green
Write-Host "======================================================================" -ForegroundColor Green
