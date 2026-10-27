[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Root,
    [Parameter(Mandatory)][guid]$RunId,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_-]{1,32}$')][string]$MachineLabel,
    [Parameter(Mandatory)][ValidatePattern('^\d{4}-\d{2}-\d{2}T\d{2}:00:00Z$')][string]$FirstHourUtc,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateRange(1, 20)][int]$OperationsPerBatch = 5,
    [ValidateRange(60, 900)][int]$BatchIntervalSeconds = 300,
    [switch]$PreflightOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\')
$output = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
if ($rootPath.Length -le 3 -or -not [IO.Directory]::Exists($rootPath)) {
    throw 'Root must be an existing synchronized folder below a volume root.'
}
if (-not [IO.Directory]::Exists($output) -or
    $output.Equals($rootPath, [StringComparison]::OrdinalIgnoreCase) -or
    $output.StartsWith($rootPath + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory must exist outside the synchronized folder.'
}
foreach ($path in @($rootPath, $output)) {
    if (([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Root and output directory cannot be reparse points.'
    }
}
$first = [DateTime]::ParseExact($FirstHourUtc, 'yyyy-MM-ddTHH:mm:ssZ',
    [Globalization.CultureInfo]::InvariantCulture,
    [Globalization.DateTimeStyles]::AssumeUniversal).ToUniversalTime()
if ($first -le [DateTime]::UtcNow.AddMinutes(2)) {
    throw 'FirstHourUtc must be at least two minutes in the future.'
}
$end = $first.AddHours(3)
$run = $RunId.ToString('N')

$captureScript = Join-Path $PSScriptRoot 'Invoke-PeerSyncSoakCaptureLoop.ps1'
$operationScript = Join-Path $PSScriptRoot 'Invoke-PeerSyncSoakOperations.ps1'
$treeScript = Join-Path $PSScriptRoot 'Measure-PeerSyncTree.ps1'
$budgetScript = Join-Path $PSScriptRoot 'Get-PeerSyncTestTreeContentBytes.ps1'
$hashes = [ordered]@{}
foreach ($path in @($captureScript, $operationScript, $treeScript, $budgetScript)) {
    if (-not [IO.File]::Exists($path)) { throw "Missing packaged soak tool: $([IO.Path]::GetFileName($path))" }
    $hashes[[IO.Path]::GetFileName($path)] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
}
. $budgetScript
$initialContentBytes = Get-PeerSyncTestTreeContentBytes -Root $rootPath -MaximumBytes 10GB
if ($first -le [DateTime]::UtcNow.AddMinutes(1)) {
    throw 'FirstHourUtc is too close after tree preflight.'
}
if ($PreflightOnly) {
    [pscustomobject]@{ runId=$run; machineLabel=$MachineLabel; status='ready';
        firstHourUtc=$FirstHourUtc; initialContentBytes=$initialContentBytes;
        tools=$hashes }
    return
}
$existing = @(Get-ChildItem -LiteralPath $output -Filter "soak-launcher-$run-$MachineLabel-*.jsonl" -File -ErrorAction Stop)
if ($existing.Count -ne 0) { throw 'This run and machine already have a soak launcher journal.' }

$journal = Join-Path $output "soak-launcher-$run-$MachineLabel-$([guid]::NewGuid().ToString('N')).jsonl"
$stream = [IO.File]::Open($journal, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
$writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
$writer.AutoFlush = $true
$captureProcess = $null
$operationProcess = $null
$batches = 0
$status = 'failed'
$issue = $null
$powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'

function Write-Record([object]$record) {
    $writer.WriteLine(($record | ConvertTo-Json -Compress -Depth 6))
}
function Quote([string]$value) { return "'" + $value.Replace("'", "''") + "'" }
function Encode-Command([string]$command) {
    return [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
}
function Require-ToolsUnchanged {
    foreach ($name in $hashes.Keys) {
        $actual = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $name) -Algorithm SHA256).Hash
        if ($actual -ne $hashes[$name]) { throw "Packaged soak tool changed: $name" }
    }
}

try {
    Write-Record ([ordered]@{ schema='peersync-physical-soak-launcher-v1'; kind='start';
        utc=[DateTimeOffset]::UtcNow.ToString('O'); runId=$run; machineLabel=$MachineLabel;
        firstHourUtc=$FirstHourUtc; endUtc=$end.ToString('yyyy-MM-ddTHH:mm:ssZ');
        operationsPerBatch=$OperationsPerBatch; batchIntervalSeconds=$BatchIntervalSeconds;
        initialContentBytes=$initialContentBytes;
        launcherSha256=(Get-FileHash -LiteralPath $MyInvocation.MyCommand.Path -Algorithm SHA256).Hash;
        tools=$hashes })

    $captureCommand = '& ' + (Quote $captureScript) + ' -Root ' + (Quote $rootPath) +
        ' -RunId ' + (Quote $RunId.ToString()) + ' -MachineLabel ' + (Quote $MachineLabel) +
        ' -FirstHourUtc ' + (Quote $FirstHourUtc) + ' -OutputDirectory ' + (Quote $output) +
        ' -Intervals 3'
    $captureOut = Join-Path $output "soak-capture-$run-$MachineLabel-stdout.txt"
    $captureErr = Join-Path $output "soak-capture-$run-$MachineLabel-stderr.txt"
    if ([IO.File]::Exists($captureOut) -or [IO.File]::Exists($captureErr)) {
        throw 'Capture process output already exists.'
    }
    $captureProcess = Start-Process -FilePath $powershell -ArgumentList @(
        '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
        '-EncodedCommand', (Encode-Command $captureCommand)) -WindowStyle Hidden `
        -RedirectStandardOutput $captureOut -RedirectStandardError $captureErr -PassThru
    Write-Record ([ordered]@{ schema='peersync-physical-soak-launcher-v1'; kind='capture-start';
        utc=[DateTimeOffset]::UtcNow.ToString('O'); processId=$captureProcess.Id })

    $nextBatch = $first.AddMinutes(20)
    while ([DateTime]::UtcNow -lt $end) {
        $now = [DateTime]::UtcNow
        $captureProcess.Refresh()
        if ($captureProcess.HasExited) {
            throw "Capture loop ended before the three-hour window; exit $($captureProcess.ExitCode)."
        }
        if ($now -lt $nextBatch) {
            Start-Sleep -Seconds ([Math]::Max(1, [Math]::Min(30, [Math]::Ceiling(($nextBatch - $now).TotalSeconds))))
            continue
        }
        $hour = $now.Minute
        if ($hour -lt 20 -or $hour -ge 45) {
            $candidate = [DateTime]::new($now.Year, $now.Month, $now.Day, $now.Hour, 20, 0, [DateTimeKind]::Utc)
            if ($hour -ge 45) { $candidate = $candidate.AddHours(1) }
            $nextBatch = $candidate
            continue
        }
        Require-ToolsUnchanged
        $batches++
        $operationCommand = '& ' + (Quote $operationScript) + ' -Root ' + (Quote $rootPath) +
            ' -RunId ' + (Quote $RunId.ToString()) + ' -MachineLabel ' + (Quote $MachineLabel) +
            ' -LogDirectory ' + (Quote $output) + ' -Count ' + $OperationsPerBatch
        $operationOut = Join-Path $output ("soak-operations-$run-$MachineLabel-{0:D3}-stdout.txt" -f $batches)
        $operationErr = Join-Path $output ("soak-operations-$run-$MachineLabel-{0:D3}-stderr.txt" -f $batches)
        $operationProcess = Start-Process -FilePath $powershell -ArgumentList @(
            '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
            '-EncodedCommand', (Encode-Command $operationCommand)) -WindowStyle Hidden `
            -RedirectStandardOutput $operationOut -RedirectStandardError $operationErr -PassThru
        $batchDeadline = [DateTime]::UtcNow.AddMinutes(15)
        if ($batchDeadline -gt $end) { $batchDeadline = $end }
        while ($true) {
            $operationProcess.Refresh()
            if ($operationProcess.HasExited) { break }
            if ([DateTime]::UtcNow -ge $batchDeadline) {
                throw "Operation batch $batches exceeded its bounded execution window."
            }
            Start-Sleep -Seconds 2
        }
        Write-Record ([ordered]@{ schema='peersync-physical-soak-launcher-v1'; kind='operation-batch';
            utc=[DateTimeOffset]::UtcNow.ToString('O'); batch=$batches;
            exitCode=$operationProcess.ExitCode; stdout=$operationOut; stderr=$operationErr })
        if ($operationProcess.ExitCode -ne 0) { throw "Operation batch $batches failed." }
        $operationProcess.Dispose()
        $operationProcess = $null
        if ([DateTime]::UtcNow -gt $end) { throw 'An operation batch exceeded the three-hour active window.' }
        $nextBatch = [DateTime]::UtcNow.AddSeconds($BatchIntervalSeconds)
    }

    $captureDeadline = $end.AddHours(1)
    while ($true) {
        $captureProcess.Refresh()
        if ($captureProcess.HasExited) { break }
        if ([DateTime]::UtcNow -ge $captureDeadline) { throw 'Final read-only capture exceeded its one-hour completion allowance.' }
        Start-Sleep -Seconds 15
    }
    if ($captureProcess.ExitCode -ne 0) { throw "Capture loop exited $($captureProcess.ExitCode)." }
    $status = 'complete'
} catch {
    $issue = $_.Exception.Message
    Write-Record ([ordered]@{ schema='peersync-physical-soak-launcher-v1'; kind='failure';
        utc=[DateTimeOffset]::UtcNow.ToString('O'); errorType=$_.Exception.GetType().Name;
        hresult=('0x{0:X8}' -f ($_.Exception.HResult -band 0xFFFFFFFF)); issue=$issue })
} finally {
    if ($operationProcess) {
        $operationProcess.Refresh()
        if (-not $operationProcess.HasExited) {
            Stop-Process -Id $operationProcess.Id -Force -ErrorAction SilentlyContinue
        }
        $operationProcess.Dispose()
    }
    if ($captureProcess) {
        $captureProcess.Refresh()
        if (-not $captureProcess.HasExited) {
            Stop-Process -Id $captureProcess.Id -Force -ErrorAction SilentlyContinue
        }
        $captureProcess.Dispose()
    }
    Write-Record ([ordered]@{ schema='peersync-physical-soak-launcher-v1'; kind='end';
        utc=[DateTimeOffset]::UtcNow.ToString('O'); status=$status;
        batches=$batches; issue=$issue })
    $writer.Dispose()
}

[pscustomobject]@{ runId=$run; machineLabel=$MachineLabel; status=$status;
    batches=$batches; issue=$issue; journal=$journal }
if ($status -ne 'complete') { exit 1 }
exit 0
