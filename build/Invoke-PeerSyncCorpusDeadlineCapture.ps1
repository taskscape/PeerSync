[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Root,
    [Parameter(Mandatory)][guid]$RunId,
    [Parameter(Mandatory)][ValidateSet('first','second','third')][string]$MachineLabel,
    [Parameter(Mandatory)][string]$SourceResumeUtc,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Quote-PS([string]$value) {
    return "'" + $value.Replace("'", "''") + "'"
}

$run = $RunId.ToString('N')
$rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\')
$outputPath = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
if (-not [IO.Directory]::Exists($rootPath) -or
    ([IO.File]::GetAttributes($rootPath) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'The synchronized root is missing or is a reparse point.'
}
if (-not [IO.Directory]::Exists($outputPath) -or
    $outputPath.StartsWith($rootPath + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $outputPath.Equals($rootPath, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The evidence directory must exist outside the synchronized root.'
}
$resume = [DateTimeOffset]::Parse($SourceResumeUtc,
    [Globalization.CultureInfo]::InvariantCulture,
    [Globalization.DateTimeStyles]::AssumeUniversal).ToUniversalTime()
$deadline = $resume.AddHours(3)
$statusFile = [IO.Path]::Combine($outputPath, "capture-$run-$MachineLabel.json")
$runFile = [IO.Path]::Combine($outputPath, "run-$run-$MachineLabel.json")
$treeFile = [IO.Path]::Combine($outputPath, "tree-$run-$MachineLabel.json")
foreach ($path in @($statusFile,$runFile,$treeFile)) {
    if ([IO.File]::Exists($path) -or [IO.Directory]::Exists($path)) {
        throw 'Capture evidence already exists; use a new output directory.'
    }
}

$status = [ordered]@{
    schema='peersync-corpus-deadline-capture-v1'
    status='failed'
    runId=$run
    machineLabel=$MachineLabel
    computer=$env:COMPUTERNAME
    sourceResumeUtc=$resume.ToString('o')
    deadlineUtc=$deadline.ToString('o')
    startedUtc=[DateTimeOffset]::UtcNow.ToString('o')
    endedUtc=$null
    runReport=$runFile
    treeSnapshot=$treeFile
    hourUtc=$null
    runCollectorSha256=$null
    treeCollectorSha256=$null
    issue=$null
}

function Invoke-Collector([string]$stage,[string]$tool,[string]$arguments) {
    if ([DateTimeOffset]::UtcNow -ge $deadline) {
        throw "$stage cannot start after the three-hour deadline."
    }
    $stdout = [IO.Path]::Combine($outputPath,"$stage-$run-$MachineLabel.stdout.txt")
    $stderr = [IO.Path]::Combine($outputPath,"$stage-$run-$MachineLabel.stderr.txt")
    if ([IO.File]::Exists($stdout) -or [IO.File]::Exists($stderr)) {
        throw "$stage output already exists."
    }
    $command = '& ' + (Quote-PS $tool) + ' ' + $arguments +
        ' 1> ' + (Quote-PS $stdout) + ' 2> ' + (Quote-PS $stderr)
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
    $start.Arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ' + $encoded
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $process = [Diagnostics.Process]::Start($start)
    if ($null -eq $process) { throw "$stage process did not start." }
    try {
        $milliseconds = [int][Math]::Max(1,
            [Math]::Min([int]::MaxValue,($deadline - [DateTimeOffset]::UtcNow).TotalMilliseconds))
        if (-not $process.WaitForExit($milliseconds)) {
            $process.Kill()
            $process.WaitForExit()
            throw "$stage exceeded the three-hour deadline."
        }
        if ($process.ExitCode -ne 0) {
            throw "$stage collector exited $($process.ExitCode); inspect $stderr."
        }
    } finally {
        $process.Dispose()
    }
}

try {
    if ([DateTimeOffset]::UtcNow -lt $resume -or [DateTimeOffset]::UtcNow -ge $deadline) {
        throw 'Capture launch is outside the source-resume three-hour window.'
    }
    $runTool = [IO.Path]::Combine($PSScriptRoot,'Measure-PeerSyncCorpusRun.ps1')
    $treeTool = [IO.Path]::Combine($PSScriptRoot,'Measure-PeerSyncTree.ps1')
    if (-not [IO.File]::Exists($runTool) -or -not [IO.File]::Exists($treeTool)) {
        throw 'A required collector is missing beside this launcher.'
    }
    $status.runCollectorSha256 = (Get-FileHash -LiteralPath $runTool -Algorithm SHA256).Hash
    $status.treeCollectorSha256 = (Get-FileHash -LiteralPath $treeTool -Algorithm SHA256).Hash
    $common = '-Root ' + (Quote-PS $rootPath) + ' -RunId ' + (Quote-PS $RunId.ToString()) +
        ' -MachineLabel ' + (Quote-PS $MachineLabel)
    Invoke-Collector 'run' $runTool ($common + ' -OutputFile ' + (Quote-PS $runFile))
    $runReport = Get-Content -LiteralPath $runFile -Raw | ConvertFrom-Json
    if ($runReport.status -ne 'complete' -or $runReport.runId -ne $run -or
        $runReport.machineLabel -ne $MachineLabel -or
        $runReport.collectorSha256 -ne $status.runCollectorSha256) {
        throw 'Run collector report is incomplete or has the wrong identity.'
    }
    $status.hourUtc = [DateTimeOffset]::UtcNow.ToString('yyyy-MM-ddTHH:00:00Z',
        [Globalization.CultureInfo]::InvariantCulture)
    Invoke-Collector 'tree' $treeTool ($common + ' -HourUtc ' +
        (Quote-PS $status.hourUtc) + ' -OutputFile ' + (Quote-PS $treeFile))
    $treeReport = Get-Content -LiteralPath $treeFile -Raw | ConvertFrom-Json
    if ($treeReport.status -ne 'complete' -or $treeReport.runId -ne $run -or
        $treeReport.machineLabel -ne $MachineLabel -or
        $treeReport.collectorSha256 -ne $status.treeCollectorSha256) {
        throw 'Tree collector report is incomplete or has the wrong identity.'
    }
    if ([DateTimeOffset]::UtcNow -gt $deadline) {
        throw 'Capture finished after the three-hour deadline.'
    }
    $status.status = 'complete'
} catch {
    $status.issue = $_.Exception.GetBaseException().Message.Replace($rootPath,'[ROOT]')
} finally {
    $status.endedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    $temporary = $statusFile + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        $status | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $temporary -Encoding UTF8
        [IO.File]::Move($temporary,$statusFile)
    } finally {
        if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
}
Write-Output $statusFile
if ($status.status -ne 'complete') { exit 1 }
