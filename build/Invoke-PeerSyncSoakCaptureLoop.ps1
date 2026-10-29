[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Root,
    [Parameter(Mandatory)][guid]$RunId,
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9_-]{1,32}$')][string]$MachineLabel,
    [Parameter(Mandatory)]
    [ValidatePattern('^\d{4}-\d{2}-\d{2}T\d{2}:00:00Z$')][string]$FirstHourUtc,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateRange(0, 3)][int]$Intervals = 3
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\')
$output = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
if ($rootPath.Length -le 3 -or -not [IO.Directory]::Exists($rootPath)) {
    throw 'Root must be an existing synchronized folder below a volume root.'
}
if (-not [IO.Directory]::Exists($output) -or
    $output.StartsWith($rootPath + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $output.Equals($rootPath, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory must exist outside the synchronized folder.'
}
$first = [DateTime]::ParseExact($FirstHourUtc, 'yyyy-MM-ddTHH:mm:ssZ',
    [Globalization.CultureInfo]::InvariantCulture,
    [Globalization.DateTimeStyles]::AssumeUniversal).ToUniversalTime()
if ([DateTime]::UtcNow -ge $first.AddHours(1)) {
    throw 'FirstHourUtc has already ended; select a future UTC hour.'
}
$captureScript = [IO.Path]::Combine($PSScriptRoot, 'Measure-PeerSyncTree.ps1')
if (-not [IO.File]::Exists($captureScript)) { throw 'The packaged tree tool is missing.' }
$captureHash = (Get-FileHash -LiteralPath $captureScript -Algorithm SHA256).Hash
$run = $RunId.ToString('N')
$journalPath = [IO.Path]::Combine($output,
    "capture-loop-$run-$MachineLabel-$([Guid]::NewGuid().ToString('N')).jsonl")
$stream = [IO.File]::Open($journalPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
$writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
$writer.AutoFlush = $true
$completed = 0
$failure = $null

function Write-Record([object]$record) {
    $writer.WriteLine(($record | ConvertTo-Json -Compress -Depth 5))
}

try {
    Write-Record ([ordered]@{ schema = 'peersync-soak-capture-loop-v1'; kind = 'start';
        runId = $run; machineLabel = $MachineLabel;
        firstHourUtc = $first.ToString('yyyy-MM-ddTHH:mm:ssZ');
        intervals = $Intervals; toolSha256 = $captureHash;
        utc = [DateTimeOffset]::UtcNow.ToString('O') })
    for ($index = 0; $index -le $Intervals; $index++) {
        $hour = $first.AddHours($index)
        while ([DateTime]::UtcNow -lt $hour) {
            $remaining = ($hour - [DateTime]::UtcNow).TotalSeconds
            Start-Sleep -Seconds ([Math]::Max(1, [Math]::Min(30, [Math]::Ceiling($remaining))))
        }
        $hourText = $hour.ToString('yyyy-MM-ddTHH:mm:ssZ')
        $snapshotName = "snapshot-$run-$MachineLabel-$($hour.ToString('yyyyMMddTHH')).json"
        $snapshot = [IO.Path]::Combine($output, $snapshotName)
        $issue = $null
        if ([DateTime]::UtcNow -ge $hour.AddHours(1)) { $issue = 'missed-hour' }
        elseif ([IO.File]::Exists($snapshot)) { $issue = 'output-exists' }
        elseif ((Get-FileHash -LiteralPath $captureScript -Algorithm SHA256).Hash -ne $captureHash) {
            $issue = 'tool-changed'
        }
        if ($issue) {
            $failure = $issue
            Write-Record ([ordered]@{ schema = 'peersync-soak-capture-loop-v1'; kind = 'hour';
                runId = $run; machineLabel = $MachineLabel; hourUtc = $hourText;
                utc = [DateTimeOffset]::UtcNow.ToString('O');
                status = 'failed'; issue = $issue; snapshot = $snapshotName })
            break
        }
        try {
            & $captureScript -Root $rootPath -RunId $RunId -MachineLabel $MachineLabel `
                -HourUtc $hourText -OutputFile $snapshot | Out-Null
            if ($LASTEXITCODE -ne 0) { throw [IO.IOException]::new('Tree capture failed.') }
            $result = Get-Content -LiteralPath $snapshot -Raw | ConvertFrom-Json
            if ($result.status -ne 'complete') { throw [IO.IOException]::new('Tree report is incomplete.') }
            $completed++
            Write-Record ([ordered]@{ schema = 'peersync-soak-capture-loop-v1'; kind = 'hour';
                runId = $run; machineLabel = $MachineLabel; hourUtc = $hourText;
                utc = [DateTimeOffset]::UtcNow.ToString('O');
                status = 'complete'; snapshot = $snapshotName;
                treeSha256 = $result.treeSha256; files = $result.files;
                directories = $result.directories; bytes = $result.bytes })
        } catch {
            $failure = 'capture-failed'
            Write-Record ([ordered]@{ schema = 'peersync-soak-capture-loop-v1'; kind = 'hour';
                runId = $run; machineLabel = $MachineLabel; hourUtc = $hourText;
                utc = [DateTimeOffset]::UtcNow.ToString('O');
                status = 'failed'; issue = $failure;
                errorType = $_.Exception.GetType().Name; snapshot = $snapshotName })
            break
        }
    }
    Write-Record ([ordered]@{ schema = 'peersync-soak-capture-loop-v1'; kind = 'end';
        runId = $run; machineLabel = $MachineLabel;
        utc = [DateTimeOffset]::UtcNow.ToString('O'); completed = $completed;
        requested = $Intervals + 1; issue = $failure })
} finally { $writer.Dispose() }

[pscustomobject]@{ runId = $run; machineLabel = $MachineLabel;
    completed = $completed; requested = $Intervals + 1;
    issue = $failure; journalPath = $journalPath }
if ($failure) { exit 1 }
exit 0
