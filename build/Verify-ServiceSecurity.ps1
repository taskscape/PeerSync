[CmdletBinding()]
param([switch]$SkipCaseCapacityStress)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'artifacts/p0-services'
$engineProcess = $null
$verificationRunId = [Guid]::NewGuid().ToString('N')
$startedUtc = [DateTime]::UtcNow.ToString('O')
Push-Location $root
try {
    New-Item -ItemType Directory -Force $out | Out-Null
    @{status='running';runId=$verificationRunId;startedUtc=$startedUtc;parallel=$true;caseCapacityStress=(-not $SkipCaseCapacityStress);watcherRuntimeChecks=$false} | ConvertTo-Json | Set-Content (Join-Path $out 'parallel-summary.json')
    & dotnet build PeerSync.slnx -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    # All active test files are immutable copied build output. P0 publishes only
    # Core/SpikeSupport probe outputs and never rebuilds this test output directory.
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.WorkingDirectory = $root
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('test','--project','tests/PeerSync.Engine.Tests','-c','Release','--no-build','--report-trx','--results-directory','artifacts/tests/native-engine','--output','Detailed','--long-running','60','--xunit-diagnostics','on')) {
        $start.ArgumentList.Add($argument)
    }
    if ($SkipCaseCapacityStress) {
        $start.ArgumentList.Add('--filter-not-method')
        $start.ArgumentList.Add('PeerSync.Engine.Tests.CaseCopyRecoveryTests.StartupCapacityBackpressureDrainsAdmittedSourcesBeforeRepairingTheRest')
    }
    $engineClock = [Diagnostics.Stopwatch]::StartNew()
    $engineProcess = [Diagnostics.Process]::Start($start)
    $engineOutput = $engineProcess.StandardOutput.ReadToEndAsync()
    $engineError = $engineProcess.StandardError.ReadToEndAsync()
    # The required x64 canonical job owns the full 100,000-operation watcher
    # stress. Duplicating it here competes with Engine flush tests for disk I/O.
    & "$PSScriptRoot/Run-P0.ps1" -SkipWatcher
    & "$PSScriptRoot/Run-ServiceP0.ps1"
    $remaining = [Math]::Max(0, 660000 - [int]$engineClock.ElapsedMilliseconds)
    if (-not $engineProcess.WaitForExit($remaining)) { throw 'Native Engine scope exceeded its eleven-minute deadline' }
    if ($engineProcess.ExitCode -ne 0) { throw "Native Engine scope failed ($($engineProcess.ExitCode)); see engine stdout/stderr and TRX" }
    @{status='passed';runId=$verificationRunId;startedUtc=$startedUtc;parallel=$true;nativeEngineExitCode=$engineProcess.ExitCode;caseCapacityStress=(-not $SkipCaseCapacityStress);caseCapacityCoverage='required Server 2022 job and default local runner';serviceAccountChecks=$true;watcherRuntimeChecks=$false;watcherCoverage='required x64 canonical job';secondsAfterBuild=$engineClock.Elapsed.TotalSeconds;gateP1Passed=$false} |
        ConvertTo-Json | Set-Content (Join-Path $out 'parallel-summary.json')
}
catch {
    @{status='failed';runId=$verificationRunId;startedUtc=$startedUtc;parallel=$true;caseCapacityStress=(-not $SkipCaseCapacityStress);watcherRuntimeChecks=$false;errorType=$_.Exception.GetType().Name;hResult=$_.Exception.HResult} |
        ConvertTo-Json | Set-Content (Join-Path $out 'parallel-summary.json')
    throw
}
finally {
    try {
        if ($engineProcess) {
            try {
                if (-not $engineProcess.HasExited) {
                    $engineProcess.Kill($true)
                    if (-not $engineProcess.WaitForExit(10000)) { throw 'Owned Engine process did not stop after termination' }
                }
                $stdout = $engineOutput.GetAwaiter().GetResult()
                $stderr = $engineError.GetAwaiter().GetResult()
                $stdout | Set-Content (Join-Path $out 'engine.stdout.log')
                $stderr | Set-Content (Join-Path $out 'engine.stderr.log')
                Write-Host $stdout
                if ($stderr) { Write-Host $stderr }
            }
            finally { $engineProcess.Dispose() }
        }
    }
    finally { Pop-Location }
}
