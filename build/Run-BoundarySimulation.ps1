[CmdletBinding()]
param(
    [ValidateRange(1,86400)][int]$FuzzSeconds = 600,
    [ValidateRange(1,10000)][int]$Cases = 8,
    [ValidateRange(1,10000)][int]$HierarchicalCases = 4,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
$fuzz = $null
$fuzzOutput = $null
$fuzzError = $null
$clock = [Diagnostics.Stopwatch]::StartNew()
try {
    New-Item -ItemType Directory -Force artifacts/fuzz | Out-Null
    & "$PSScriptRoot/Write-Environment.ps1" -Path artifacts/fuzz/environment.json
    if (-not $NoBuild) {
        foreach ($project in @('PeerSync.Fuzz','PeerSync.Simulation')) {
            & dotnet restore "tests/$project/$project.csproj" --locked-mode
            if ($LASTEXITCODE -ne 0) { throw "Restore failed: $project" }
            & dotnet build "tests/$project/$project.csproj" -c Release -p:PlatformTarget=x64 --no-restore
            if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
        }
    }
    # Both outputs are now immutable. Simulation reads its own dependency
    # copies; no publishing or build runs while either process uses them.
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $root 'tests/PeerSync.Fuzz/bin/Release/net10.0-windows10.0.20348.0/PeerSync.Fuzz.exe'))
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $start.WorkingDirectory=$root
    $start.ArgumentList.Add('--seconds'); $start.ArgumentList.Add("$FuzzSeconds")
    $fuzz = [Diagnostics.Process]::Start($start)
    $fuzzClock = [Diagnostics.Stopwatch]::StartNew()
    $fuzzOutput = $fuzz.StandardOutput.ReadToEndAsync()
    $fuzzError = $fuzz.StandardError.ReadToEndAsync()
    & "$PSScriptRoot/Run-Simulation.ps1" -Cases $Cases -HierarchicalCases $HierarchicalCases -NoBuild
    $remaining = [Math]::Max(1000, ($FuzzSeconds + 60) * 1000 - [int]$fuzzClock.ElapsedMilliseconds)
    if (-not $fuzz.WaitForExit($remaining)) { throw 'Boundary fuzz exceeded its duration/cleanup deadline' }
    $stdout = $fuzzOutput.GetAwaiter().GetResult()
    $stdout | Set-Content artifacts/fuzz/summary.log
    if ($fuzz.ExitCode -ne 0) { throw 'Boundary fuzz failed; inspect artifacts/fuzz' }
    $result = $stdout | ConvertFrom-Json
    if ($result.target -ne 'FastCDC,PathRules,VersionVector,IgnoreMatcher' -or $result.seconds -lt $FuzzSeconds -or $result.crashes -ne 0 -or $result.seeds -le 0) {
        throw 'Boundary fuzz result does not prove the requested duration and targets'
    }
    @{status='passed';requestedSeconds=$FuzzSeconds;seconds=$fuzzClock.Elapsed.TotalSeconds;target=$result.target;seeds=$result.seeds;gateP0Passed=$false} |
        ConvertTo-Json | Set-Content artifacts/fuzz/verification.json
    @{status='passed';fuzzSeconds=$FuzzSeconds;simulationCases=$Cases;hierarchicalCases=$HierarchicalCases;retainedRegressionCases=3;parallel=$true;seconds=$clock.Elapsed.TotalSeconds;gateP1Passed=$false} |
        ConvertTo-Json | Set-Content artifacts/fuzz/boundary-simulation.json
}
catch {
    @{status='failed';fuzzSeconds=$FuzzSeconds;simulationCases=$Cases;hierarchicalCases=$HierarchicalCases;parallel=$true;errorType=$_.Exception.GetType().Name;hResult=$_.Exception.HResult} |
        ConvertTo-Json | Set-Content artifacts/fuzz/boundary-simulation.json
    throw
}
finally {
    if ($fuzz) {
        try {
            if (-not $fuzz.HasExited) { $fuzz.Kill($true); if (-not $fuzz.WaitForExit(10000)) { throw 'Owned fuzz process did not stop' } }
            if ($fuzzOutput) { $fuzzOutput.GetAwaiter().GetResult() | Set-Content artifacts/fuzz/summary.log }
            if ($fuzzError) { $fuzzError.GetAwaiter().GetResult() | Set-Content artifacts/fuzz/stderr.log }
        }
        finally { $fuzz.Dispose() }
    }
    Pop-Location
}
