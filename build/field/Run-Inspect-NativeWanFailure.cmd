@echo off
setlocal
set "SCRIPT=%~dp0Inspect-NativeWanFailure.ps1"
if not exist "%SCRIPT%" (
    echo Keep Inspect-NativeWanFailure.ps1 beside this launcher.
    pause
    exit /b 2
)
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%"
echo.
echo Send the NativeWAN-failure-*.txt file printed above through Taildrop.
pause
