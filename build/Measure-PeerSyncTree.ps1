[CmdletBinding(DefaultParameterSetName = 'Capture')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Capture')][string]$Root,
    [Parameter(Mandatory, ParameterSetName = 'Capture')][guid]$RunId,
    [Parameter(Mandatory, ParameterSetName = 'Capture')]
    [ValidatePattern('^[A-Za-z0-9_-]{1,32}$')][string]$MachineLabel,
    [Parameter(Mandatory, ParameterSetName = 'Capture')]
    [ValidatePattern('^\d{4}-\d{2}-\d{2}T\d{2}:00:00Z$')][string]$HourUtc,
    [Parameter(Mandatory, ParameterSetName = 'Compare')]
    [ValidateCount(2, 16)][string[]]$Snapshots,
    [Parameter(ParameterSetName = 'Compare')]
    [ValidateRange(2, 16)][int]$MinimumMachines = 3,
    [Parameter(ParameterSetName = 'Compare')]
    [ValidateRange(1, 15)][int]$MaxStartSkewMinutes = 15,
    [Parameter(Mandatory, ParameterSetName = 'Audit')]
    [ValidateCount(2, 1000)][string[]]$Reports,
    [Parameter(ParameterSetName = 'Audit')]
    [ValidateRange(1, 3)][int]$MinimumHours = 3,
    [Parameter(ParameterSetName = 'Audit')]
    [ValidateRange(2, 16)][int]$RequiredMachines = 3,
    [Parameter(Mandatory)][string]$OutputFile
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$output = [IO.Path]::GetFullPath($OutputFile)
if ([IO.File]::Exists($output) -or [IO.Directory]::Exists($output)) {
    throw 'The tree-report output must be a new file.'
}
$parent = [IO.Path]::GetDirectoryName($output)
if (-not [IO.Directory]::Exists($parent)) { throw 'The tree-report output directory must already exist.' }

function Save-Report([object]$report) {
    $temporary = $output + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temporary -Encoding UTF8
        [IO.File]::Move($temporary, $output)
    } finally {
        if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
    Write-Output $output
}

function Convert-ToUtc([object]$value) {
    if ($value -is [DateTimeOffset]) { return $value.UtcDateTime }
    if ($value -is [DateTime]) { return $value.ToUniversalTime() }
    return [DateTime]::Parse([string]$value,
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::AssumeUniversal).ToUniversalTime()
}

function Get-HourValue([object]$value) {
    $utc = Convert-ToUtc $value
    if ($utc.Minute -ne 0 -or $utc.Second -ne 0 -or $utc.Millisecond -ne 0) {
        throw 'Snapshot hour is not on a UTC hour boundary.'
    }
    return $utc.ToString('yyyy-MM-ddTHH:mm:ssZ',
        [Globalization.CultureInfo]::InvariantCulture)
}

function Get-Entries([string]$rootPath, [string]$prefix) {
    $entries = [System.Collections.Generic.List[string]]::new()
    $queue = [System.Collections.Generic.Queue[string]]::new()
    $queue.Enqueue($rootPath)
    while ($queue.Count -gt 0) {
        $directory = $queue.Dequeue()
        foreach ($path in [IO.Directory]::EnumerateFileSystemEntries($directory)) {
            $relative = $path.Substring($prefix.Length)
            if ($relative.Equals('.psp', [StringComparison]::OrdinalIgnoreCase) -and
                $directory.Equals($rootPath, [StringComparison]::OrdinalIgnoreCase)) { continue }
            $attributes = [IO.File]::GetAttributes($path)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw [IO.IOException]::new('A reparse point prevents a stable tree measurement.')
            }
            $entries.Add($path)
            if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) { $queue.Enqueue($path) }
        }
    }
    $array = $entries.ToArray()
    [Array]::Sort($array, [StringComparer]::Ordinal)
    return ,$array
}

function Add-Frame([Security.Cryptography.IncrementalHash]$hasher,
    [byte]$kind, [byte[]]$relative, [long]$size, [byte[]]$contentHash) {
    $hasher.AppendData([byte[]]@($kind))
    $hasher.AppendData([BitConverter]::GetBytes([int]$relative.Length))
    $hasher.AppendData($relative)
    if ($kind -eq 70) {
        $hasher.AppendData([BitConverter]::GetBytes($size))
        $hasher.AppendData($contentHash)
    }
}

if ($PSCmdlet.ParameterSetName -eq 'Capture') {
    $start = [DateTimeOffset]::UtcNow
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    if ($rootPath.Length -le 3 -or -not [IO.Directory]::Exists($rootPath)) {
        throw 'Capture requires an existing folder below a volume root.'
    }
    $prefix = $rootPath + '\'
    if ($output.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Write the report outside the synchronized tree.'
    }
    if (([IO.File]::GetAttributes($rootPath) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Capture root cannot be a reparse point.'
    }
    $hour = [DateTime]::ParseExact($HourUtc, 'yyyy-MM-ddTHH:mm:ssZ',
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::AssumeUniversal).ToUniversalTime()
    $normalizedHour = $hour.ToString('yyyy-MM-ddTHH:mm:ssZ')
    $scriptHash = (Get-FileHash -LiteralPath $MyInvocation.MyCommand.Path -Algorithm SHA256).Hash
    $files = 0L
    $directories = 0L
    $bytes = 0L
    $hasher = $null
    $fileHasher = $null
    try {
        if ($start.UtcDateTime -lt $hour -or $start.UtcDateTime -ge $hour.AddHours(1)) {
            throw [IO.IOException]::new('The capture started outside its nominated UTC hour.')
        }
        $entries = Get-Entries $rootPath $prefix
        $hasher = [Security.Cryptography.IncrementalHash]::CreateHash(
            [Security.Cryptography.HashAlgorithmName]::SHA256)
        $fileHasher = [Security.Cryptography.SHA256]::Create()
        foreach ($path in $entries) {
            $relative = $path.Substring($prefix.Length).Replace('\', '/')
            $relativeBytes = [Text.Encoding]::UTF8.GetBytes($relative)
            if ([IO.Directory]::Exists($path)) {
                Add-Frame $hasher 68 $relativeBytes 0 $null
                $directories++
                continue
            }
            $before = [IO.FileInfo]::new($path)
            $beforeSize = $before.Length
            if ($beforeSize -gt 10GB -or $bytes + $beforeSize -gt 10GB) {
                throw [IO.IOException]::new('Test tree exceeds the 10 GiB content budget.')
            }
            $beforeTicks = $before.LastWriteTimeUtc.Ticks
            $share = [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete
            $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, $share)
            try { $contentHash = $fileHasher.ComputeHash($stream) }
            finally { $stream.Dispose() }
            $after = [IO.FileInfo]::new($path)
            if (-not $after.Exists -or $after.Length -ne $beforeSize -or
                $after.LastWriteTimeUtc.Ticks -ne $beforeTicks) {
                throw [IO.IOException]::new('A file changed during tree measurement.')
            }
            Add-Frame $hasher 70 $relativeBytes $beforeSize $contentHash
            $files++
            $bytes += $beforeSize
        }
        $afterEntries = Get-Entries $rootPath $prefix
        if ($entries.Length -ne $afterEntries.Length) {
            throw [IO.IOException]::new('Tree entry count changed during measurement.')
        }
        for ($index = 0; $index -lt $entries.Length; $index++) {
            if ($entries[$index] -cne $afterEntries[$index]) {
                throw [IO.IOException]::new('Tree entries changed during measurement.')
            }
        }
        $digest = [BitConverter]::ToString($hasher.GetHashAndReset()).Replace('-', '')
        $report = [ordered]@{
            schema = 'peersync-tree-snapshot-v1'; status = 'complete'
            runId = $RunId.ToString('N'); machineLabel = $MachineLabel
            hourUtc = $normalizedHour
            startedUtc = $start.ToString('O'); endedUtc = [DateTimeOffset]::UtcNow.ToString('O')
            collectorSha256 = $scriptHash; files = $files; directories = $directories
            bytes = $bytes; treeSha256 = $digest
            scope = 'relative names, directory shape, file sizes and SHA-256 contents; root .psp excluded'
        }
        Save-Report $report
    } catch {
        $report = [ordered]@{
            schema = 'peersync-tree-snapshot-v1'; status = 'failed'
            runId = $RunId.ToString('N'); machineLabel = $MachineLabel
            hourUtc = $normalizedHour; startedUtc = $start.ToString('O')
            endedUtc = [DateTimeOffset]::UtcNow.ToString('O')
            errorType = $_.Exception.GetType().Name
        }
        Save-Report $report
        exit 1
    } finally {
        if ($fileHasher) { $fileHasher.Dispose() }
        if ($hasher) { $hasher.Dispose() }
    }
    exit 0
}

if ($PSCmdlet.ParameterSetName -eq 'Audit') {
    $auditIssues = [System.Collections.Generic.List[string]]::new()
    $hours = [System.Collections.Generic.List[object]]::new()
    foreach ($reportPath in $Reports) {
        try {
            $item = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
            if ($item.schema -ne 'peersync-tree-compare-v1' -or $item.match -ne $true -or
                @($item.issues).Count -ne 0 -or $item.machineCount -lt $RequiredMachines -or
                $item.runId -notmatch '^[0-9a-fA-F]{32}$' -or
                $item.collectorSha256 -notmatch '^[0-9a-fA-F]{64}$') {
                throw 'Invalid or failed hourly comparison.'
            }
            $normalizedHour = Get-HourValue $item.hourUtc
            $timestamp = Convert-ToUtc $item.hourUtc
            $checked = Convert-ToUtc $item.checkedUtc
            if ($checked -lt $timestamp -or @($item.snapshots).Count -lt $RequiredMachines) {
                throw 'The hourly comparison has incomplete timing or peer evidence.'
            }
            $labels = @($item.snapshots | ForEach-Object { $_.machineLabel } | Sort-Object -Unique)
            if ($labels.Count -ne $item.machineCount -or
                @($labels | Where-Object { $_ -notmatch '^[A-Za-z0-9_-]{1,32}$' }).Count -ne 0) {
                throw 'The hourly machine set is invalid.'
            }
            $starts = [System.Collections.Generic.List[DateTime]]::new()
            foreach ($snapshot in $item.snapshots) {
                $captureStart = Convert-ToUtc $snapshot.startedUtc
                $captureEnd = Convert-ToUtc $snapshot.endedUtc
                if ($captureStart -lt $timestamp -or
                    $captureStart -gt $timestamp.AddMinutes(15) -or
                    $captureEnd -lt $captureStart -or $captureEnd -gt $checked) {
                    throw 'An hourly snapshot has inconsistent timestamps.'
                }
                $starts.Add($captureStart)
            }
            $sortedStarts = @($starts | Sort-Object)
            $hours.Add([pscustomobject]@{ runId = $item.runId; hourUtc = $normalizedHour;
                timestamp = $timestamp; firstStart = $sortedStarts[0];
                lastStart = $sortedStarts[-1];
                collectorSha256 = $item.collectorSha256; labels = ($labels -join ',');
                machines = [int]$item.machineCount })
        } catch { $auditIssues.Add('hourly-report-invalid') }
    }
    $sorted = @($hours | Sort-Object timestamp)
    if ($sorted.Count -lt ($MinimumHours + 1)) { $auditIssues.Add('insufficient-hours') }
    if ($sorted.Count -gt 4) { $auditIssues.Add('exceeds-three-hour-cap') }
    for ($index = 1; $index -lt $sorted.Count; $index++) {
        if ($sorted[$index].runId -ne $sorted[0].runId) { $auditIssues.Add('run-id-mismatch') }
        if ($sorted[$index].collectorSha256 -ne $sorted[0].collectorSha256) {
            $auditIssues.Add('collector-version-mismatch')
        }
        if ($sorted[$index].labels -cne $sorted[0].labels) { $auditIssues.Add('machine-set-mismatch') }
        if (($sorted[$index].timestamp - $sorted[$index - 1].timestamp).TotalHours -ne 1) {
            $auditIssues.Add('missing-or-duplicate-hour')
        }
    }
    $elapsedHours = if ($sorted.Count -gt 1) {
        ($sorted[-1].timestamp - $sorted[0].timestamp).TotalHours
    } else { 0 }
    if ($elapsedHours -lt $MinimumHours) {
        $auditIssues.Add('insufficient-elapsed-time')
    }
    if ($elapsedHours -gt 3) { $auditIssues.Add('exceeds-three-hour-cap') }
    $audit = [ordered]@{
        schema = 'peersync-tree-soak-audit-v1'; checkedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        runId = if ($sorted.Count -gt 0) { $sorted[0].runId } else { $null }
        firstHourUtc = if ($sorted.Count -gt 0) { $sorted[0].hourUtc } else { $null }
        lastHourUtc = if ($sorted.Count -gt 0) { $sorted[-1].hourUtc } else { $null }
        passedHours = $sorted.Count; requiredIntervals = $MinimumHours
        elapsedHours = $elapsedHours
        observedCaptureStartSpanHours = if ($sorted.Count -gt 1) {
            ($sorted[-1].firstStart - $sorted[0].lastStart).TotalHours
        } else { 0 }
        requiredMachines = $RequiredMachines
        machineLabels = if ($sorted.Count -gt 0) { $sorted[0].labels } else { $null }
        collectorSha256 = if ($sorted.Count -gt 0) { $sorted[0].collectorSha256 } else { $null }
        issues = @($auditIssues.ToArray()); pass = $auditIssues.Count -eq 0
    }
    Save-Report $audit
    if ($auditIssues.Count -ne 0) { exit 1 }
    exit 0
}

$issues = [System.Collections.Generic.List[string]]::new()
$loaded = [System.Collections.Generic.List[object]]::new()
foreach ($snapshotPath in $Snapshots) {
    try {
        $item = Get-Content -LiteralPath $snapshotPath -Raw | ConvertFrom-Json
        if ($item.schema -ne 'peersync-tree-snapshot-v1' -or $item.status -ne 'complete' -or
            $item.runId -notmatch '^[0-9a-fA-F]{32}$' -or
            $item.machineLabel -notmatch '^[A-Za-z0-9_-]{1,32}$' -or
            $item.collectorSha256 -notmatch '^[0-9a-fA-F]{64}$' -or
            $item.treeSha256 -notmatch '^[0-9a-fA-F]{64}$' -or
            $item.files -lt 0 -or $item.directories -lt 0 -or
            $item.bytes -lt 0 -or $item.bytes -gt 10GB) {
            throw 'Incomplete snapshot.'
        }
        $item.hourUtc = Get-HourValue $item.hourUtc
        $loaded.Add($item)
    } catch { $issues.Add('snapshot-invalid') }
}
if ($loaded.Count -lt $MinimumMachines) { $issues.Add('insufficient-machines') }
if ($loaded.Count -gt 0) {
    $first = $loaded[0]
    if (@($loaded | Select-Object -ExpandProperty machineLabel -Unique).Count -ne $loaded.Count) {
        $issues.Add('duplicate-machine-label')
    }
    if (@($loaded | Where-Object { $_.runId -ne $first.runId }).Count -gt 0) { $issues.Add('run-id-mismatch') }
    if (@($loaded | Where-Object { $_.hourUtc -ne $first.hourUtc }).Count -gt 0) { $issues.Add('hour-mismatch') }
    if (@($loaded | Where-Object { $_.collectorSha256 -ne $first.collectorSha256 }).Count -gt 0) {
        $issues.Add('collector-version-mismatch')
    }
    if (@($loaded | Where-Object { $_.treeSha256 -ne $first.treeSha256 -or
        $_.files -ne $first.files -or $_.directories -ne $first.directories -or
        $_.bytes -ne $first.bytes }).Count -gt 0) { $issues.Add('tree-mismatch') }
    try {
        $starts = @($loaded | ForEach-Object { Convert-ToUtc $_.startedUtc } | Sort-Object)
        if (($starts[-1] - $starts[0]).TotalMinutes -gt $MaxStartSkewMinutes) {
            $issues.Add('capture-start-skew')
        }
        $expectedHour = Convert-ToUtc $first.hourUtc
        foreach ($snapshot in $loaded) {
            $captureStart = Convert-ToUtc $snapshot.startedUtc
            $captureEnd = Convert-ToUtc $snapshot.endedUtc
            if ($captureStart -lt $expectedHour -or
                $captureStart -gt $expectedHour.AddMinutes(15) -or
                $captureEnd -lt $captureStart) {
                $issues.Add('capture-time-invalid')
            }
        }
    } catch { $issues.Add('capture-time-invalid') }
}
$report = [ordered]@{
    schema = 'peersync-tree-compare-v1'; checkedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    runId = if ($loaded.Count -gt 0) { $loaded[0].runId } else { $null }
    hourUtc = if ($loaded.Count -gt 0) { $loaded[0].hourUtc } else { $null }
    machineCount = $loaded.Count; requiredMachines = $MinimumMachines
    collectorSha256 = if ($loaded.Count -gt 0) { $loaded[0].collectorSha256 } else { $null }
    snapshots = @($loaded | ForEach-Object {
        [ordered]@{ machineLabel = $_.machineLabel; files = $_.files
            directories = $_.directories; bytes = $_.bytes; treeSha256 = $_.treeSha256
            startedUtc = (Convert-ToUtc $_.startedUtc).ToString('O')
            endedUtc = (Convert-ToUtc $_.endedUtc).ToString('O') }
    })
    issues = @($issues.ToArray()); match = $issues.Count -eq 0
}
Save-Report $report
if ($issues.Count -ne 0) { exit 1 }
exit 0
