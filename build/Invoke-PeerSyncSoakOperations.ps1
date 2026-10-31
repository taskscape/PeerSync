[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Root,
    [Parameter(Mandatory)][guid]$RunId,
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9_-]{1,32}$')][string]$MachineLabel,
    [Parameter(Mandatory)][string]$LogDirectory,
    [ValidateRange(1, 1000)][int]$Count = 10,
    [ValidateRange(0, 60)][int]$DelaySeconds = 0,
    [ValidateRange(1, 1000)][int]$MaxOwnedFiles = 200,
    [int]$Seed
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Get-PeerSyncTestTreeContentBytes.ps1')

$rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\')
$logs = [IO.Path]::GetFullPath($LogDirectory).TrimEnd('\')
if ($rootPath.Length -le 3 -or -not [IO.Directory]::Exists($rootPath)) {
    throw 'Root must be an existing synchronized folder below a volume root.'
}
if (-not [IO.Directory]::Exists($logs) -or
    $logs.StartsWith($rootPath + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $logs.Equals($rootPath, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'LogDirectory must exist outside the synchronized folder.'
}
if (([IO.File]::GetAttributes($rootPath) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'Root cannot be a reparse point.'
}

# Reserve the largest possible growth of this batch on each of three peers.
# Each batch remeasures the whole tree, so old soak files consume the budget.
$growthReserveBytes = [long]$Count * 65536 * 3
$currentContentBytes = Get-PeerSyncTestTreeContentBytes -Root $rootPath `
    -MaximumBytes (10GB - $growthReserveBytes)

$run = $RunId.ToString('N')
$container = [IO.Path]::Combine($rootPath, 'PeerSync-Soak', $run)
$owned = [IO.Path]::Combine($container, $MachineLabel)
foreach ($part in @([IO.Path]::Combine($rootPath, 'PeerSync-Soak'), $container, $owned)) {
    if ([IO.Directory]::Exists($part) -and
        ([IO.File]::GetAttributes($part) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The workload path contains a reparse point.'
    }
}
$marker = [IO.Path]::Combine($owned, 'owner.json')
if ([IO.Directory]::Exists($owned)) {
    if (-not [IO.File]::Exists($marker)) { throw 'Existing workload folder has no ownership marker.' }
    $owner = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
    if ($owner.schema -ne 'peersync-soak-owner-v1' -or $owner.runId -ne $run -or
        $owner.machineLabel -cne $MachineLabel) {
        throw 'Existing workload folder belongs to a different run or machine.'
    }
} else {
    [IO.Directory]::CreateDirectory($owned) | Out-Null
    for ($bucket = 0; $bucket -lt 8; $bucket++) {
        [IO.Directory]::CreateDirectory([IO.Path]::Combine($owned, ('{0:D2}' -f $bucket))) | Out-Null
    }
    [ordered]@{ schema = 'peersync-soak-owner-v1'; runId = $run;
        machineLabel = $MachineLabel } | ConvertTo-Json -Compress |
        Set-Content -LiteralPath $marker -Encoding UTF8
}

$paths = [System.Collections.Generic.List[string]]::new()
for ($bucket = 0; $bucket -lt 8; $bucket++) {
    $directory = [IO.Path]::Combine($owned, ('{0:D2}' -f $bucket))
    if (-not [IO.Directory]::Exists($directory) -or
        ([IO.File]::GetAttributes($directory) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'A workload bucket is missing or redirected.'
    }
    foreach ($path in [IO.Directory]::EnumerateFileSystemEntries($directory)) {
        $name = [IO.Path]::GetFileName($path)
        if ($name -cnotmatch '^item-[0-9a-f]{32}\.bin$' -or
            ([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            -not [IO.File]::Exists($path)) {
            throw 'A workload bucket contains an unexpected entry.'
        }
        $paths.Add($path)
    }
}
if ($paths.Count -gt $MaxOwnedFiles) { throw 'Owned file count exceeds the configured cap.' }

if ($PSBoundParameters.ContainsKey('Seed')) { $randomSeed = $Seed }
else {
    $seedBytes = [byte[]]::new(4)
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($seedBytes) } finally { $generator.Dispose() }
    $randomSeed = [BitConverter]::ToInt32($seedBytes, 0)
}
$random = [Random]::new($randomSeed)
$logPath = [IO.Path]::Combine($logs, "operations-$run-$MachineLabel-$([Guid]::NewGuid().ToString('N')).jsonl")
$stream = [IO.File]::Open($logPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
$writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
$writer.AutoFlush = $true
$completed = 0
$failed = 0
$scriptHash = (Get-FileHash -LiteralPath $MyInvocation.MyCommand.Path -Algorithm SHA256).Hash

function Write-Record([object]$record) {
    $writer.WriteLine(($record | ConvertTo-Json -Compress -Depth 5))
}

function New-Content([int]$limit) {
    $length = $random.Next(1, $limit + 1)
    $content = [byte[]]::new($length)
    $random.NextBytes($content)
    return ,$content
}

function Get-ItemId([string]$path) {
    return [IO.Path]::GetFileNameWithoutExtension($path).Substring(5)
}

try {
    Write-Record ([ordered]@{ schema = 'peersync-soak-operations-v1'; kind = 'start';
        runId = $run; machineLabel = $MachineLabel;
        utc = [DateTimeOffset]::UtcNow.ToString('O'); seed = $randomSeed;
        requested = $Count; ownedFilesBefore = $paths.Count;
        treeContentBytesAtStart = $currentContentBytes;
        growthReserveBytes = $growthReserveBytes;
        generatorSha256 = $scriptHash })
    for ($number = 1; $number -le $Count; $number++) {
        $choice = if ($paths.Count -eq 0) { 'create' }
            elseif ($paths.Count -ge $MaxOwnedFiles) { @('overwrite', 'append', 'rename', 'delete')[$random.Next(4)] }
            else { @('create', 'create', 'overwrite', 'append', 'rename', 'delete')[$random.Next(6)] }
        $record = [ordered]@{ schema = 'peersync-soak-operations-v1'; kind = 'operation';
            runId = $run; machineLabel = $MachineLabel; number = $number;
            utc = [DateTimeOffset]::UtcNow.ToString('O'); operation = $choice;
            status = 'failed'; itemId = $null; toItemId = $null;
            bytes = 0; sha256 = $null; errorType = $null; hresult = $null }
        try {
            if ($choice -eq 'create') {
                $id = [Guid]::NewGuid().ToString('N')
                $directory = [IO.Path]::Combine($owned, ('{0:D2}' -f $random.Next(8)))
                $path = [IO.Path]::Combine($directory, "item-$id.bin")
                $content = New-Content 65536
                $file = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                try { $file.Write($content, 0, $content.Length) } finally { $file.Dispose() }
                $paths.Add($path)
                $record.itemId = $id
            } else {
                $index = $random.Next($paths.Count)
                $path = $paths[$index]
                $record.itemId = Get-ItemId $path
                if (-not [IO.File]::Exists($path) -or
                    ([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw [IO.IOException]::new('Selected workload file changed or was redirected.')
                }
                switch ($choice) {
                    'overwrite' {
                        $content = New-Content 65536
                        $file = [IO.File]::Open($path, [IO.FileMode]::Truncate, [IO.FileAccess]::Write, [IO.FileShare]::None)
                        try { $file.Write($content, 0, $content.Length) } finally { $file.Dispose() }
                    }
                    'append' {
                        $content = New-Content 16384
                        $file = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::None)
                        try { $file.Seek(0, [IO.SeekOrigin]::End) | Out-Null; $file.Write($content, 0, $content.Length) }
                        finally { $file.Dispose() }
                    }
                    'rename' {
                        $id = [Guid]::NewGuid().ToString('N')
                        $destination = [IO.Path]::Combine([IO.Path]::GetDirectoryName($path), "item-$id.bin")
                        [IO.File]::Move($path, $destination)
                        $paths[$index] = $destination
                        $path = $destination
                        $record.toItemId = $id
                    }
                    'delete' {
                        $record.bytes = ([IO.FileInfo]::new($path)).Length
                        $record.sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
                        [IO.File]::Delete($path)
                        $paths.RemoveAt($index)
                    }
                }
            }
            if ($choice -ne 'delete') {
                $record.bytes = ([IO.FileInfo]::new($path)).Length
                $record.sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
            }
            $record.status = 'complete'
            $completed++
        } catch {
            $record.errorType = $_.Exception.GetType().Name
            $record.hresult = ('0x{0:X8}' -f ($_.Exception.HResult -band 0xFFFFFFFF))
            $failed++
        }
        Write-Record $record
        if ($failed -gt 0) { break }
        if ($number -lt $Count -and $DelaySeconds -gt 0) { Start-Sleep -Seconds $DelaySeconds }
    }
    Write-Record ([ordered]@{ schema = 'peersync-soak-operations-v1'; kind = 'end';
        runId = $run; machineLabel = $MachineLabel;
        utc = [DateTimeOffset]::UtcNow.ToString('O'); completed = $completed;
        failed = $failed; ownedFilesAfter = $paths.Count })
} finally { $writer.Dispose() }

[pscustomobject]@{ runId = $run; machineLabel = $MachineLabel;
    completed = $completed; failed = $failed; logPath = $logPath;
    ownedFiles = $paths.Count }
if ($failed -gt 0) { exit 1 }
exit 0
