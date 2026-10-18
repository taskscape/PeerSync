[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = Join-Path $root 'artifacts/msquic'
New-Item -ItemType Directory -Force "$output/include" | Out-Null
# Pin the reviewed API headers, not a moving branch. Headers carry the MIT licence.
$commit = '42376ae4b414ff80d8317aadb3e9e789400e7638'
foreach($file in @('msquic.h','msquic_winuser.h')) {
    Invoke-WebRequest "https://raw.githubusercontent.com/microsoft/msquic/$commit/src/inc/$file" -OutFile "$output/include/$file"
}
$vswhere = "${env:ProgramFiles(x86)}/Microsoft Visual Studio/Installer/vswhere.exe"
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual C++ x64 build tools are required for this optional binding spike' }
Import-Module (Join-Path $vs 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
& cl.exe /nologo /std:c++17 /W4 /WX /EHsc /DQUIC_API_ENABLE_INSECURE_FEATURES /I "$output/include" (Join-Path $root 'spikes/QuicPunch/native/shared-binding.cpp') /Fe:"$output/shared-binding.exe" /Fo:"$output/shared-binding.obj" /link crypt32.lib ws2_32.lib
if ($LASTEXITCODE -ne 0) { throw 'Native binding spike failed to compile' }
Write-Output "$output/shared-binding.exe"
