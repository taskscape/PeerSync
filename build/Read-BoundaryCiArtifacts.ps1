[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Path,
    [Parameter(Mandatory=$true)][string]$RunId,
    [Parameter(Mandatory=$true)][string]$RunAttempt,
    [Parameter(Mandatory=$true)][string]$SourceRevision
)
$ErrorActionPreference='Stop'
$cohorts=@('baseline','retained-1','retained-2','retained-3')
$registry=Get-Content -LiteralPath (Join-Path $PSScriptRoot simulation-scope.json) -Raw | ConvertFrom-Json
if ($registry.schema -ne 'peersync-simulation-scope-v1' -or @($registry.retainedSeeds).Count -ne 8) { throw 'Invalid Simulation scope registry' }
function Read-Json([string]$File) {
    $item=Get-Item -LiteralPath $File
    if ($item.Length -gt 1MB) { throw "Oversized boundary evidence: $($item.Name)" }
    Get-Content -LiteralPath $item.FullName -Raw | ConvertFrom-Json
}
$directories=@(Get-ChildItem -LiteralPath $Path -Directory)
$expectedDirectories=@($cohorts | ForEach-Object { "boundary-$_-attempt-$RunAttempt" })
if ($directories.Count -ne 4 -or @(Compare-Object @($expectedDirectories | Sort-Object) @($directories.Name | Sort-Object)).Count -ne 0) {
    throw 'Boundary artifacts are missing, extra or from another workflow attempt'
}
$manifests=@()
$reports=@()
$hash=$null
foreach ($cohort in $cohorts) {
    $folder=Join-Path $Path "boundary-$cohort-attempt-$RunAttempt"
    $allowedFiles=if ($cohort -eq 'baseline') {
        @('manifest.json','scope.trx','summary.json','hierarchical-summary.json','cases.jsonl',
          'hierarchical-cases.jsonl','connected-maintenance.json','fuzz-verification.json')
    } else { @('manifest.json','scope.trx') }
    if (@(Compare-Object @($allowedFiles | Sort-Object) @((Get-ChildItem -LiteralPath $folder -File).Name | Sort-Object)).Count -ne 0 -or
        @((Get-ChildItem -LiteralPath $folder -Directory)).Count -ne $(if($cohort -eq 'baseline'){0}else{1})) {
        throw "Missing or extra boundary package entries: $cohort"
    }
    $manifest=Read-Json (Join-Path $folder manifest.json)
    if ($manifest.schema -ne 'peersync-boundary-shard-v1' -or $manifest.status -ne 'passed' -or
        $manifest.cohort -ne $cohort -or $manifest.ciRunId -ne $RunId -or
        $manifest.ciRunAttempt -ne $RunAttempt -or $manifest.sourceRevision -ne $SourceRevision -or
        $manifest.binaryOutputsUnchanged -ne $true -or $manifest.simulationCases -ne 8 -or
        $manifest.hierarchicalCases -ne 4 -or $manifest.simulationSha256 -notmatch '^[0-9A-F]{64}$') {
        throw "Invalid boundary manifest: $cohort"
    }
    if ($null -eq $hash) { $hash=$manifest.simulationSha256 }
    elseif ($hash -ne $manifest.simulationSha256) { throw 'Simulation binaries differ across boundary shards' }
    $trx=Join-Path $folder scope.trx
    $scope=& "$PSScriptRoot/Read-SimulationScope.ps1" -Path $trx -Cohort $cohort
    if ($scope.executedMethods -ne $manifest.executedMethods -or
        $scope.retainedRegressionCases -ne $manifest.retainedRegressionCases -or
        $scope.maintenanceCases -ne $manifest.maintenanceCases) { throw "Boundary manifest disagrees with TRX: $cohort" }
    $reports+=$trx
    $manifests+=$manifest
    if ($cohort -eq 'baseline') {
        if (@($manifest.retainedSeeds).Count -ne 0 -or $scope.executedMethods -ne 7 -or $scope.maintenanceCases -ne 1) { throw 'Incomplete baseline scope' }
        foreach ($entry in @(@('summary.json',8),@('hierarchical-summary.json',4))) {
            $summary=Read-Json (Join-Path $folder $entry[0])
            if ($summary.status -ne 'passed' -or $summary.completed -ne $entry[1]) { throw "Incomplete generated evidence: $($entry[0])" }
        }
        foreach ($entry in @(@('cases.jsonl',8),@('hierarchical-cases.jsonl',4))) {
            $file=Get-Item -LiteralPath (Join-Path $folder $entry[0])
            if ($file.Length -gt 1MB) { throw "Oversized generated case list: $($entry[0])" }
            $cases=@(Get-Content -LiteralPath $file.FullName | ForEach-Object { $_ | ConvertFrom-Json })
            if ($cases.Count -ne $entry[1] -or @($cases.Seed | Sort-Object -Unique).Count -ne $entry[1] -or
                @($cases | Where-Object { $_.Rounds -le 0 -or $_.Rounds -gt 32 }).Count -ne 0) { throw "Incomplete generated case list: $($entry[0])" }
        }
        $maintenance=Read-Json (Join-Path $folder connected-maintenance.json)
        if ($maintenance.status -ne 'passed' -or -not $maintenance.sourceInformationalVersion.EndsWith("+$SourceRevision") -or
            $maintenance.retiredArchives -ne 2 -or $maintenance.deletedBlocks -ne 2 -or $maintenance.publishedVersions -ne 2) {
            throw 'Incomplete connected maintenance evidence'
        }
        $fuzz=Read-Json (Join-Path $folder fuzz-verification.json)
        if ($fuzz.status -ne 'passed' -or $fuzz.requestedSeconds -ne 600 -or $fuzz.seconds -lt 600 -or
            $fuzz.seconds -ne $manifest.fuzzSeconds -or $fuzz.seeds -ne $manifest.fuzzSeeds -or
            $manifest.fuzzCrashes -ne 0 -or $fuzz.target -ne 'FastCDC,PathRules,VersionVector,IgnoreMatcher,FrameCodec') {
            throw 'Incomplete ten-minute five-target fuzz evidence'
        }
    }
    else {
        $index=[int]$cohort.Substring('retained-'.Length)-1
        $expectedSeeds=@(for ($i=$index; $i -lt 8; $i+=3) { [int]$registry.retainedSeeds[$i] })
        if ($scope.executedMethods -ne $expectedSeeds.Count -or $scope.maintenanceCases -ne 0 -or
            @(Compare-Object @($expectedSeeds | Sort-Object) @($manifest.retainedSeeds | Sort-Object)).Count -ne 0) {
            throw "Wrong retained seed ownership: $cohort"
        }
        $files=@(Get-ChildItem -LiteralPath (Join-Path $folder regressions) -Filter '*.json' -File)
        if ($files.Count -ne $expectedSeeds.Count -or
            @(Compare-Object @($expectedSeeds | ForEach-Object { "$_.json" } | Sort-Object) @($files.Name | Sort-Object)).Count -ne 0) {
            throw "Missing or extra retained evidence: $cohort"
        }
        foreach ($seed in $expectedSeeds) {
            $result=Read-Json (Join-Path $folder "regressions/$seed.json")
            if ($result.Seed -ne $seed -or $result.Rounds -le 0 -or $result.Rounds -gt 32 -or
                $result.Nodes -lt 3 -or $result.Nodes -gt 7 -or $result.Versions -le 0) { throw "Invalid retained evidence: $seed" }
        }
    }
}
$combined=& "$PSScriptRoot/Read-SimulationScope.ps1" -Path $reports
if ($combined.executedMethods -ne 15 -or $combined.retainedRegressionCases -ne 8 -or $combined.maintenanceCases -ne 1) {
    throw 'Incomplete aggregate boundary Simulation scope'
}
[pscustomobject]@{status='passed';sourceRevision=$SourceRevision;ciRunId=$RunId;ciRunAttempt=$RunAttempt;
    simulationSha256=$hash;executedMethods=$combined.executedMethods;
    retainedRegressionCases=$combined.retainedRegressionCases;maintenanceCases=$combined.maintenanceCases;
    fuzzSeconds=$manifests[0].fuzzSeconds;fuzzSeeds=$manifests[0].fuzzSeeds;cohorts=$cohorts}
