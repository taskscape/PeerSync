@echo off
setlocal
set "PSP=%ProgramFiles%\PeerSync\cli\psp.exe"
if not exist "%PSP%" (
    echo PeerSync CLI was not found at "%PSP%".
    echo Install PeerSync first, then run this collector again.
    pause
    exit /b 2
)

pushd "%~dp0"
if errorlevel 1 (
    echo Could not open this collector's folder.
    pause
    exit /b 3
)

echo Collecting a sanitized PeerSync diagnostic ZIP beside this collector...
"%PSP%" diagnostics
set "RESULT=%ERRORLEVEL%"
popd

if not "%RESULT%"=="0" (
    echo Diagnostic collection failed with exit code %RESULT%.
    pause
    exit /b %RESULT%
)

echo The new PeerSync-diagnostics-*.zip is beside this collector.
echo Transfer the ZIP through a trusted channel if PeerSync sync is stalled.
pause
exit /b 0
