[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$fixture=Join-Path $root ('artifacts/boundary-ci-controls/'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$registry=Get-Content -LiteralPath (Join-Path $PSScriptRoot simulation-scope.json) -Raw | ConvertFrom-Json
$source='0123456789abcdef0123456789abcdef01234567'
$hash='A'*64
$run='123456'
$attempt='2'
$passed=0
function Write-Json([string]$File,[object]$Value) { $Value | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $File }
function Assert-Rejected([string]$Name,[scriptblock]$Action) {
    $rejected=$false
    try { & $Action | Out-Null } catch { $rejected=$true }
    if (-not $rejected) { throw "Invalid boundary artifact accepted: $Name" }
    $script:passed++
}
function Read-Aggregate { & "$PSScriptRoot/Read-BoundaryCiArtifacts.ps1" -Path $fixture -RunId $run -RunAttempt $attempt -SourceRevision $source }
$cohorts=@('baseline','retained-1','retained-2','retained-3')
foreach ($cohort in $cohorts) {
    $folder=Join-Path $fixture "boundary-$cohort-attempt-$attempt"
    New-Item -ItemType Directory -Path $folder | Out-Null
    $seeds=@()
    $methods=@()
    if ($cohort -eq 'baseline') {
        $methods=@($registry.baselineMethods | ForEach-Object { $registry.prefix+$_ })
        Write-Json (Join-Path $folder summary.json) @{status='passed';completed=8}
        Write-Json (Join-Path $folder hierarchical-summary.json) @{status='passed';completed=4}
        1..8 | ForEach-Object { @{Seed=$_;Rounds=1} | ConvertTo-Json -Compress } | Set-Content (Join-Path $folder cases.jsonl)
        11..14 | ForEach-Object { @{Seed=$_;Rounds=1} | ConvertTo-Json -Compress } | Set-Content (Join-Path $folder hierarchical-cases.jsonl)
        Write-Json (Join-Path $folder connected-maintenance.json) @{status='passed';sourceInformationalVersion="1.0+$source";retiredArchives=2;deletedBlocks=2;publishedVersions=2}
        Write-Json (Join-Path $folder fuzz-verification.json) @{status='passed';requestedSeconds=600;seconds=600.1;seeds=100;target='FastCDC,PathRules,VersionVector,IgnoreMatcher,FrameCodec,MessageValidator,KeyText'}
    }
    else {
        $index=[int]$cohort.Substring('retained-'.Length)-1
        $seeds=@(for ($i=$index; $i -lt 10; $i+=3) { [int]$registry.retainedSeeds[$i] })
        $methods=@($seeds | ForEach-Object { $registry.prefix+"RetainedHierarchicalRegressionsConvergeAndPreservePublishedPayloads(seed: $_)" })
        New-Item -ItemType Directory -Path (Join-Path $folder regressions) | Out-Null
        foreach ($seed in $seeds) { Write-Json (Join-Path $folder "regressions/$seed.json") @{Seed=$seed;Rounds=$(if ($seed -eq 1390180802) { 28 } else { 1 });Nodes=3;Versions=1} }
    }
    $results=@($methods | ForEach-Object { '<UnitTestResult testName="'+[Security.SecurityElement]::Escape($_)+'" outcome="Passed" />' }) -join ''
    $count=$methods.Count
    $trx='<TestRun><Results>'+$results+'</Results><ResultSummary><Counters total="'+$count+'" executed="'+$count+'" passed="'+$count+'" failed="0" notExecuted="0" /></ResultSummary></TestRun>'
    Set-Content -LiteralPath (Join-Path $folder scope.trx) -Value $trx
    Write-Json (Join-Path $folder manifest.json) @{schema='peersync-boundary-shard-v1';status='passed';sourceRevision=$source;ciRunId=$run;ciRunAttempt=$attempt;cohort=$cohort;simulationSha256=$hash;executedMethods=$count;retainedRegressionCases=$seeds.Count;maintenanceCases=$(if($cohort -eq 'baseline'){1}else{0});retainedSeeds=@($seeds);simulationCases=8;hierarchicalCases=4;fuzzSeconds=$(if($cohort -eq 'baseline'){600.1}else{0});fuzzSeeds=$(if($cohort -eq 'baseline'){100}else{0});fuzzCrashes=0;binaryOutputsUnchanged=$true}
}
$aggregate=Read-Aggregate
if ($aggregate.status -ne 'passed' -or $aggregate.executedMethods -ne 17 -or $aggregate.retainedRegressionCases -ne 10) { throw 'Complete boundary artifacts rejected' }
$passed++
Assert-Rejected wrong-attempt { & "$PSScriptRoot/Read-BoundaryCiArtifacts.ps1" -Path $fixture -RunId $run -RunAttempt 3 -SourceRevision $source }
Assert-Rejected wrong-source { & "$PSScriptRoot/Read-BoundaryCiArtifacts.ps1" -Path $fixture -RunId $run -RunAttempt $attempt -SourceRevision ('f'*40) }
$retainedFolder=Join-Path $fixture "boundary-retained-1-attempt-$attempt"
$manifestPath=Join-Path $retainedFolder manifest.json
$original=Get-Content -LiteralPath $manifestPath -Raw
try {
    $manifest=$original | ConvertFrom-Json; $manifest.simulationSha256='B'*64; Write-Json $manifestPath $manifest
    Assert-Rejected mismatched-binary { Read-Aggregate }
    $manifest=$original | ConvertFrom-Json; $manifest.retainedSeeds=@($manifest.retainedSeeds | Select-Object -Skip 1); Write-Json $manifestPath $manifest
    Assert-Rejected missing-seed-ownership { Read-Aggregate }
    $manifest=$original | ConvertFrom-Json; $manifest.ciRunId='other'; Write-Json $manifestPath $manifest
    Assert-Rejected mixed-run { Read-Aggregate }
}
finally { Set-Content -LiteralPath $manifestPath -Value $original }
$seed=[int]$registry.retainedSeeds[0]
$seedPath=Join-Path $retainedFolder "regressions/$seed.json"
$seedOriginal=Get-Content -LiteralPath $seedPath -Raw
try { Remove-Item -LiteralPath $seedPath; Assert-Rejected missing-result { Read-Aggregate } }
finally { Set-Content -LiteralPath $seedPath -Value $seedOriginal }
try { $result=$seedOriginal | ConvertFrom-Json; $result.Versions=0; Write-Json $seedPath $result; Assert-Rejected empty-version-result { Read-Aggregate } }
finally { Set-Content -LiteralPath $seedPath -Value $seedOriginal }
try { $result=$seedOriginal | ConvertFrom-Json; $result.Rounds=33; Write-Json $seedPath $result; Assert-Rejected over-bound-result { Read-Aggregate } }
finally { Set-Content -LiteralPath $seedPath -Value $seedOriginal }
$extraPath=Join-Path $retainedFolder extra.trx
try { Set-Content -LiteralPath $extraPath -Value '<TestRun />'; Assert-Rejected extra-report { Read-Aggregate } }
finally { Remove-Item -LiteralPath $extraPath }
$trxPath=Join-Path $retainedFolder scope.trx
$trxOriginal=Get-Content -LiteralPath $trxPath -Raw
try { Set-Content -LiteralPath $trxPath -Value ($trxOriginal.Replace('passed="4"','passed="3"')); Assert-Rejected incomplete-trx { Read-Aggregate } }
finally { Set-Content -LiteralPath $trxPath -Value $trxOriginal }
$fuzzPath=Join-Path $fixture "boundary-baseline-attempt-$attempt/fuzz-verification.json"
$fuzzOriginal=Get-Content -LiteralPath $fuzzPath -Raw
try { $fuzz=$fuzzOriginal | ConvertFrom-Json; $fuzz.seconds=599; Write-Json $fuzzPath $fuzz; Assert-Rejected short-fuzz { Read-Aggregate } }
finally { Set-Content -LiteralPath $fuzzPath -Value $fuzzOriginal }
@{status='passed';controls=$passed;fixture=$fixture} | ConvertTo-Json
