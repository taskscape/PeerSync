[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$output = [IO.Path]::GetFullPath($OutputDirectory)
if ([IO.Directory]::Exists($output) -or [IO.File]::Exists($output)) {
    throw 'Use a new output directory so the control cannot overwrite evidence.'
}
if (-not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($output))) {
    throw 'The output parent must already exist.'
}

$measure = Join-Path $PSScriptRoot 'Measure-PeerSyncTree.ps1'
$capture = Join-Path $PSScriptRoot 'Invoke-PeerSyncSoakCaptureLoop.ps1'
$compare = Join-Path $PSScriptRoot 'Compare-PeerSyncSoakReports.ps1'
$budget = Join-Path $PSScriptRoot 'Get-PeerSyncTestTreeContentBytes.ps1'
foreach ($script in @($measure, $capture, $compare, $budget)) {
    if (-not [IO.File]::Exists($script)) { throw "Missing field tool: $script" }
}

[IO.Directory]::CreateDirectory($output) | Out-Null
$runId = [Guid]::NewGuid()
$run = $runId.ToString('N')
$labels = @('pc-a', 'pc-b', 'pc-c')
$now = [DateTime]::UtcNow
$currentHour = $now.AddMinutes(-$now.Minute).AddSeconds(-$now.Second).
    AddMilliseconds(-$now.Millisecond)
$first = $currentHour.AddHours(-4)
$firstText = $first.ToString('yyyy-MM-ddTHH:mm:ssZ',
    [Globalization.CultureInfo]::InvariantCulture)
$collectorHash = (Get-FileHash -LiteralPath $measure -Algorithm SHA256).Hash

# One actual small capture checks the Windows PowerShell 5.1 field entry point.
$liveRoot = Join-Path $output 'live-root'
$liveReports = Join-Path $output 'live-reports'
[IO.Directory]::CreateDirectory($liveRoot) | Out-Null
[IO.Directory]::CreateDirectory($liveReports) | Out-Null
[IO.File]::WriteAllText((Join-Path $liveRoot 'control.txt'), 'three-hour-control')
$deepDirectory = $liveRoot
for ($depth = 1; $depth -le 24; $depth++) {
    $deepDirectory = Join-Path $deepDirectory ('level-{0:D3}' -f $depth)
}
$deepFile = Join-Path $deepDirectory 'depth-024.bin'
if ($deepFile.Length -le 260 -or $deepFile -notmatch '^[A-Za-z]:\\') {
    throw 'The live long-path fixture is not an absolute path beyond MAX_PATH.'
}
[IO.Directory]::CreateDirectory('\\?\' + $deepDirectory) | Out-Null
[IO.File]::WriteAllText(('\\?\' + $deepFile), 'deep-tree-control')
. $budget
$expectedLiveBytes = [Text.Encoding]::UTF8.GetByteCount('three-hour-control') +
    [Text.Encoding]::UTF8.GetByteCount('deep-tree-control')
$measuredLiveBytes = Get-PeerSyncTestTreeContentBytes -Root $liveRoot
if ($measuredLiveBytes -ne $expectedLiveBytes) {
    throw 'The budget preflight did not count the long-path file.'
}
$currentText = $currentHour.ToString('yyyy-MM-ddTHH:mm:ssZ',
    [Globalization.CultureInfo]::InvariantCulture)
& $capture -Root $liveRoot -RunId $runId -MachineLabel 'pc-a' `
    -FirstHourUtc $currentText -OutputDirectory $liveReports -Intervals 0 | Out-Null
$liveExit = $LASTEXITCODE
$liveSnapshot = Join-Path $liveReports "snapshot-$run-pc-a-$($currentHour.ToString('yyyyMMddTHH')).json"
if ($liveExit -ne 0 -or -not [IO.File]::Exists($liveSnapshot)) {
    throw 'The one-hour live capture control failed.'
}
$live = Get-Content -LiteralPath $liveSnapshot -Raw | ConvertFrom-Json
if ($live.status -ne 'complete' -or $live.files -ne 2 -or
    $live.directories -ne 24 -or $live.bytes -ne $expectedLiveBytes -or
    $live.collectorSha256 -ne $collectorHash) {
    throw 'The live snapshot did not include the long-path file.'
}

function New-SyntheticSet([string]$variant, [bool]$omitOne) {
    $base = Join-Path $output $variant
    $inputDirectories = @($labels | ForEach-Object { Join-Path $base $_ })
    $reports = Join-Path $base 'reports'
    foreach ($directory in @($inputDirectories) + @($reports)) {
        [IO.Directory]::CreateDirectory($directory) | Out-Null
    }
    for ($index = 0; $index -le 3; $index++) {
        $hour = $first.AddHours($index)
        $hourText = $hour.ToString('yyyy-MM-ddTHH:mm:ssZ',
            [Globalization.CultureInfo]::InvariantCulture)
        for ($machine = 0; $machine -lt $labels.Length; $machine++) {
            if ($omitOne -and $index -eq 2 -and $machine -eq 2) { continue }
            $name = "snapshot-$run-$($labels[$machine])-$($hour.ToString('yyyyMMddTHH')).json"
            $path = Join-Path $inputDirectories[$machine] $name
            # These reports are deliberately synthetic. They must never be
            # presented as physical machine, content or elapsed-time evidence.
            $snapshot = [ordered]@{
                schema = 'peersync-tree-snapshot-v1'; status = 'complete'
                runId = $run; machineLabel = $labels[$machine]
                hourUtc = $hourText
                startedUtc = $hour.AddMinutes(1).ToString('O')
                endedUtc = $hour.AddMinutes(1).AddSeconds(2).ToString('O')
                collectorSha256 = $collectorHash
                files = 100032; directories = 100; bytes = 9663676416
                treeSha256 = ('A' * 64)
            }
            $snapshot | ConvertTo-Json -Depth 4 |
                Set-Content -LiteralPath $path -Encoding UTF8
        }
    }
    & $compare -RunId $runId -FirstHourUtc $firstText `
        -MachineLabels $labels -InputDirectories $inputDirectories `
        -OutputDirectory $reports -Intervals 3 -MinimumMachines 3 `
        -MinimumHours 3 | Out-Null
    $code = $LASTEXITCODE
    $summaryPath = Join-Path $reports "soak-summary-$run.json"
    if (-not [IO.File]::Exists($summaryPath)) {
        throw "The $variant batch did not create its summary."
    }
    return [pscustomobject]@{
        exitCode = $code
        path = $summaryPath
        summary = (Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json)
    }
}

$pass = New-SyntheticSet 'synthetic-pass' $false
if ($pass.exitCode -ne 0 -or $pass.summary.matchedHours -ne 4 -or
    $pass.summary.auditPass -ne $true -or @($pass.summary.failedHours).Count -ne 0) {
    throw 'The four-hour, three-machine synthetic fixture did not pass.'
}
$missing = New-SyntheticSet 'synthetic-missing-hour' $true
if ($missing.exitCode -ne 1 -or $missing.summary.matchedHours -ne 3 -or
    $missing.summary.auditPass -ne $false -or
    @($missing.summary.failedHours).Count -ne 1) {
    throw 'The missing-peer-hour fixture was not rejected.'
}

$report = [ordered]@{
    schema = 'peersync-three-hour-soak-control-v1'
    checkedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    scope = 'one real local capture and synthetic four-hour, three-machine comparison; no physical soak'
    runId = $run; firstHourUtc = $firstText
    collectorSha256 = $collectorHash
    captureLoopSha256 = (Get-FileHash -LiteralPath $capture -Algorithm SHA256).Hash
    comparatorSha256 = (Get-FileHash -LiteralPath $compare -Algorithm SHA256).Hash
    liveSnapshot = $liveSnapshot; liveExitCode = $liveExit
    liveBytes = $measuredLiveBytes; liveLongPath = $deepFile.Length
    positiveSummary = $pass.path; positiveExitCode = $pass.exitCode
    positiveMatchedHours = $pass.summary.matchedHours
    negativeSummary = $missing.path; negativeExitCode = $missing.exitCode
    negativeMatchedHours = $missing.summary.matchedHours
    negativeAuditIssues = @($missing.summary.auditIssues)
    pass = $true
}
$reportPath = Join-Path $output 'control-result.json'
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $reportPath -Encoding UTF8
"PASS $reportPath"
