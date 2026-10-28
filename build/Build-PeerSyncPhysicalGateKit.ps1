[CmdletBinding()]
param([string]$OutputPath)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$revision = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $revision -notmatch '^[0-9a-f]{40}$') {
    throw 'Cannot determine the source revision.'
}
$trackedChanges = @(& git -C $root diff --name-only HEAD)
if ($LASTEXITCODE -ne 0 -or $trackedChanges.Count -ne 0) {
    throw 'Commit tracked changes before building the physical gate kit.'
}

$output = if ($OutputPath) { [IO.Path]::GetFullPath($OutputPath) } else {
    [IO.Path]::Combine($root, 'artifacts', 'deploy',
        "PeerSync-physical-gates-$($revision.Substring(0, 8)).zip")
}
$outputParent = [IO.Path]::GetDirectoryName($output)
if (-not [IO.Directory]::Exists($outputParent)) {
    throw 'The output directory must already exist.'
}
if ([IO.File]::Exists($output) -or [IO.Directory]::Exists($output)) {
    throw 'The output path already exists.'
}

$entries = [ordered]@{
    'tools/Get-PeerSyncTestTreeContentBytes.ps1' = 'build/Get-PeerSyncTestTreeContentBytes.ps1'
    'tools/New-PeerSyncGateCorpus.ps1' = 'build/New-PeerSyncGateCorpus.ps1'
    'tools/Invoke-PeerSyncSoakOperations.ps1' = 'build/Invoke-PeerSyncSoakOperations.ps1'
    'tools/Invoke-PeerSyncSoakCaptureLoop.ps1' = 'build/Invoke-PeerSyncSoakCaptureLoop.ps1'
    'tools/Invoke-PeerSyncPhysicalSoak.ps1' = 'build/Invoke-PeerSyncPhysicalSoak.ps1'
    'tools/Register-PeerSyncPhysicalSoakTask.ps1' = 'build/Register-PeerSyncPhysicalSoakTask.ps1'
    'tools/Start-NativeWanSoakIsolation.ps1' = 'build/field/Start-NativeWanSoakIsolation.ps1'
    'tools/Measure-PeerSyncTree.ps1' = 'build/Measure-PeerSyncTree.ps1'
    'tools/Compare-PeerSyncSoakReports.ps1' = 'build/Compare-PeerSyncSoakReports.ps1'
    'docs/physical-soak.md' = 'docs/physical-soak.md'
    'docs/three-hour-ten-gib-test-budget.md' = 'docs/requirements/three-hour-ten-gib-test-budget.md'
}
$hashes = [ordered]@{}
foreach ($entry in $entries.GetEnumerator()) {
    $path = [IO.Path]::Combine($root, $entry.Value.Replace('/', '\'))
    if (-not [IO.File]::Exists($path)) { throw "Missing kit input: $($entry.Value)" }
    $hashes[$entry.Key] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
}

$manifest = [ordered]@{
    schema = 'peersync-physical-gate-kit-v1'
    sourceRevision = $revision
    generatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    corpusDefaultBytes = 9GB
    maximumTestTreeContentBytes = 10GB
    maximumActiveHours = 3
    requiredSmallFiles = 50000
    requiredCaptureIntervals = 3
    files = $hashes
}
$readme = @"
# PeerSync physical gate tools

Source revision: $revision

This ZIP contains verification scripts and instructions only. It contains no
installer, folder key, password, private certificate or service configuration.
Use it with the already installed PeerSync clients on three physical Windows
PCs. Transfer the ZIP through a trusted channel, compare its SHA-256 with the
builder's printed value, then extract the same ZIP on each PC.

The generated corpus defaults to 9 GiB of content and 50,000 small files.
The generator and soak writer check existing tree content before writing;
keep the entire test tree at or below 10 GiB, including later soak writes.
Use the generator's -PreflightOnly switch to inspect the exact budget without
creating any files in the synchronized folder.
The required active window is three hours, with matching tree captures at
scheduled hours 0, 1, 2 and 3. Follow docs/physical-soak.md for the exact
three-PC LAN, WAN and report-comparison steps. The scripts refuse reused
corpus/report paths and do not delete synchronized data.
For native WAN, the optional isolation helper blocks only the installed
PeerSync service on Tailscale. It requires a physical WAN route and a working
TCP/UDP rendezvous probe; retain its local report and cleanup receipt.

manifest.json lists the SHA-256 of every script and document. Confirm the
archive's externally supplied SHA-256 first; the manifest alone cannot
authenticate an archive. A local or virtual-machine run does not satisfy a
physical LAN/WAN gate. Preserve the generated reports, operation journals,
network topology and installed-version evidence.
"@

function Add-TextEntry([IO.Compression.ZipArchive]$archive, [string]$name, [string]$content) {
    $item = $archive.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
    $writer = [IO.StreamWriter]::new($item.Open(), [Text.UTF8Encoding]::new($false))
    try { $writer.Write($content) } finally { $writer.Dispose() }
}

$stream = [IO.File]::Open($output, [IO.FileMode]::CreateNew)
try {
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        foreach ($entry in $entries.GetEnumerator()) {
            $source = [IO.Path]::Combine($root, $entry.Value.Replace('/', '\'))
            $item = $zip.CreateEntry($entry.Key, [IO.Compression.CompressionLevel]::Optimal)
            $input = [IO.File]::OpenRead($source)
            $destination = $item.Open()
            try { $input.CopyTo($destination) }
            finally { $destination.Dispose(); $input.Dispose() }
        }
        Add-TextEntry $zip 'README.md' $readme
        Add-TextEntry $zip 'manifest.json' ($manifest | ConvertTo-Json -Depth 5)
    } finally { $zip.Dispose() }
} finally { $stream.Dispose() }

[pscustomobject]@{
    package = $output
    bytes = ([IO.FileInfo]::new($output)).Length
    sha256 = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
    sourceRevision = $revision
}
