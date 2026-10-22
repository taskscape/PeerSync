[CmdletBinding()]
param([ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64', [switch]$SkipRuntimeChecks, [switch]$SkipEngineTests, [switch]$SkipSimulation, [switch]$SkipFuzz, [int]$FuzzSeconds = 600)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
$fuzz = $null
try {
    New-Item -ItemType Directory -Force artifacts | Out-Null
    & "$PSScriptRoot/Write-Environment.ps1" -Path "artifacts/environment-$Runtime.json"
    $clock = [Diagnostics.Stopwatch]::StartNew()
    & dotnet restore PeerSync.slnx --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed' }
    $architecture = if ($Runtime -eq 'win-arm64') { 'arm64' } else { 'x64' }
    & dotnet build PeerSync.slnx -c Release -p:PlatformTarget=$architecture --no-restore -bl:"artifacts/build-$Runtime.binlog"
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    if (-not $SkipRuntimeChecks) {
        if (-not $SkipFuzz) {
            # Start the required ten-minute fuzz as soon as its immutable build output
            # exists. Tests (--no-build) and native probes only read their build output;
            # publishing Core/SpikeSupport does not build the fuzz's Crypto/Protocol
            # dependencies. No rebuild may run against the active fuzz output directory.
            $fuzzInfo = [Diagnostics.ProcessStartInfo]::new((Join-Path $root 'tests/PeerSync.Fuzz/bin/Release/net10.0-windows10.0.20348.0/PeerSync.Fuzz.exe'))
            $fuzzInfo.UseShellExecute=$false; $fuzzInfo.CreateNoWindow=$true
            $fuzzInfo.RedirectStandardOutput=$true; $fuzzInfo.RedirectStandardError=$true
            $fuzzInfo.WorkingDirectory=$root
            $fuzzInfo.ArgumentList.Add('--seconds'); $fuzzInfo.ArgumentList.Add("$FuzzSeconds")
            $fuzz = [Diagnostics.Process]::Start($fuzzInfo)
            $fuzzClock = [Diagnostics.Stopwatch]::StartNew()
            $fuzzOutput = $fuzz.StandardOutput.ReadToEndAsync()
            $fuzzError = $fuzz.StandardError.ReadToEndAsync()
        }
        & "$PSScriptRoot/Test-Diagnostics.ps1"
        & "$PSScriptRoot/Test-ControlPipeReadiness.ps1"
        # Only projects with real tests are counted; future-phase skeletons are excluded.
        foreach ($name in @('Core','Crypto','Protocol','Net','Engine','Storage','Control.Contracts','Service','App','Rendezvous')) {
            # Required Server 2022 and Server 2025 jobs each run the complete Engine
            # suite. Packaging CI can omit its third copy; local default stays full.
            if ($name -eq 'Engine' -and $SkipEngineTests) { continue }
            & dotnet test --project "tests/PeerSync.$name.Tests/PeerSync.$name.Tests.csproj" -c Release --no-build --report-trx --results-directory artifacts/tests
            if ($LASTEXITCODE -ne 0) { throw "Tests failed: $name" }
        }
        if (-not $SkipSimulation) { & "$PSScriptRoot/Run-Simulation.ps1" -Cases 8 -NoBuild }
        & "$PSScriptRoot/Run-P0.ps1"
        & "$PSScriptRoot/Test-QuicDiagnostics.ps1"
    }
    & "$PSScriptRoot/Publish.ps1" -Runtime $Runtime
    if ($fuzz) {
        $remaining = [Math]::Max(1000, ($FuzzSeconds + 60) * 1000 - [int]$fuzzClock.ElapsedMilliseconds)
        if (-not $fuzz.WaitForExit($remaining)) { throw 'P0 boundary fuzz timed out' }
        New-Item -ItemType Directory -Force artifacts/fuzz | Out-Null
        $fuzzOutput.GetAwaiter().GetResult() | Set-Content artifacts/fuzz/summary.log
        $fuzzError.GetAwaiter().GetResult() | Set-Content artifacts/fuzz/stderr.log
        Get-Content artifacts/fuzz/summary.log
        if ($fuzz.ExitCode -ne 0) { throw 'P0 boundary fuzz failed; see artifacts/fuzz' }
    }
    $clock.Stop()
    $fuzzDuration = 0
    if ($fuzz) { $fuzzDuration = $FuzzSeconds }
    @{status='passed';runtime=$Runtime;runtimeChecks=(-not $SkipRuntimeChecks);engineTestsRun=(-not $SkipRuntimeChecks -and -not $SkipEngineTests);simulationRun=(-not $SkipRuntimeChecks -and -not $SkipSimulation);fuzzSeconds=$fuzzDuration;seconds=$clock.Elapsed.TotalSeconds;phase='P1';gateP1Passed=$false} | ConvertTo-Json | Set-Content "artifacts/verification-$Runtime.json"
}
finally {
    if ($fuzz) {
        if (-not $fuzz.HasExited) { $fuzz.Kill($true); $fuzz.WaitForExit() }
        $fuzz.Dispose()
    }
    Pop-Location
}
