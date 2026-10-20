[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidateSet('preflight','campaign')][string]$Mode,
    [Parameter(Mandatory=$true)][ValidateRange(0,4)][int]$Shard,
    [Parameter(Mandatory=$true)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$SourceRevision,
    [Parameter(Mandatory=$true)][ValidatePattern('^[0-9]+$')][string]$RunId,
    [ValidateRange(1,1000)][int]$RunAttempt = 1,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    if ($Mode -eq 'campaign' -and $RunAttempt -ne 1) { throw 'Campaign shards require the first workflow attempt' }
    $source = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $source -ne $SourceRevision.ToLowerInvariant()) { throw 'Shard checkout differs from the pinned source' }
    $dirty = @(& git status --porcelain).Count -ne 0
    if ($Mode -eq 'campaign' -and ($NoBuild -or $dirty)) { throw 'Campaign requires a clean checkout and an actual build' }
    if (-not $NoBuild) {
        & dotnet restore tests/PeerSync.Simulation/PeerSync.Simulation.csproj --locked-mode
        if ($LASTEXITCODE -ne 0) { throw 'Campaign locked restore failed' }
        & dotnet build tests/PeerSync.Simulation/PeerSync.Simulation.csproj -c Release -p:PlatformTarget=x64 --no-restore
        if ($LASTEXITCODE -ne 0) { throw 'Campaign build failed' }
    }
    $cases = if ($Mode -eq 'campaign') { 200 } else { 2 }
    $withRetained = $Mode -eq 'campaign' -and $Shard -eq 0
    $started = [DateTime]::UtcNow
    $before = @()
    if (Test-Path artifacts/simulation) { $before = @(Get-ChildItem artifacts/simulation -Directory | ForEach-Object FullName) }
    $testExit = 1
    try {
        Write-Host "CAMPAIGN_SHARD mode=$Mode shard=$Shard flat=$cases hierarchy=1 retained=$withRetained source=$source"
        & "$PSScriptRoot/Run-Simulation.ps1" -Cases $cases -HierarchicalCases 1 -NoBuild -SkipRetainedRegressions:(-not $withRetained)
        $testExit = 0
    }
    finally {
        $created = @(Get-ChildItem artifacts/simulation -Directory | Where-Object FullName -NotIn $before)
        if ($created.Count -ne 1) { throw 'Cannot uniquely identify this shard evidence directory' }
        $evidence = $created[0].FullName
        $trx = @(Get-ChildItem artifacts/tests/simulation -Filter '*.trx' -File | Where-Object LastWriteTimeUtc -GE $started)
        if ($trx.Count -eq 1) { Copy-Item -LiteralPath $trx[0].FullName -Destination (Join-Path $evidence 'results.trx') }
        $binary = Join-Path $root 'tests/PeerSync.Simulation/bin/Release/net10.0-windows10.0.20348.0/PeerSync.Simulation.dll'
        @{schema='peersync-p1-campaign-shard-v1';status=$(if($testExit -eq 0){'passed'}else{'failed'});testExitCode=$testExit;
          sourceRevision=$source;workingTreeDirty=$dirty;mode=$Mode;runId=$RunId;runAttempt=$RunAttempt;shard=$Shard;
          startedUtc=$started.ToString('O');completedUtc=[DateTime]::UtcNow.ToString('O');flatCases=$cases;hierarchicalCases=1;
          retainedRegressions=$withRetained;trx='results.trx';binarySha256=(Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash;
          gateP1Passed=$false} | ConvertTo-Json | Set-Content (Join-Path $evidence 'campaign-shard.json')
        Write-Output "CAMPAIGN_EVIDENCE $evidence"
    }
}
finally { Pop-Location }
