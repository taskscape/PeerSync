[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Root,
    [Parameter(Mandatory)][guid]$RunId,
    [Parameter(Mandatory)][string]$ArchiveRoot,
    [Parameter(Mandatory)][string]$GeneratorSha256,
    [Parameter(Mandatory)][string]$OutputFile,
    [string]$OwnerMarkerPath,
    [ValidatePattern('^[a-zA-Z0-9-]+$')][string]$ArchiveSuffix,
    [switch]$Move
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function FullDirectory([string]$path) {
    $full = [IO.Path]::GetFullPath($path).TrimEnd('\')
    if ($full.Length -le 3 -or -not [IO.Directory]::Exists($full)) {
        throw "Expected an existing directory below a volume root: $path"
    }
    return $full
}

function Assert-OrdinaryAncestors([string]$path) {
    $current = $path
    while ($current) {
        if ([IO.Directory]::Exists($current) -and
            ([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "A path traverses a reparse point: $current"
        }
        $next = [IO.Path]::GetDirectoryName($current.TrimEnd('\'))
        if (-not $next -or $next -eq $current) { break }
        $current = $next
    }
}

$rootPath = FullDirectory $Root
$archivePath = FullDirectory $ArchiveRoot
$run = $RunId.ToString('N')
$source = [IO.Path]::Combine($rootPath, 'PeerSync-GateCorpus', $run)
$targetName = if ($ArchiveSuffix) { "$run-$ArchiveSuffix" } else { $run }
$target = [IO.Path]::Combine($archivePath, $targetName)
$output = [IO.Path]::GetFullPath($OutputFile)
$outputParent = [IO.Path]::GetDirectoryName($output)

if (-not [IO.Directory]::Exists($source)) { throw "Exact corpus run is absent: $source" }
if ([IO.Directory]::Exists($target) -or [IO.File]::Exists($target)) {
    throw "Archive target already exists: $target"
}
if (-not [IO.Directory]::Exists($outputParent) -or [IO.File]::Exists($output)) {
    throw 'OutputFile parent must exist and OutputFile must be new.'
}
foreach ($path in @($archivePath, $outputParent)) {
    if ($path.Equals($rootPath, [StringComparison]::OrdinalIgnoreCase) -or
        $path.StartsWith($rootPath + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Archive and report must remain outside the synchronized root.'
    }
}
if (-not [IO.Path]::GetPathRoot($rootPath).Equals(
        [IO.Path]::GetPathRoot($archivePath), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Archive must be on the same volume for an atomic directory move.'
}
if ($GeneratorSha256 -notmatch '^[A-Fa-f0-9]{64}$') {
    throw 'GeneratorSha256 must be a 64-digit SHA-256 value.'
}
Assert-OrdinaryAncestors $source
Assert-OrdinaryAncestors $archivePath
Assert-OrdinaryAncestors $outputParent

$marker = if ($OwnerMarkerPath) { [IO.Path]::GetFullPath($OwnerMarkerPath) }
    else { [IO.Path]::Combine($source, 'owner.json') }
Assert-OrdinaryAncestors ([IO.Path]::GetDirectoryName($marker))
if (-not [IO.File]::Exists($marker) -or
    ([IO.File]::GetAttributes($marker) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'The exact run ownership marker is missing or is a reparse point.'
}
$owner = (Get-Content -LiteralPath $marker -Raw).TrimStart([char]0xFEFF) | ConvertFrom-Json
if ($owner.schema -ne 'peersync-gate-corpus-owner-v1' -or
    $owner.runId -ne $run -or
    $owner.generatorSha256 -ne $GeneratorSha256.ToUpperInvariant()) {
    throw 'The ownership marker does not match this run and generator.'
}

$queue = [System.Collections.Generic.Queue[string]]::new()
$queue.Enqueue($source)
$files = [long]0
$bytes = [long]0
while ($queue.Count -gt 0) {
    $directory = $queue.Dequeue()
    foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($directory)) {
        $attributes = [IO.File]::GetAttributes($entry)
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Corpus contains a reparse point: $entry"
        }
        if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
            $queue.Enqueue($entry)
        } else {
            $files++
            $bytes += ([IO.FileInfo]::new($entry)).Length
        }
    }
}

$service = Get-Service PeerSync -ErrorAction Stop
$result = [ordered]@{
    schema = 'peersync-gate-corpus-archive-v1'
    observedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    machine = $env:COMPUTERNAME
    runId = $run
    source = $source
    archive = $target
    ownerMarker = $marker
    files = $files
    bytes = $bytes
    serviceStatus = $service.Status.ToString()
    moved = $false
}
if ($Move) {
    if ($service.Status -ne 'Stopped') {
        throw 'Stop PeerSync on every participating PC before moving a corpus.'
    }
    [IO.Directory]::Move($source, $target)
    if ([IO.Directory]::Exists($source) -or -not [IO.Directory]::Exists($target)) {
        throw 'Corpus move did not leave the expected source and archive state.'
    }
    $result.moved = $true
}
$result.completedUtc = [DateTimeOffset]::UtcNow.ToString('O')
$json = $result | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText($output, $json + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))
$json
