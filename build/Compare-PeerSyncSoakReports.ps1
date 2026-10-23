[CmdletBinding()]
param(
    [Parameter(Mandatory)][guid]$RunId,
    [Parameter(Mandatory)]
    [ValidatePattern('^\d{4}-\d{2}-\d{2}T\d{2}:00:00Z$')][string]$FirstHourUtc,
    [Parameter(Mandatory)][ValidateCount(2, 16)][string[]]$MachineLabels,
    [Parameter(Mandatory)][ValidateCount(2, 16)][string[]]$InputDirectories,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateRange(0, 168)][int]$Intervals = 73,
    [ValidateRange(2, 16)][int]$MinimumMachines = 3,
    [ValidateRange(1, 168)][int]$MinimumHours = 72
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($MachineLabels.Length -ne $InputDirectories.Length -or
    $MachineLabels.Length -lt $MinimumMachines -or
    @($MachineLabels | Sort-Object -Unique).Count -ne $MachineLabels.Length) {
    throw 'Provide one distinct machine label and snapshot directory per required peer.'
}
foreach ($label in $MachineLabels) {
    if ($label -cnotmatch '^[A-Za-z0-9_-]{1,32}$') { throw 'Invalid machine label.' }
}
foreach ($directory in $InputDirectories) {
    if (-not [IO.Directory]::Exists([IO.Path]::GetFullPath($directory))) {
        throw 'A snapshot input directory is missing.'
    }
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (-not [IO.Directory]::Exists($output)) { throw 'OutputDirectory must already exist.' }
$first = [DateTime]::ParseExact($FirstHourUtc, 'yyyy-MM-ddTHH:mm:ssZ',
    [Globalization.CultureInfo]::InvariantCulture,
    [Globalization.DateTimeStyles]::AssumeUniversal).ToUniversalTime()
$run = $RunId.ToString('N')
$measure = [IO.Path]::Combine($PSScriptRoot, 'Measure-PeerSyncTree.ps1')
if (-not [IO.File]::Exists($measure)) { throw 'The packaged tree tool is missing.' }
$measureHash = (Get-FileHash -LiteralPath $measure -Algorithm SHA256).Hash
$comparePaths = [System.Collections.Generic.List[string]]::new()
for ($index = 0; $index -le $Intervals; $index++) {
    $name = "compare-$run-$($first.AddHours($index).ToString('yyyyMMddTHH')).json"
    $path = [IO.Path]::Combine($output, $name)
    if ([IO.File]::Exists($path) -or [IO.Directory]::Exists($path)) {
        throw 'A comparison output already exists; use a new output directory.'
    }
    $comparePaths.Add($path)
}
$auditPath = [IO.Path]::Combine($output, "soak-audit-$run.json")
$summaryPath = [IO.Path]::Combine($output, "soak-summary-$run.json")
foreach ($path in @($auditPath, $summaryPath)) {
    if ([IO.File]::Exists($path) -or [IO.Directory]::Exists($path)) {
        throw 'An audit or summary output already exists; use a new output directory.'
    }
}

$matched = 0
$failedHours = [System.Collections.Generic.List[string]]::new()
for ($index = 0; $index -le $Intervals; $index++) {
    if ((Get-FileHash -LiteralPath $measure -Algorithm SHA256).Hash -ne $measureHash) {
        throw 'The tree comparison tool changed during the run.'
    }
    $hour = $first.AddHours($index)
    $hourText = $hour.ToString('yyyy-MM-ddTHH:mm:ssZ')
    $snapshots = [System.Collections.Generic.List[string]]::new()
    for ($machine = 0; $machine -lt $MachineLabels.Length; $machine++) {
        $name = "snapshot-$run-$($MachineLabels[$machine])-$($hour.ToString('yyyyMMddTHH')).json"
        $snapshots.Add([IO.Path]::Combine([IO.Path]::GetFullPath($InputDirectories[$machine]), $name))
    }
    & $measure -Snapshots $snapshots.ToArray() -MinimumMachines $MinimumMachines `
        -OutputFile $comparePaths[$index] | Out-Null
    $code = $LASTEXITCODE
    if (-not [IO.File]::Exists($comparePaths[$index])) {
        throw 'An hourly comparison did not produce a report.'
    }
    $report = Get-Content -LiteralPath $comparePaths[$index] -Raw | ConvertFrom-Json
    if ($code -eq 0 -and $report.match -eq $true) { $matched++ }
    else { $failedHours.Add($hourText) }
}
& $measure -Reports $comparePaths.ToArray() -MinimumHours $MinimumHours `
    -RequiredMachines $MinimumMachines -OutputFile $auditPath | Out-Null
$auditExit = $LASTEXITCODE
if (-not [IO.File]::Exists($auditPath)) { throw 'The elapsed-time audit did not produce a report.' }
$audit = Get-Content -LiteralPath $auditPath -Raw | ConvertFrom-Json
$summary = [ordered]@{
    schema = 'peersync-soak-comparison-summary-v1'
    checkedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    runId = $run; machineLabels = @($MachineLabels)
    firstHourUtc = $first.ToString('yyyy-MM-ddTHH:mm:ssZ')
    requestedHours = $Intervals + 1; matchedHours = $matched
    failedHours = @($failedHours.ToArray())
    minimumHours = $MinimumHours; minimumMachines = $MinimumMachines
    collectorSha256 = $audit.collectorSha256
    auditPass = ($auditExit -eq 0 -and $audit.pass -eq $true)
    auditIssues = @($audit.issues)
}
$stream = [IO.File]::Open($summaryPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
$writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
try { $writer.Write(($summary | ConvertTo-Json -Depth 6)) } finally { $writer.Dispose() }
Write-Output $summaryPath
if ($failedHours.Count -ne 0 -or $auditExit -ne 0) { exit 1 }
exit 0
