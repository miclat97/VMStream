@echo off
setlocal EnableExtensions

echo ======================================================================
echo  VMStream — Auto-Setup Prerequisites ^& Full Build Launcher
echo ======================================================================

:: Check for Administrator privileges (required to auto-install VS Build Tools / WDK)
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo [VMStream] Requesting Administrator privileges ^(UAC^) to install missing SDKs/Compilers...
    powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -WorkingDirectory '%~dp0' -Verb RunAs"
    exit /b 0
)

cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Build-All.ps1" %*
set BUILD_EXIT_CODE=%errorlevel%

echo.
if %BUILD_EXIT_CODE% equ 0 (
    echo [VMStream] Build finished successfully! Output is in: "%~dp0build\dist"
) else (
    echo [VMStream] Build failed with exit code %BUILD_EXIT_CODE%.
)

pause
exit /b %BUILD_EXIT_CODE%
