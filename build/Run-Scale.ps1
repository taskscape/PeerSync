[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidateRange(32,100000)][int]$Files,
    [ValidateRange(1,86400)][int]$Seconds = 7200,
    [switch]$OracleOnly,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    if (-not $NoBuild) {
        & dotnet build tests/PeerSync.Benchmarks -c Release -p:RestoreLockedMode=true
        if ($LASTEXITCODE -ne 0) { throw 'Scale runner build failed' }
    }
    $revision = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read source revision' }
    $sourceDirty = @(& git status --porcelain).Count -ne 0
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read working tree state' }
    $runId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmss') + '-' + [Guid]::NewGuid().ToString('N')
    $evidence = Join-Path $root "artifacts/scale/$runId"
    if ($OracleOnly) { & dotnet run --project tests/PeerSync.Benchmarks -c Release --no-build -- --scale-oracle $evidence $revision }
    else { & dotnet run --project tests/PeerSync.Benchmarks -c Release --no-build -- --scale $Files $Seconds $evidence $revision }
    $runnerExit = $LASTEXITCODE
    if (Test-Path -LiteralPath $evidence) {
        @{sourceRevision=$revision;workingTreeDirty=$sourceDirty;runnerExitCode=$runnerExit} | ConvertTo-Json | Set-Content (Join-Path $evidence 'source-state.json')
    }
    if ($runnerExit -ne 0) { throw "Scale validation failed; generated fixture and evidence retained: $evidence" }
    $summary = Get-Content -LiteralPath (Join-Path $evidence 'summary.json') -Raw | ConvertFrom-Json
    if ($summary.status -ne 'passed') { throw 'Scale runner did not report successful completion' }
    if (-not $OracleOnly -and ($summary.generatedFiles -ne $Files -or $summary.initialNativeFiles -ne $Files -or $summary.nodes -ne 5)) { throw 'Scale runner corpus/node counts differ from the request' }
    Write-Output "SCALE_EVIDENCE $evidence"
}
finally { Pop-Location }
