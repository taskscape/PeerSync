@echo off
setlocal
set "SCRIPT=%~dp0Test-NativeWanWithoutOverlay.ps1"
set "FOLDER=C:\Users\offic\Documents\PeerSync-WAN-Test"

if not exist "%SCRIPT%" (
    echo Missing %SCRIPT%
    echo Keep this launcher beside Test-NativeWanWithoutOverlay.ps1.
    pause
    exit /b 2
)

echo PeerSync native WAN field check
echo Leave the mobile hotspot and Tailscale connected.
echo This test takes about five minutes and cleans up its firewall rules.
echo.
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" -Folder "%FOLDER%"
set "RESULT=%ERRORLEVEL%"
echo.
echo PeerSync test exit code: %RESULT%
echo Copy the READY, CLEANED, and REPORT lines before closing this window.
pause
exit /b %RESULT%
