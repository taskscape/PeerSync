[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Root,
    [Parameter(Mandatory)][string]$OutputFile
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\')
$outputPath = [IO.Path]::GetFullPath($OutputFile)
if ($rootPath.Length -le 3 -or -not [IO.Directory]::Exists($rootPath)) {
    throw 'Archive root must be an existing directory below a volume root.'
}
if ($outputPath.Equals($rootPath,[StringComparison]::OrdinalIgnoreCase) -or
    $outputPath.StartsWith($rootPath + '\',[StringComparison]::OrdinalIgnoreCase) -or
    -not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($outputPath)) -or
    [IO.File]::Exists($outputPath)) {
    throw 'OutputFile must be new and outside the archive root.'
}
if ($rootPath.Split([char]'\').Count -lt 3) { throw 'Archive root is too broad.' }

$extendedRoot = '\\?\' + $rootPath
$files = [System.Collections.Generic.List[string]]::new()
$queue = [System.Collections.Generic.Queue[string]]::new()
$queue.Enqueue($extendedRoot)
while ($queue.Count -gt 0) {
    $directory = $queue.Dequeue()
    foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($directory)) {
        $attributes = [IO.File]::GetAttributes($entry)
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Archive contains a reparse point: $entry"
        }
        if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) { $queue.Enqueue($entry) }
        else { $files.Add($entry) }
    }
}
$ordered = $files.ToArray()
[Array]::Sort($ordered,[StringComparer]::Ordinal)
$aggregate = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
$fileHash = [Security.Cryptography.SHA256]::Create()
$bytes = [long]0
try {
    foreach ($path in $ordered) {
        $relative = $path.Substring($extendedRoot.Length + 1).Replace('\','/')
        $before = [IO.FileInfo]::new($path)
        $size = $before.Length
        $stream = [IO.FileStream]::new($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,
            [IO.FileShare]::Read,1048576,[IO.FileOptions]::SequentialScan)
        try { $digest = [BitConverter]::ToString($fileHash.ComputeHash($stream)).Replace('-','') }
        finally { $stream.Dispose() }
        if (([IO.FileInfo]::new($path)).Length -ne $size) { throw "File size changed during hashing: $path" }
        $record = $relative + "`t" + $size + "`t" + $digest + "`n"
        $aggregate.AppendData([Text.Encoding]::UTF8.GetBytes($record))
        $bytes += $size
    }
    $treeDigest = [BitConverter]::ToString($aggregate.GetHashAndReset()).Replace('-','')
} finally {
    $fileHash.Dispose()
    $aggregate.Dispose()
}
$result = [ordered]@{
    schema='peersync-archive-digest-v1'
    root=$rootPath
    files=$ordered.Length
    bytes=$bytes
    sha256=$treeDigest
    measuredUtc=[DateTimeOffset]::UtcNow.ToString('O')
}
$json = $result | ConvertTo-Json -Depth 3
[IO.File]::WriteAllText($outputPath,$json + [Environment]::NewLine,[Text.UTF8Encoding]::new($false))
$json
