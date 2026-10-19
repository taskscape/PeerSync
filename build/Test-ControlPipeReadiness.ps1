[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. "$PSScriptRoot/ProbeReadiness.ps1"
$exe = Join-Path $root 'spikes/ControlPipe/bin/Release/net10.0-windows10.0.20348.0/ControlPipe.exe'
$out = Join-Path $root ('artifacts/p0/pipe-readiness/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $out | Out-Null
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$server = $null
function Start-PipeProcess([string[]]$Arguments, [string]$Label) {
    $info = [Diagnostics.ProcessStartInfo]::new($exe)
    $info.UseShellExecute=$false; $info.CreateNoWindow=$true; $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
    foreach($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($info)
    [pscustomobject]@{Process=$process;Label=$Label;Stdout=$process.StandardOutput.ReadToEndAsync();Stderr=$process.StandardError.ReadToEndAsync()}
}
function Stop-PipeProcess($Probe) {
    if (-not $Probe) { return }
    try {
        if (-not $Probe.Process.HasExited) { $Probe.Process.Kill($true); if (-not $Probe.Process.WaitForExit(10000)) { throw 'Owned readiness probe did not stop' } }
        $Probe.Stdout.GetAwaiter().GetResult() | Set-Content (Join-Path $out "$($Probe.Label).stdout.log")
        $Probe.Stderr.GetAwaiter().GetResult() | Set-Content (Join-Path $out "$($Probe.Label).stderr.log")
    } finally { $Probe.Process.Dispose() }
}
try {
    $sid = $identity.User.Value
    $pipe = 'PeerSync.Readiness.' + [Guid]::NewGuid().ToString('N')
    $ready = Join-Path $out 'legacy-ready.json'
    $server = Start-PipeProcess @('server',$pipe,$sid,$ready,'5000') 'legacy-server'
    Start-Sleep -Milliseconds 1000
    $early = [IO.Pipes.NamedPipeClientStream]::new('.', $pipe, [IO.Pipes.PipeDirection]::InOut)
    $rejected = $false
    try { $early.Connect(3000) } catch [TimeoutException] { $rejected = $true } finally { $early.Dispose() }
    if (-not $rejected) { throw 'Controlled delayed server did not reproduce the old one-second startup race' }
    Stop-PipeProcess $server; $server = $null

    $pipe = 'PeerSync.Readiness.' + [Guid]::NewGuid().ToString('N')
    $ready = Join-Path $out 'actual-ready.json'
    # A visible stale/wrong-PID file must not release the client before readiness.
    @{processId=0} | ConvertTo-Json | Set-Content -LiteralPath $ready
    $server = Start-PipeProcess @('server',$pipe,$sid,$ready,'5000') 'ready-server'
    $wait = [Diagnostics.Stopwatch]::StartNew()
    Wait-ProbeReadyFile $server $ready
    if ($wait.ElapsedMilliseconds -lt 4000) { throw 'Wrong-PID readiness was accepted prematurely' }
    $client = Start-PipeProcess @('client',$pipe,$sid) 'ready-client'
    try {
        if (-not $client.Process.WaitForExit(15000)) { throw 'Ready client exceeded the existing RPC deadline' }
        if ($client.Process.ExitCode -ne 0) { throw 'Ready client failed; inspect readiness stdout/stderr' }
        $response = $client.Stdout.GetAwaiter().GetResult() | ConvertFrom-Json
        if (-not $response.ownerVerified -or -not $response.callerVerified -or $response.returnedCallerSid -ne $sid) { throw 'Ready client did not preserve owner/caller verification' }
    } finally { Stop-PipeProcess $client }
    @{status='passed';legacyEarlyConnectRejected=$rejected;wrongPidIgnored=$true;actualOwnerAndCallerVerified=$true;readyWaitMilliseconds=$wait.ElapsedMilliseconds} |
        ConvertTo-Json | Set-Content (Join-Path $out 'summary.json')
    Write-Host 'Control pipe readiness: delayed startup reproduced; PID-bound readiness preserves owner/caller checks'
}
finally { Stop-PipeProcess $server; $identity.Dispose() }
