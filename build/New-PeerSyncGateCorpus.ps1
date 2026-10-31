[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Root,
    [Parameter(Mandatory)][guid]$RunId,
    [Parameter(Mandatory)][string]$OutputFile,
    [switch]$PreflightOnly,
    [ValidateRange(1048576, 10737418240)][long]$TotalBytes = 9GB,
    [ValidateRange(1, 100000)][int]$SmallFileCount = 50000,
    [ValidateRange(1, 1048576)][int]$SmallFileBytes = 1024,
    [ValidateRange(1, 128)][int]$DeepLevels = 24,
    [ValidateRange(1, 1048576)][int]$DeepFileBytes = 4096,
    [ValidateRange(1, 128)][int]$LargeFileCount = 8
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Get-PeerSyncTestTreeContentBytes.ps1')

$rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\')
$output = [IO.Path]::GetFullPath($OutputFile)
$parent = [IO.Path]::GetDirectoryName($output)
if ($rootPath.Length -le 3 -or -not [IO.Directory]::Exists($rootPath)) {
    throw 'Root must be an existing synchronized folder below a volume root.'
}
if (-not [IO.Directory]::Exists($parent) -or
    $parent.Equals($rootPath, [StringComparison]::OrdinalIgnoreCase) -or
    $parent.StartsWith($rootPath + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputFile parent must exist outside the synchronized folder.'
}
if ([IO.File]::Exists($output) -or [IO.Directory]::Exists($output)) {
    throw 'OutputFile must be new.'
}

function Assert-OrdinaryAncestors([string]$path) {
    $current = $path
    while ($current) {
        if ([IO.Directory]::Exists($current) -and
            ([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'The corpus path traverses a reparse point.'
        }
        $next = [IO.Path]::GetDirectoryName($current.TrimEnd('\'))
        if (-not $next -or $next -eq $current) { break }
        $current = $next
    }
}
Assert-OrdinaryAncestors $rootPath

$smallBytes = [long]$SmallFileCount * $SmallFileBytes
$deepBytes = [long]$DeepLevels * $DeepFileBytes
if ($TotalBytes -le $smallBytes + $deepBytes) {
    throw 'TotalBytes must leave positive content for the large files.'
}
$largeBytes = $TotalBytes - $smallBytes - $deepBytes
if ($largeBytes -lt $LargeFileCount) {
    throw 'Each large file needs at least one byte.'
}
$drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($rootPath))
$blockReserve = $TotalBytes
$safetyHeadroom = 5GB
$requiredFree = $TotalBytes + $blockReserve + $safetyHeadroom
if ($drive.AvailableFreeSpace -lt $requiredFree) {
    throw "Insufficient free disk space: available $($drive.AvailableFreeSpace) bytes; this generator reserves $requiredFree bytes ($TotalBytes native test content + $blockReserve independent PeerSync blocks + $safetyHeadroom safety headroom). The 10 GiB cap limits synchronized test content, not all on-disk copies."
}

$run = $RunId.ToString('N')
$container = [IO.Path]::Combine($rootPath, 'PeerSync-GateCorpus')
$target = [IO.Path]::Combine($container, $run)
if ([IO.Directory]::Exists($container) -and
    ([IO.File]::GetAttributes($container) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'The corpus container is a reparse point.'
}
if ([IO.Directory]::Exists($target) -or [IO.File]::Exists($target)) {
    throw 'This run already has a corpus folder; inspect it before creating a new run.'
}

# The required cap applies to the entire synchronized tree, including old test
# files and this run's ownership marker, rather than only to new corpus bytes.
$markerReserveBytes = 1024L
$existingBudget = 10GB - $TotalBytes - $markerReserveBytes
if ($existingBudget -lt 0) {
    throw 'The requested corpus leaves no room for its ownership marker under 10 GiB.'
}
$existingContentBytes = Get-PeerSyncTestTreeContentBytes -Root $rootPath `
    -MaximumBytes $existingBudget
if ($PreflightOnly) {
    [pscustomobject]@{
        status = 'ready'
        existingContentBytes = $existingContentBytes
        requestedContentBytes = $TotalBytes
        projectedContentBytesBeforeMarker = $existingContentBytes + $TotalBytes
        maximumTestTreeContentBytes = 10GB
        freeBytes = $drive.AvailableFreeSpace
        requiredFreeBytes = $requiredFree
        blockReserveBytes = $blockReserve
        safetyHeadroomBytes = $safetyHeadroom
        runId = $run
    } | ConvertTo-Json -Compress
    return
}

$relative = "PeerSync-GateCorpus/$run"
$scriptHash = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
$started = [DateTimeOffset]::UtcNow
$deadlineUtc = $started.AddHours(3)
$createdFiles = 0
$createdBytes = [long]0
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
$buffer = [byte[]]::new(4MB)

function Write-Report([string]$status, [string]$errorCode) {
    [ordered]@{
        schema = 'peersync-gate-corpus-v1'
        status = $status
        runId = $run
        corpusRelativePath = $relative
        requestedContentBytes = $TotalBytes
        existingContentBytesAtStart = $existingContentBytes
        smallFileCount = $SmallFileCount
        smallFileBytes = $SmallFileBytes
        deepLevels = $DeepLevels
        deepFileBytes = $DeepFileBytes
        largeFileCount = $LargeFileCount
        createdFiles = $createdFiles
        createdContentBytes = $createdBytes
        generatorSha256 = $scriptHash
        startedUtc = $started.ToString('O')
        endedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        errorCode = $errorCode
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $output -Encoding UTF8
}

function New-RandomFile([string]$path, [long]$size) {
    if ([DateTimeOffset]::UtcNow -ge $deadlineUtc) { throw 'Corpus generation exceeded three hours.' }
    $stream = [IO.FileStream]::new($path, [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write, [IO.FileShare]::None, 1048576,
        [IO.FileOptions]::SequentialScan)
    try {
        $remaining = $size
        while ($remaining -gt 0) {
            if ([DateTimeOffset]::UtcNow -ge $deadlineUtc) { throw 'Corpus generation exceeded three hours.' }
            $count = [int][Math]::Min($buffer.Length, $remaining)
            if ($count -eq $buffer.Length) {
                $rng.GetBytes($buffer)
                $stream.Write($buffer, 0, $count)
            } else {
                $tail = [byte[]]::new($count)
                $rng.GetBytes($tail)
                $stream.Write($tail, 0, $count)
            }
            $remaining -= $count
        }
        $stream.Flush($size -gt 1MB)
    } finally { $stream.Dispose() }
    $script:createdFiles++
    $script:createdBytes += $size
}

try {
    [IO.Directory]::CreateDirectory($target) | Out-Null
    [ordered]@{
        schema = 'peersync-gate-corpus-owner-v1'
        runId = $run
        generatorSha256 = $scriptHash
    } | ConvertTo-Json -Compress | Set-Content -LiteralPath ([IO.Path]::Combine($target, 'owner.json')) -Encoding UTF8
    Write-Report 'running' $null

    $smallRoot = [IO.Path]::Combine($target, 'small')
    [IO.Directory]::CreateDirectory($smallRoot) | Out-Null
    for ($i = 0; $i -lt $SmallFileCount; $i++) {
        if ([DateTimeOffset]::UtcNow -ge $deadlineUtc) { throw 'Corpus generation exceeded three hours.' }
        $bucket = [IO.Path]::Combine($smallRoot, ('{0:D4}' -f [int][Math]::Floor($i / 1000)))
        if ($i % 1000 -eq 0) { [IO.Directory]::CreateDirectory($bucket) | Out-Null }
        New-RandomFile ([IO.Path]::Combine($bucket, ('{0:D7}.bin' -f $i))) $SmallFileBytes
        if (($i + 1) % 10000 -eq 0) { Write-Output "PROGRESS smallFiles=$($i + 1)/$SmallFileCount" }
    }

    $deep = [IO.Path]::Combine($target, 'deep')
    for ($level = 1; $level -le $DeepLevels; $level++) {
        $deep = [IO.Path]::Combine($deep, ('level-{0:D3}' -f $level))
        [IO.Directory]::CreateDirectory($deep) | Out-Null
        New-RandomFile ([IO.Path]::Combine($deep, ('depth-{0:D3}.bin' -f $level))) $DeepFileBytes
    }

    $largeRoot = [IO.Path]::Combine($target, 'large')
    [IO.Directory]::CreateDirectory($largeRoot) | Out-Null
    $each = [long][Math]::Floor($largeBytes / $LargeFileCount)
    $extra = [long]($largeBytes % $LargeFileCount)
    for ($i = 0; $i -lt $LargeFileCount; $i++) {
        $size = $each + $(if ($i -lt $extra) { 1L } else { 0L })
        New-RandomFile ([IO.Path]::Combine($largeRoot, ('large-{0:D3}.bin' -f $i))) $size
        Write-Output "PROGRESS largeFiles=$($i + 1)/$LargeFileCount contentBytes=$createdBytes/$TotalBytes"
    }
    if ($createdFiles -ne $SmallFileCount + $DeepLevels + $LargeFileCount -or
        $createdBytes -ne $TotalBytes) {
        throw 'Corpus content count or byte total is inconsistent.'
    }
    [void](Get-PeerSyncTestTreeContentBytes -Root $rootPath -MaximumBytes 10GB)
    Write-Report 'complete' $null
    Write-Output $output
} catch {
    Write-Report 'failed' $_.Exception.GetType().Name
    throw
} finally { $rng.Dispose() }
