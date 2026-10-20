[CmdletBinding()]
param([ValidateRange(1,10000)][int]$Cases = 1000, [ValidateRange(1,10000)][int]$HierarchicalCases = 4, [switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$previousArtifacts = $env:PEERSYNC_SIMULATION_ARTIFACTS
$previousCases = $env:PEERSYNC_SIMULATION_CASES
$previousDirect = $env:PEERSYNC_SIMULATION_CASE
$previousHierarchyCases = $env:PEERSYNC_SIMULATION_HIERARCHY_CASES
$previousHierarchyDirect = $env:PEERSYNC_SIMULATION_HIERARCHY_CASE
Push-Location $root
try {
    if (-not $NoBuild) {
        & dotnet restore PeerSync.slnx --locked-mode
        if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed' }
        & dotnet build tests/PeerSync.Simulation -c Release --no-restore
        if ($LASTEXITCODE -ne 0) { throw 'Simulation build failed' }
    }
    $runId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmss') + '-' + [Guid]::NewGuid().ToString('N')
    $env:PEERSYNC_SIMULATION_ARTIFACTS = Join-Path $root "artifacts/simulation/$runId"
    $env:PEERSYNC_SIMULATION_CASES = $Cases.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:PEERSYNC_SIMULATION_CASE = $null
    $env:PEERSYNC_SIMULATION_HIERARCHY_CASES = $HierarchicalCases.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:PEERSYNC_SIMULATION_HIERARCHY_CASE = $null
    & dotnet test --project tests/PeerSync.Simulation -c Release --no-build --report-trx --results-directory artifacts/tests/simulation
    if ($LASTEXITCODE -ne 0) { throw "Real-engine simulation failed; evidence: $env:PEERSYNC_SIMULATION_ARTIFACTS" }
    $summaryPath = Join-Path $env:PEERSYNC_SIMULATION_ARTIFACTS summary.json
    $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
    if ($summary.status -ne 'passed' -or $summary.completed -ne $Cases) { throw 'Simulation did not complete the requested generated cases' }
    Get-Content -LiteralPath $summaryPath
    $hierarchyPath = Join-Path $env:PEERSYNC_SIMULATION_ARTIFACTS hierarchical-summary.json
    $hierarchySummary = Get-Content -LiteralPath $hierarchyPath -Raw | ConvertFrom-Json
    if ($hierarchySummary.status -ne 'passed' -or $hierarchySummary.completed -ne $HierarchicalCases) { throw 'Hierarchical simulation did not complete the requested generated cases' }
    Get-Content -LiteralPath $hierarchyPath
    Write-Output "SIMULATION_EVIDENCE $env:PEERSYNC_SIMULATION_ARTIFACTS"
}
finally {
    $env:PEERSYNC_SIMULATION_ARTIFACTS = $previousArtifacts
    $env:PEERSYNC_SIMULATION_CASES = $previousCases
    $env:PEERSYNC_SIMULATION_CASE = $previousDirect
    $env:PEERSYNC_SIMULATION_HIERARCHY_CASES = $previousHierarchyCases
    $env:PEERSYNC_SIMULATION_HIERARCHY_CASE = $previousHierarchyDirect
    Pop-Location
}
