[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $FolderPath,
    [string] $FileName = 'smoke.txt',
    [ValidateRange(0, 65535)] [int] $ServicePort = 0,
    [switch] $RenderUi,
    [ValidateSet('win-x64', 'win-arm64')] [string] $Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repoRoot 'spikes/InstalledServiceProbe/PeerSync.InstalledProbe.csproj'
$probe = Join-Path $repoRoot "spikes/InstalledServiceProbe/bin/Release/net10.0-windows10.0.20348.0/$Runtime/PeerSync.InstalledProbe.exe"
$helper = Join-Path $PSScriptRoot 'Allow-PeerSync-TestTraffic.ps1'

if ((Get-Service PeerSync -ErrorAction Stop).Status -ne 'Running') {
    throw 'The installed PeerSync service is not Running.'
}
if ($ServicePort -eq 0) {
    $settingsPath = Join-Path $env:ProgramData 'PeerSync/config/settings.json'
    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    $ServicePort = [int] $settings.listenPort
    if ($ServicePort -lt 1 -or $ServicePort -gt 65535) { throw 'The installed service listen port is invalid.' }
}

& dotnet build $project -c Release -r $Runtime -p:RestoreLockedMode=true
if ($LASTEXITCODE -ne 0) { throw "Installed peer probe build failed: $LASTEXITCODE" }
if (-not (Test-Path -LiteralPath $probe -PathType Leaf)) { throw 'The installed peer probe executable was not built.' }

$probePort = 0
for ($attempt = 0; $attempt -lt 128; $attempt++) {
    $candidate = Get-Random -Minimum 20000 -Maximum 45001
    $udp = [Net.Sockets.Socket]::new([Net.Sockets.AddressFamily]::InterNetwork,
        [Net.Sockets.SocketType]::Dgram, [Net.Sockets.ProtocolType]::Udp)
    $tcp = [Net.Sockets.Socket]::new([Net.Sockets.AddressFamily]::InterNetwork,
        [Net.Sockets.SocketType]::Stream, [Net.Sockets.ProtocolType]::Tcp)
    try {
        $udp.ExclusiveAddressUse = $true
        $tcp.ExclusiveAddressUse = $true
        $udp.Bind([Net.IPEndPoint]::new([Net.IPAddress]::Any, $candidate))
        $tcp.Bind([Net.IPEndPoint]::new([Net.IPAddress]::Any, $candidate))
        $probePort = $candidate
        break
    }
    catch [Net.Sockets.SocketException] { }
    finally { $udp.Dispose(); $tcp.Dispose() }
}
if ($probePort -eq 0) { throw 'No free TCP/UDP port was found for the installed peer probe.' }

$rulePrefix = "PeerSync test $probePort 127.0.0.1"
try {
    & $helper -ServiceExecutable $probe -ListenPort $probePort -PeerAddress '127.0.0.1'
    if ($RenderUi) { & $probe $FolderPath $ServicePort $FileName $probePort '--render-ui' }
    else { & $probe $FolderPath $ServicePort $FileName $probePort }
    if ($LASTEXITCODE -ne 0) { throw "Installed peer probe failed: $LASTEXITCODE" }
}
finally {
    Get-NetFirewallRule -DisplayName "$rulePrefix*" -ErrorAction SilentlyContinue |
        Where-Object Group -eq 'PeerSync Test Setup' | Remove-NetFirewallRule
}
