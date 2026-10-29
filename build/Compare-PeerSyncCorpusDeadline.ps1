[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateCount(3,3)][string[]]$Snapshots,
    [Parameter(Mandatory)][ValidateCount(3,3)][string[]]$RunReports,
    [Parameter(Mandatory)][string]$GeneratorReport,
    [Parameter(Mandatory)][string]$SourceResumeUtc,
    [Parameter(Mandatory)][string]$OutputFile,
    [ValidateRange(1,15)][int]$MaxStartSkewMinutes = 5
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$output = [IO.Path]::GetFullPath($OutputFile)
if ([IO.File]::Exists($output) -or [IO.Directory]::Exists($output)) {
    throw 'Corpus comparison output already exists.'
}
if (-not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($output))) {
    throw 'Corpus comparison output directory is missing.'
}

function Utc([object]$value) {
    return [DateTimeOffset]::Parse([string]$value,
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::AssumeUniversal).UtcDateTime
}

$resume = Utc $SourceResumeUtc
$deadline = $resume.AddHours(3)
$generator = Get-Content -LiteralPath $GeneratorReport -Raw | ConvertFrom-Json
if ($generator.schema -ne 'peersync-gate-corpus-v1' -or
    $generator.status -ne 'complete' -or
    $generator.runId -notmatch '^[0-9a-fA-F]{32}$' -or
    $generator.smallFileCount -ne 50000 -or
    $generator.deepLevels -ne 24 -or
    $generator.largeFileCount -ne 8 -or
    $generator.createdFiles -ne 50032 -or
    $generator.createdContentBytes -ne 9GB -or
    $generator.generatorSha256 -notmatch '^[0-9a-fA-F]{64}$' -or
    (Utc $generator.endedUtc) -gt $resume) {
    throw 'Generator report does not prove the required completed 9 GiB corpus before source resume.'
}

$issues = [Collections.Generic.List[string]]::new()
$runs = [Collections.Generic.List[object]]::new()
foreach ($path in $RunReports) {
    try {
        $item = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        if ($item.schema -ne 'peersync-corpus-run-measure-v1' -or
            $item.status -ne 'complete' -or
            $item.runId -ne $generator.runId -or
            $item.machineLabel -notin @('first','second','third') -or
            $item.files -ne ($generator.createdFiles + 1) -or
            $item.markerBytes -le 0 -or
            $item.bytes -ne ($generator.createdContentBytes + $item.markerBytes) -or
            $item.directories -lt $generator.deepLevels -or
            $item.markerSha256 -notmatch '^[0-9a-fA-F]{64}$' -or
            $item.ownerGeneratorSha256 -ne $generator.generatorSha256 -or
            $item.collectorSha256 -notmatch '^[0-9a-fA-F]{64}$') {
            throw 'Corpus-run report is incomplete or has the wrong shape.'
        }
        $runStart = Utc $item.startedUtc
        $runEnd = Utc $item.endedUtc
        if ($runStart -lt $resume -or $runEnd -lt $runStart -or $runEnd -gt $deadline) {
            $issues.Add('run-measure-outside-three-hour-window')
        }
        $runs.Add([pscustomobject]@{
            label=$item.machineLabel;files=[long]$item.files
            directories=[long]$item.directories;bytes=[long]$item.bytes
            markerBytes=[long]$item.markerBytes
            markerSha256=$item.markerSha256
            ownerGeneratorSha256=$item.ownerGeneratorSha256
            collectorSha256=$item.collectorSha256
            startedUtc=$runStart.ToString('o');endedUtc=$runEnd.ToString('o')
        })
    } catch {
        $issues.Add('run-measure-invalid')
    }
}
if ($runs.Count -ne 3 -or
    @($runs | Select-Object -ExpandProperty label | Sort-Object -Unique).Count -ne 3) {
    $issues.Add('three-distinct-run-measures-required')
} else {
    $sourceRun = @($runs | Where-Object label -eq 'first')[0]
    foreach ($item in $runs) {
        if ($item.files -ne $sourceRun.files -or
            $item.directories -ne $sourceRun.directories -or
            $item.bytes -ne $sourceRun.bytes -or
            $item.markerSha256 -ne $sourceRun.markerSha256 -or
            $item.collectorSha256 -ne $sourceRun.collectorSha256) {
            $issues.Add('run-measure-mismatch')
            break
        }
    }
}
$loaded = [Collections.Generic.List[object]]::new()
foreach ($path in $Snapshots) {
    try {
        $item = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        if ($item.schema -ne 'peersync-tree-snapshot-v1' -or
            $item.status -ne 'complete' -or
            $item.runId -ne $generator.runId -or
            $item.machineLabel -notin @('first','second','third') -or
            $item.treeSha256 -notmatch '^[0-9a-fA-F]{64}$' -or
            $item.collectorSha256 -notmatch '^[0-9a-fA-F]{64}$' -or
            $item.files -lt $generator.createdFiles -or
            $item.bytes -lt $generator.createdContentBytes -or
            $item.bytes -gt 10GB -or
            $item.directories -lt 0) {
            throw 'Snapshot shape or corpus minimum is invalid.'
        }
        $start = Utc $item.startedUtc
        $end = Utc $item.endedUtc
        if ($start -lt $resume -or $end -lt $start -or $end -gt $deadline) {
            $issues.Add('capture-outside-three-hour-window')
        }
        $loaded.Add([pscustomobject]@{
            label=$item.machineLabel;runId=$item.runId
            files=[long]$item.files;directories=[long]$item.directories
            bytes=[long]$item.bytes;treeSha256=$item.treeSha256
            collectorSha256=$item.collectorSha256
            startedUtc=$start.ToString('o');endedUtc=$end.ToString('o')
        })
    } catch {
        $issues.Add('snapshot-invalid')
    }
}
if ($loaded.Count -ne 3 -or
    @($loaded | Select-Object -ExpandProperty label | Sort-Object -Unique).Count -ne 3) {
    $issues.Add('three-distinct-machines-required')
}
if ($loaded.Count -eq 3) {
    $first = @($loaded | Where-Object label -eq 'first')
    if ($first.Count -ne 1) {
        $issues.Add('source-snapshot-required')
    } else {
        foreach ($item in $loaded) {
            if ($item.collectorSha256 -ne $first[0].collectorSha256) {
                $issues.Add('collector-version-mismatch')
                break
            }
        }
        foreach ($item in $loaded) {
            if ($item.files -ne $first[0].files -or
                $item.directories -ne $first[0].directories -or
                $item.bytes -ne $first[0].bytes -or
                $item.treeSha256 -ne $first[0].treeSha256) {
                $issues.Add('tree-mismatch')
                break
            }
        }
    }
    $starts = @($loaded | ForEach-Object { Utc $_.startedUtc } | Sort-Object)
    if (($starts[-1] - $starts[0]).TotalMinutes -gt $MaxStartSkewMinutes) {
        $issues.Add('capture-start-skew')
    }
    if ($runs.Count -eq 3) {
        foreach ($snapshot in $loaded) {
            $runMeasure = @($runs | Where-Object label -eq $snapshot.label)[0]
            if (-not $runMeasure -or (Utc $runMeasure.endedUtc) -gt (Utc $snapshot.startedUtc)) {
                $issues.Add('run-measure-after-capture')
                break
            }
        }
    }
}

$result = [ordered]@{
    schema='peersync-corpus-deadline-compare-v1'
    checkedUtc=[DateTimeOffset]::UtcNow.ToString('o')
    runId=$generator.runId
    sourceResumeUtc=$resume.ToString('o')
    deadlineUtc=$deadline.ToString('o')
    requiredCorpusFiles=$generator.createdFiles
    requiredCorpusBytes=$generator.createdContentBytes
    generatorSha256=$generator.generatorSha256
    runMeasures=@($runs)
    maxStartSkewMinutes=$MaxStartSkewMinutes
    snapshots=@($loaded)
    issues=@($issues | Sort-Object -Unique)
    match=$issues.Count -eq 0
}
$temporary = $output + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
try {
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $temporary -Encoding UTF8
    [IO.File]::Move($temporary,$output)
} finally {
    if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
}
Write-Output $output
if ($issues.Count -ne 0) { exit 1 }
