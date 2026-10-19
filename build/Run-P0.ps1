[CmdletBinding()]
param([int]$WatcherOperations = 100000, [long]$TransferBytes = 1073741824, [switch]$SkipWatcher)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'artifacts/p0'
New-Item -ItemType Directory -Force $out | Out-Null
$tfm = 'net10.0-windows10.0.20348.0'
. "$PSScriptRoot/ProbeReadiness.ps1"

function Get-Spike([string]$Name) {
    Join-Path $root "spikes/$Name/bin/Release/$tfm/$Name.exe"
}
function Start-Probe([string]$Name, [string[]]$Arguments, [string]$Label) {
    $info = [Diagnostics.ProcessStartInfo]::new((Get-Spike $Name))
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($arg in $Arguments) { $info.ArgumentList.Add($arg) }
    $process = [Diagnostics.Process]::Start($info)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    [pscustomobject]@{Process=$process;Stdout=$stdout;Stderr=$stderr;Label=$Label;Name=$Name;Disposed=$false}
}
function Finish-Probe($Probe, [int]$Seconds = 180, [switch]$ExpectFailure, [int[]]$ExpectedExits = @(0)) {
    try {
        if (-not $Probe.Process.WaitForExit($Seconds * 1000)) {
            $Probe.Process.Kill($true)
            $Probe.Process.WaitForExit()
            throw "$($Probe.Label) timed out"
        }
        $Probe.Stdout.GetAwaiter().GetResult() | Set-Content (Join-Path $out "$($Probe.Label).stdout.log")
        $Probe.Stderr.GetAwaiter().GetResult() | Set-Content (Join-Path $out "$($Probe.Label).stderr.log")
        $code = $Probe.Process.ExitCode
        if (($ExpectFailure -and $code -ne 1) -or (-not $ExpectFailure -and $code -notin $ExpectedExits)) {
            throw "$($Probe.Label) returned unexpected exit code $code; see artifacts/p0"
        }
        Write-Host "$($Probe.Label): expected exit $code"
    }
    finally {
        if ($Probe.Name -eq 'QuicBasics') {
            $logs = Join-Path (Split-Path (Get-Spike 'QuicBasics') -Parent) "logs/$($Probe.Process.Id)"
            if (Test-Path -LiteralPath $logs) { Copy-Item -LiteralPath $logs -Destination (Join-Path $out "$($Probe.Label)-logs") -Recurse -Force }
        }
        $Probe.Process.Dispose()
        $Probe.Disposed = $true
    }
}
function Stop-Probe($Probe) {
    if ($null -eq $Probe -or $Probe.Disposed) { return }
    if (-not $Probe.Process.HasExited) { $Probe.Process.Kill($true); $Probe.Process.WaitForExit() }
    $Probe.Stdout.GetAwaiter().GetResult() | Set-Content (Join-Path $out "$($Probe.Label).stdout.log")
    $Probe.Stderr.GetAwaiter().GetResult() | Set-Content (Join-Path $out "$($Probe.Label).stderr.log")
    $Probe.Process.Dispose()
    $Probe.Disposed = $true
}
function Wait-Tcp([int]$Port, $Probe) {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        if ($Probe.Process.HasExited) { throw "Server exited before becoming ready: $($Probe.Label)" }
        $socket = [Net.Sockets.TcpClient]::new()
        try { $socket.Connect('127.0.0.1', $Port); return } catch [Net.Sockets.SocketException] { }
        finally { $socket.Dispose() }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Server not ready on TCP $Port"
}

$server = $null
try {
    foreach ($address in @('127.0.0.1','::1')) {
        $label = if ($address -eq '::1') {'quic-ipv6'} else {'quic-ipv4'}
        # Ask the server for a free port and read its readiness line before draining stdout.
        $info = [Diagnostics.ProcessStartInfo]::new((Get-Spike 'QuicBasics'))
        $info.UseShellExecute=$false; $info.CreateNoWindow=$true
        $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
        foreach($arg in @('listen','0',"$TransferBytes")) { $info.ArgumentList.Add($arg) }
        $process = [Diagnostics.Process]::Start($info)
        $readyTask = $process.StandardOutput.ReadLineAsync()
        if (-not $readyTask.Wait(20000)) { $process.Kill($true); throw 'QUIC readiness timed out' }
        $ready = $readyTask.Result
        if ($ready -notmatch '^READY (\d+)$') { throw "QUIC server did not start: $ready" }
        $port = $Matches[1]
        $server = [pscustomobject]@{Process=$process;Stdout=$process.StandardOutput.ReadToEndAsync();Stderr=$process.StandardError.ReadToEndAsync();Label="$label-server";Name='QuicBasics';Disposed=$false}
        Finish-Probe (Start-Probe 'QuicBasics' @('dial',$address,$port,"$TransferBytes") "$label-client")
        Finish-Probe $server
        $server = $null
        $sent = Get-Content (Join-Path $out "$label-client.stdout.log") | ConvertFrom-Json
        $received = Get-Content (Join-Path $out "$label-server.stdout.log") | ConvertFrom-Json
        if ($sent.sessionId -ne $received.sessionId -or $sent.logDirectory -eq $received.logDirectory -or $sent.localDeviceId -ne $received.remoteDeviceId -or $sent.remoteDeviceId -ne $received.localDeviceId -or $sent.bytes -ne $received.bytes) {
            throw "$label correlation or certificate identity mismatch between processes"
        }
        foreach($peer in @($sent,$received)) {
            $events = Get-Content (Join-Path $peer.logDirectory 'debug.jsonl') | ConvertFrom-Json
            if (-not @($events | Where-Object { $_.eventName -eq 'transfer.completed' -and $_.sessionId -eq $sent.sessionId }).Count) { throw "$label completion event missing from peer journal" }
        }
    }
    Finish-Probe (Start-Probe 'QuicPunch' @() 'quic-source-port') 30 -ExpectedExits @(0,3)
    Finish-Probe (Start-Probe 'QuicPunch' @('--tcp') 'tcp-source-port') 30 -ExpectedExits @(0,3)
    $native = Join-Path $root 'artifacts/msquic/shared-binding.exe'
    if (Test-Path -LiteralPath $native) {
        Finish-Probe (Start-Probe 'QuicPunch' @('--native',$native) 'msquic-source-port') 30 -ExpectedExits @(0,1)
    }
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $pipe = 'PeerSync.Spike.' + [Guid]::NewGuid().ToString('N')
    $readyFile = Join-Path $out ('pipe-ready-' + [Guid]::NewGuid().ToString('N') + '.json')
    $server = Start-Probe 'ControlPipe' @('server',$pipe,$sid,$readyFile) 'pipe-server'
    Wait-ProbeReadyFile $server $readyFile
    Finish-Probe (Start-Probe 'ControlPipe' @('client',$pipe,$sid) 'pipe-client') 20
    Finish-Probe (Start-Probe 'ControlPipe' @('client',$pipe,'S-1-5-19') 'pipe-wrong-owner') 20 -ExpectFailure
    Stop-Probe $server
    $server = $null

    $existingTcp = Get-NetTCPConnection -LocalPort 99 -State Listen -ErrorAction SilentlyContinue
    $existingUdp = Get-NetUDPEndpoint -LocalPort 99 -ErrorAction SilentlyContinue
    if ($existingTcp -or $existingUdp) { throw 'Port 99 already in use; refusing to interfere' }
    $server = Start-Probe 'Rendezvous99' @('server','99',(Join-Path $out 'rendezvous-logs')) 'rendezvous-server'
    Wait-Tcp 99 $server
    Finish-Probe (Start-Probe 'Rendezvous99' @('client','https://localhost:99','2') 'rendezvous-http2') 30
    Finish-Probe (Start-Probe 'Rendezvous99' @('client','https://localhost:99','3') 'rendezvous-http3') 30
    Stop-Probe $server
    $server = $null
    Finish-Probe (Start-Probe 'Rendezvous99' @('client','https://127.0.0.1:99','2') 'rendezvous-unreachable') 30 -ExpectFailure
    $unreachable = Get-Content (Join-Path $out 'rendezvous-unreachable.stdout.log') | ConvertFrom-Json
    if ($unreachable.mutualTls -or $unreachable.hResult -ge 0 -or $unreachable.nativeError -ne 10061) {
        throw 'Unreachable HTTP/2 probe did not distinguish HRESULT from native WSAECONNREFUSED'
    }
    if (-not $SkipWatcher) { Finish-Probe (Start-Probe 'WatcherStress' @("$WatcherOperations") 'watcher') 180 }
    $watcherCount = if ($SkipWatcher) { 0 } else { $WatcherOperations }
    @{status='local-probes-completed';finishedUtc=[DateTime]::UtcNow.ToString('O');transferBytes=$TransferBytes;watcherOperations=$watcherCount;watcherRuntimeChecks=(-not $SkipWatcher);scope='single-host native Windows; no NAT or second physical PC';gateP0Passed=$false} | ConvertTo-Json | Set-Content (Join-Path $out 'summary.json')
}
catch {
    @{status='failed';finishedUtc=[DateTime]::UtcNow.ToString('O');error=$_.Exception.Message} | ConvertTo-Json | Set-Content (Join-Path $out 'summary.json')
    throw
}
finally { Stop-Probe $server }
