[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$QueueDirectory,
    [ValidateRange(1, 120)][int]$WaitHours = 60,
    [switch]$PreflightOnly
)
$ErrorActionPreference = 'Stop'
$queue = (Resolve-Path -LiteralPath $QueueDirectory).Path
$prepared = Get-Content -LiteralPath (Join-Path $queue 'prepared.json') -Raw | ConvertFrom-Json
$previousQueue = [string]$prepared.prerequisiteQueue
$previousTerminal = Join-Path $previousQueue 'terminal.json'
$previousRunning = Join-Path $previousQueue 'running.json'
$terminal = Join-Path $queue 'terminal.json'
$exe = Join-Path $queue 'bin\PeerSync.Benchmarks.exe'
$fixture = [string]$prepared.fixture
$minimumStartFree = 6GB
$minimumContinueFree = 1536MB
$started = $null
$child = $null
$previousStatus = $null

function Assert-Payload {
    $bin = Join-Path $queue 'bin'
    $expected = @($prepared.payloadFiles)
    if ($expected.Count -eq 0) { throw 'Missing immutable payload manifest.' }
    $actual = @(Get-ChildItem -LiteralPath $bin -File -Recurse | ForEach-Object {
        $_.FullName.Substring($bin.Length + 1).Replace('\', '/')
    })
    $difference = @(Compare-Object -ReferenceObject @($expected.relativePath) -DifferenceObject $actual)
    if ($difference.Count -ne 0) { throw 'Benchmark payload file set changed.' }
    foreach ($file in $expected) {
        $path = Join-Path $bin ([string]$file.relativePath).Replace('/', '\')
        if ((Get-Item -LiteralPath $path).Length -ne [long]$file.length -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne [string]$file.sha256) {
            throw "Benchmark payload hash changed: $($file.relativePath)"
        }
    }
    if ((Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash -ne [string]$prepared.exeSha256) {
        throw 'Benchmark executable hash changed.'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $fixture 'tree') -PathType Container)) {
        throw 'Retained million-file fixture tree is missing.'
    }
}

function Write-Terminal([string]$status, [string]$reason, $details) {
    [ordered]@{
        status = $status
        reason = $reason
        preparedRevision = $prepared.sourceRevision
        prerequisiteStatus = $previousStatus
        startedUtc = if ($started) { $started.ToString('O') } else { $null }
        endedUtc = [DateTime]::UtcNow.ToString('O')
        details = $details
    } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $terminal -Encoding utf8
}

if ($PreflightOnly) {
    Assert-Payload
    "PREFLIGHT_OK files=$(@($prepared.payloadFiles).Count) revision=$($prepared.sourceRevision)"
    exit 0
}
if (Test-Path -LiteralPath $terminal -PathType Leaf) { exit 0 }

Add-Type -Namespace PeerSyncField -Name QueuedScaleSleepGuard -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("kernel32.dll")]
public static extern uint SetThreadExecutionState(uint flags);
'@
$continuous = [uint32]2147483648
$systemRequired = [uint32]1
$waitDeadline = [DateTime]::UtcNow.AddHours($WaitHours)
try {
    Assert-Payload
    $previous = [PeerSyncField.QueuedScaleSleepGuard]::SetThreadExecutionState(
        [uint32]($continuous -bor $systemRequired))
    if ($previous -eq 0) { throw 'Windows rejected the system-awake request.' }

    while (-not (Test-Path -LiteralPath $previousTerminal -PathType Leaf)) {
        if ([DateTime]::UtcNow -ge $waitDeadline) {
            Write-Terminal 'blocked' 'prerequisite-terminal-timeout' $null
            exit 1
        }
        $prior = if (Test-Path -LiteralPath $previousRunning -PathType Leaf) {
            Get-Content -LiteralPath $previousRunning -Raw | ConvertFrom-Json
        } else { $null }
        $priorPid = if ($prior) { [int]$prior.benchmarkPid } else { 0 }
        $priorProcess = if ($priorPid -gt 0) {
            Get-CimInstance Win32_Process -Filter "ProcessId=$priorPid" -ErrorAction SilentlyContinue
        } else { $null }
        [ordered]@{
            status = 'waiting-for-prerequisite'
            observedUtc = [DateTime]::UtcNow.ToString('O')
            deadlineUtc = $waitDeadline.ToString('O')
            prerequisitePid = $priorPid
            prerequisiteProcessLive = $null -ne $priorProcess
        } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $queue 'waiting.json') -Encoding utf8
        if ($priorPid -gt 0 -and -not $priorProcess) {
            [System.Threading.Thread]::Sleep(300000)
            $priorProcess = Get-CimInstance Win32_Process -Filter "ProcessId=$priorPid" -ErrorAction SilentlyContinue
            if (-not $priorProcess -and -not (Test-Path -LiteralPath $previousTerminal -PathType Leaf)) {
                Write-Terminal 'blocked' 'prerequisite-ended-without-terminal' @{ prerequisitePid = $priorPid }
                exit 1
            }
            if (Test-Path -LiteralPath $previousTerminal -PathType Leaf) { break }
        }
        [System.Threading.Thread]::Sleep(60000)
    }
    $previousStatus = [string](Get-Content -LiteralPath $previousTerminal -Raw | ConvertFrom-Json).status
    # A failed older candidate may still be superseded by this repaired source.
    # Only the prerequisite's terminal state and process exit are required.
    $prior = Get-Content -LiteralPath $previousRunning -Raw | ConvertFrom-Json
    $priorProcess = Get-CimInstance Win32_Process -Filter "ProcessId=$([int]$prior.benchmarkPid)" -ErrorAction SilentlyContinue
    if ($priorProcess -and $priorProcess.ExecutablePath -eq (Join-Path $previousQueue 'bin\PeerSync.Benchmarks.exe')) {
        throw 'Prerequisite terminal exists while its benchmark process remains live.'
    }
    Assert-Payload

    $diskDeadline = [DateTime]::UtcNow.AddHours(6)
    $freeBefore = (Get-PSDrive C).Free
    while ($freeBefore -lt $minimumStartFree -and [DateTime]::UtcNow -lt $diskDeadline) {
        [ordered]@{ status = 'waiting-for-disk'; observedUtc = [DateTime]::UtcNow.ToString('O');
            freeBytes = $freeBefore; requiredBytes = $minimumStartFree;
            deadlineUtc = $diskDeadline.ToString('O') } | ConvertTo-Json |
            Set-Content -LiteralPath (Join-Path $queue 'disk-wait.json') -Encoding utf8
        [System.Threading.Thread]::Sleep(300000)
        $freeBefore = (Get-PSDrive C).Free
    }
    if ($freeBefore -lt $minimumStartFree) {
        Write-Terminal 'blocked' 'insufficient-start-disk' @{
            freeBytes = $freeBefore; requiredBytes = $minimumStartFree }
        exit 1
    }

    $started = [DateTime]::UtcNow
    $child = Start-Process -FilePath $exe -ArgumentList @('--scan-peer-existing', $fixture, '--files',
        [string]$prepared.requestedFiles) -WorkingDirectory $queue -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $queue 'stdout.log') `
        -RedirectStandardError (Join-Path $queue 'stderr.log')
    [ordered]@{ status = 'running'; startedUtc = $started.ToString('O');
        benchmarkPid = $child.Id; sourceRevision = $prepared.sourceRevision;
        prerequisiteStatus = $previousStatus; exeSha256 = $prepared.exeSha256;
        freeBytes = $freeBefore; minimumContinueFreeBytes = $minimumContinueFree } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $queue 'running.json') -Encoding utf8

    $diskAbort = $false
    while (-not $child.WaitForExit(60000)) {
        if ((Get-PSDrive C).Free -lt $minimumContinueFree) {
            $diskAbort = $true
            Stop-Process -Id $child.Id -Force -ErrorAction SilentlyContinue
            $child.WaitForExit()
            break
        }
    }
    $rows = @(Get-Content -LiteralPath (Join-Path $queue 'stdout.log') |
        Where-Object { $_ -match '^\{"target":' } |
        ForEach-Object { try { $_ | ConvertFrom-Json } catch { $null } } |
        Where-Object { $null -ne $_ })
    $scanner = $rows | Where-Object { $_.target -in @('Scanner', 'P5 million-file scanner') } |
        Select-Object -Last 1
    $peer = $rows | Where-Object { $_.target -eq 'P5 million-file fresh peer' -and $null -ne $_.correct } |
        Select-Object -Last 1
    $stderrBytes = (Get-Item -LiteralPath (Join-Path $queue 'stderr.log')).Length
    $passed = -not $diskAbort -and $child.ExitCode -eq 0 -and $scanner -and
        $scanner.correct -and $scanner.targetMet -and $peer -and $peer.correct -and
        $peer.verifiedFiles -eq [int]$prepared.requestedFiles -and $stderrBytes -eq 0
    Write-Terminal $(if ($passed) { 'passed' } else { 'failed' }) `
        $(if ($diskAbort) { 'low-disk-abort' } elseif ($passed) { 'targets-met' }
            else { 'benchmark-failed-or-target-missed' }) `
        @{ exitCode = $child.ExitCode; scanner = $scanner; peer = $peer;
            stderrBytes = $stderrBytes; freeBytes = (Get-PSDrive C).Free }
    if (-not $passed) { exit 1 }
    exit 0
} catch {
    Write-Terminal 'failed' $_.Exception.Message @{ benchmarkPid = if ($child) { $child.Id } else { $null } }
    exit 1
} finally {
    [void][PeerSyncField.QueuedScaleSleepGuard]::SetThreadExecutionState($continuous)
}
