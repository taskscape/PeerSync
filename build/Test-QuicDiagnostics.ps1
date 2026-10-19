[CmdletBinding()]
param([string]$ExecutablePath)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. "$PSScriptRoot/QuicDiagnosticEvidence.ps1"
& "$PSScriptRoot/Test-QuicDiagnosticEvidence.ps1"
$executable = if ($ExecutablePath) { (Resolve-Path -LiteralPath $ExecutablePath).ProviderPath }
    else { Join-Path $root 'spikes/QuicBasics/bin/Release/net10.0-windows10.0.20348.0/QuicBasics.exe' }
$evidence = Join-Path $root 'artifacts/p0/quic-timeout'
New-Item -ItemType Directory -Force $evidence | Out-Null

function Start-Quic([string[]]$Arguments) {
    $info = [Diagnostics.ProcessStartInfo]::new($executable)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($info)
    [pscustomobject]@{ Process=$process; Error=$process.StandardError.ReadToEndAsync() }
}

$server = $null
$client = $null
try {
    foreach ($arguments in @(@('dial','127.0.0.1','47821'), @('listen','47821','1073741824\'))) {
        $invalid = Start-Quic $arguments
        try {
            $stdout = $invalid.Process.StandardOutput.ReadToEndAsync()
            if (-not $invalid.Process.WaitForExit(10000)) { throw 'Invalid arguments hung the probe' }
            if ($invalid.Process.ExitCode -ne 2) { throw 'Expected usage error for malformed arguments' }
            $invalid.Error.GetAwaiter().GetResult() | Set-Content (Join-Path $evidence 'invalid-arguments.log')
            $stdout.GetAwaiter().GetResult() | Out-Null
        } finally {
            if (-not $invalid.Process.HasExited) { $invalid.Process.Kill($true); $invalid.Process.WaitForExit() }
            $invalid.Process.Dispose()
        }
    }

    # One TiB cannot finish in six seconds on the reference PC. No file or TiB buffer is allocated.
    $server = Start-Quic @('listen','0','1099511627776','--timeout-seconds','6')
    $ready = $server.Process.StandardOutput.ReadLineAsync()
    if (-not $ready.Wait(20000) -or $ready.Result -notmatch '^READY (\d+)$') { throw 'Receiver did not become ready' }
    $port = $Matches[1]
    $serverOutput = $server.Process.StandardOutput.ReadToEndAsync()
    # Listening must not consume the transfer budget.
    Start-Sleep -Seconds 7
    if ($server.Process.HasExited) { throw 'Receiver consumed transfer timeout while waiting for a connection' }
    $client = Start-Quic @('dial','127.0.0.1',$port,'1099511627776','--timeout-seconds','30')
    $clientOutput = $client.Process.StandardOutput.ReadToEndAsync()
    foreach ($probe in @($server,$client)) {
        if (-not $probe.Process.WaitForExit(40000)) { throw 'Timeout regression probe hung' }
        if ($probe.Process.ExitCode -ne 1) { throw 'Expected an explicit timeout/peer-close failure' }
    }
    $server.Error.GetAwaiter().GetResult() | Set-Content (Join-Path $evidence 'server.stderr.log')
    $client.Error.GetAwaiter().GetResult() | Set-Content (Join-Path $evidence 'client.stderr.log')
    $serverOutput.GetAwaiter().GetResult() | Set-Content (Join-Path $evidence 'server.stdout.log')
    $clientOutput.GetAwaiter().GetResult() | Set-Content (Join-Path $evidence 'client.stdout.log')
    $serverLogs = Join-Path (Split-Path $executable -Parent) "logs/$($server.Process.Id)"
    $clientLogs = Join-Path (Split-Path $executable -Parent) "logs/$($client.Process.Id)"
    Copy-Item -LiteralPath (Join-Path $serverLogs 'debug.jsonl') -Destination (Join-Path $evidence 'server.jsonl')
    Copy-Item -LiteralPath (Join-Path $clientLogs 'debug.jsonl') -Destination (Join-Path $evidence 'client.jsonl')
    $events = @(Select-QuicInvocationEvents -Events @(Get-Content (Join-Path $serverLogs 'debug.jsonl') | ConvertFrom-Json) -ExpectedProcessId $server.Process.Id `
        -StartedUtc $server.Process.StartTime.ToUniversalTime() -ExitedUtc $server.Process.ExitTime.ToUniversalTime() -Role listen)
    $failure = @($events | Where-Object eventName -eq 'transfer.failed')
    if ($failure.Count -ne 1 -or $failure[0].errorCode -ne 'ProbeTimeout' -or $failure[0].operation -ne 'receive' -or $failure[0].bytes -le 0 -or $null -eq $failure[0].hResult) {
        throw 'Timeout did not preserve its stage, progress and HRESULT'
    }
    $progress = @($events | Where-Object eventName -eq 'transfer.progress')
    if ($progress.Count -eq 0 -or $progress[0].bytes -le 0 -or $progress[0].bytes -gt 1048576 -or $progress[0].bytes -gt $failure.bytes -or $progress[0].elapsedMs -lt 0) {
        throw 'Initial data progress event missing or invalid'
    }
    for ($i = 1; $i -lt $progress.Count; $i++) {
        if ($progress[$i].bytes -le $progress[$i - 1].bytes -or $progress[$i].elapsedMs - $progress[$i - 1].elapsedMs -lt 4900) {
            throw 'Progress counters or five-second cadence are invalid'
        }
    }
    if ($null -ne $failure.nativeError) { throw 'HRESULT was incorrectly labelled a native socket error' }
    Write-Host 'QUIC delayed start, transfer deadline, progress, error metadata and argument validation passed'
}
finally {
    foreach ($probe in @($server,$client)) {
        if ($null -eq $probe) { continue }
        if (-not $probe.Process.HasExited) { $probe.Process.Kill($true); $probe.Process.WaitForExit() }
        $probe.Process.Dispose()
    }
}
