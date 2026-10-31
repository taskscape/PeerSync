$ErrorActionPreference = 'Stop'
$cli = Join-Path $env:ProgramFiles 'PeerSync\cli\psp.exe'
if (-not [Environment]::Is64BitOperatingSystem) { throw 'This package requires x64 Windows.' }
if (-not (Test-Path -LiteralPath $cli -PathType Leaf)) { throw "Install PeerSync first: $cli" }

'ACTIVE_ADAPTERS'
Get-NetAdapter -Physical | Where-Object Status -eq 'Up' |
    Select-Object Name, InterfaceDescription, LinkSpeed | Format-Table -AutoSize
'ROUTE_TO_SERVER'
$serverAddress = Resolve-DnsName sync.taskscape.com -Type A |
    Where-Object IPAddress | Select-Object -First 1 -ExpandProperty IPAddress
"SERVER_ADDRESS=$serverAddress"
Find-NetRoute -RemoteIPAddress $serverAddress |
    Select-Object -First 1 IPAddress, InterfaceAlias, NextHop | Format-List
'RENDEZVOUS_PROBE'
& $cli probe sync.taskscape.com:99
$probeExit = $LASTEXITCODE
"PROBE_EXIT=$probeExit"
if ($probeExit -ne 0) { exit $probeExit }
'CLIENT_STATUS'
& $cli status --json
exit $LASTEXITCODE
