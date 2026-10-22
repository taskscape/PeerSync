[CmdletBinding()]
param(
    [switch]$SkipCaseCapacityStress,
    [ValidateSet(840,1440)][int]$VerificationBudgetSeconds = 840
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'artifacts/p0-services'
$engineProcess = $null
$engineClock = $null
$buildSeconds = $null
$remaining = $null
$verificationClock = [Diagnostics.Stopwatch]::StartNew()
# Each Server matrix entry reserves one minute beyond this shared build/probe/
# Engine budget for setup, artifact upload and owned-process cleanup.
$verificationBudgetMilliseconds = $VerificationBudgetSeconds * 1000
$verificationRunId = [Guid]::NewGuid().ToString('N')
$startedUtc = [DateTime]::UtcNow.ToString('O')
Push-Location $root
try {
    New-Item -ItemType Directory -Force $out | Out-Null
    @{status='running';runId=$verificationRunId;startedUtc=$startedUtc;parallel=$true;caseCapacityStress=(-not $SkipCaseCapacityStress);watcherRuntimeChecks=$false;verificationBudgetSeconds=($verificationBudgetMilliseconds/1000)} | ConvertTo-Json | Set-Content (Join-Path $out 'parallel-summary.json')
    & dotnet build build/PeerSync.NativeVerification.slnx -c Release -p:RestoreLockedMode=true
    $buildSeconds = $verificationClock.Elapsed.TotalSeconds
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    if ($verificationClock.ElapsedMilliseconds -ge $verificationBudgetMilliseconds) { throw 'Native verification budget exhausted during build' }
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
    $remaining = [int][Math]::Max(0, $verificationBudgetMilliseconds - $verificationClock.ElapsedMilliseconds)
    if ($remaining -eq 0 -or -not $engineProcess.WaitForExit($remaining)) { throw "Native verification exceeded its shared $VerificationBudgetSeconds-second budget" }
    if ($engineProcess.ExitCode -ne 0) { throw "Native Engine scope failed ($($engineProcess.ExitCode)); see engine stdout/stderr and TRX" }
    if ($verificationClock.ElapsedMilliseconds -ge $verificationBudgetMilliseconds) { throw 'Native verification completed outside its shared budget' }
    @{status='passed';runId=$verificationRunId;startedUtc=$startedUtc;parallel=$true;nativeEngineExitCode=$engineProcess.ExitCode;caseCapacityStress=(-not $SkipCaseCapacityStress);caseCapacityCoverage='required Server 2022 job and default local runner';serviceAccountChecks=$true;watcherRuntimeChecks=$false;watcherCoverage='required x64 canonical job';secondsAfterBuild=$engineClock.Elapsed.TotalSeconds;verificationBudgetSeconds=($verificationBudgetMilliseconds/1000);buildSeconds=$buildSeconds;remainingWaitMilliseconds=$remaining;totalSeconds=$verificationClock.Elapsed.TotalSeconds;gateP1Passed=$false} |
        ConvertTo-Json | Set-Content (Join-Path $out 'parallel-summary.json')
}
catch {
    @{status='failed';runId=$verificationRunId;startedUtc=$startedUtc;parallel=$true;caseCapacityStress=(-not $SkipCaseCapacityStress);watcherRuntimeChecks=$false;errorType=$_.Exception.GetType().Name;hResult=$_.Exception.HResult;verificationBudgetSeconds=($verificationBudgetMilliseconds/1000);buildSeconds=$buildSeconds;remainingWaitMilliseconds=$remaining;totalSeconds=$verificationClock.Elapsed.TotalSeconds} |
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
