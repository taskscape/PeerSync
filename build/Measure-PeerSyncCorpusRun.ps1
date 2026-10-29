[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Root,
    [Parameter(Mandatory)][guid]$RunId,
    [Parameter(Mandatory)][ValidateSet('first','second','third')][string]$MachineLabel,
    [Parameter(Mandatory)][string]$OutputFile
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$output = [IO.Path]::GetFullPath($OutputFile)
if ([IO.File]::Exists($output) -or [IO.Directory]::Exists($output)) {
    throw 'Corpus-run output already exists.'
}
if (-not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($output))) {
    throw 'Corpus-run output directory is missing.'
}
$rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\')
if ($rootPath.Length -le 3 -or -not [IO.Directory]::Exists($rootPath) -or
    ([IO.File]::GetAttributes($rootPath) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'Corpus root is absent, a volume root or a reparse point.'
}
$run = $RunId.ToString('N')
$subtree = [IO.Path]::Combine($rootPath,'PeerSync-GateCorpus',$run)
$start = [DateTimeOffset]::UtcNow
$files = 0L
$directories = 0L
$bytes = 0L
$markerBytes = 0L
$markerSha256 = $null
$owner = $null
$status = 'failed'
$issue = $null
try {
    $gateRoot = [IO.Path]::Combine($rootPath,'PeerSync-GateCorpus')
    if (-not [IO.Directory]::Exists($gateRoot) -or
        -not [IO.Directory]::Exists($subtree)) { throw 'Run subtree is absent.' }
    foreach ($directory in @($gateRoot,$subtree)) {
        if (([IO.File]::GetAttributes($directory) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Run subtree contains a reparse point.'
        }
    }
    $marker = [IO.Path]::Combine($subtree,'owner.json')
    if (-not [IO.File]::Exists($marker)) { throw 'Run ownership marker is absent.' }
    $owner = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
    if ($owner.schema -ne 'peersync-gate-corpus-owner-v1' -or
        $owner.runId -ne $run -or
        $owner.generatorSha256 -notmatch '^[0-9a-fA-F]{64}$') {
        throw 'Run ownership marker has the wrong identity.'
    }
    $markerBytes = ([IO.FileInfo]::new($marker)).Length
    $markerSha256 = (Get-FileHash -LiteralPath $marker -Algorithm SHA256).Hash
    if ($rootPath -match '^[A-Za-z]:\\') {
        $scanRoot = '\\?\' + $subtree
    } elseif ($rootPath.StartsWith('\\',[StringComparison]::Ordinal)) {
        $scanRoot = '\\?\UNC\' + $subtree.Substring(2)
    } else {
        throw 'Corpus root must be an absolute Windows path.'
    }
    $queue = [Collections.Generic.Queue[string]]::new()
    $queue.Enqueue($scanRoot)
    while ($queue.Count -gt 0) {
        foreach ($path in [IO.Directory]::EnumerateFileSystemEntries($queue.Dequeue())) {
            $attributes = [IO.File]::GetAttributes($path)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Run subtree contains a reparse point.'
            }
            if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                $directories++
                $queue.Enqueue($path)
            } else {
                $files++
                $bytes += ([IO.FileInfo]::new($path)).Length
            }
            if ($files -gt 60000 -or $bytes -gt 10GB) {
                throw 'Run subtree exceeds its bounded file/content limit.'
            }
        }
    }
    if ($files -lt 1 -or $bytes -lt $markerBytes) {
        throw 'Run subtree count is invalid.'
    }
    $status = 'complete'
} catch {
    $issue = $_.Exception.GetBaseException().Message.Replace($rootPath,'[ROOT]')
}
$report = [ordered]@{
    schema='peersync-corpus-run-measure-v1'
    status=$status
    runId=$run
    machineLabel=$MachineLabel
    computer=$env:COMPUTERNAME
    startedUtc=$start.ToString('o')
    endedUtc=[DateTimeOffset]::UtcNow.ToString('o')
    files=$files
    directories=$directories
    bytes=$bytes
    markerBytes=$markerBytes
    markerSha256=$markerSha256
    ownerGeneratorSha256=if ($owner) { $owner.generatorSha256 } else { $null }
    collectorSha256=(Get-FileHash -LiteralPath $MyInvocation.MyCommand.Path -Algorithm SHA256).Hash
    issue=$issue
}
$temporary = $output + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
try {
    $report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $temporary -Encoding UTF8
    [IO.File]::Move($temporary,$output)
} finally {
    if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
}
Write-Output $output
if ($status -ne 'complete') { exit 1 }
