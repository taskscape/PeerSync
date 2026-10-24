$ErrorActionPreference = 'Continue'
$outputPath = Join-Path $PSScriptRoot ('NativeWAN-failure-{0}.txt' -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
$output = [Collections.Generic.List[string]]::new()
function Record([string]$line) {
    [void]$output.Add($line)
    Write-Output $line
}

Record ('UTC ' + (Get-Date).ToUniversalTime().ToString('o'))
$report = Get-ChildItem -LiteralPath $PSScriptRoot -Filter 'NativeWAN-result-*.json' -File -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if ($report) {
    Record ('REPORT_FILE ' + $report.FullName)
    foreach ($line in Get-Content -LiteralPath $report.FullName) { Record $line }
} else { Record 'REPORT_FILE missing' }

$rules = @(Get-NetFirewallRule -Name 'PeerSync-NativeWAN-*' -ErrorAction SilentlyContinue)
Record ('TEST_FIREWALL_RULES ' + $rules.Count)
$before = Get-CimInstance Win32_Service -Filter "Name='PeerSync'" -ErrorAction SilentlyContinue
if ($null -eq $before) {
    Record 'SERVICE missing'
} else {
    Record ('SERVICE_BEFORE state={0} processId={1} exitCode={2} serviceSpecificExitCode={3}' -f
        $before.State, $before.ProcessId, $before.ExitCode, $before.ServiceSpecificExitCode)
    if ($before.State -ne 'Running' -and $rules.Count -eq 0) {
        try {
            Start-Service -Name PeerSync -ErrorAction Stop
            (Get-Service -Name PeerSync).WaitForStatus(
                [System.ServiceProcess.ServiceControllerStatus]::Running,
                [TimeSpan]::FromSeconds(45))
            Record 'START_ATTEMPT succeeded'
        } catch { Record ('START_ATTEMPT failed: ' + $_.Exception.Message) }
    } elseif ($rules.Count -ne 0) {
        Record 'START_ATTEMPT skipped while native WAN test firewall rules remain'
    }
    $after = Get-CimInstance Win32_Service -Filter "Name='PeerSync'" -ErrorAction SilentlyContinue
    if ($after) {
        Record ('SERVICE_AFTER state={0} processId={1} exitCode={2} serviceSpecificExitCode={3}' -f
            $after.State, $after.ProcessId, $after.ExitCode, $after.ServiceSpecificExitCode)
    }
}

$since = (Get-Date).AddHours(-2)
foreach ($logName in @('System', 'Application')) {
    Record ("EVENT_LOG $logName")
    try {
        $events = @(Get-WinEvent -FilterHashtable @{LogName=$logName; StartTime=$since} -MaxEvents 1000 -ErrorAction Stop |
            Where-Object { $_.Message -match '(?i)PeerSync' } | Select-Object -First 12)
        foreach ($event in $events) {
            Record ('EVENT {0:o} id={1} provider={2} message={3}' -f
                $event.TimeCreated.ToUniversalTime(), $event.Id, $event.ProviderName,
                (($event.Message -replace '[\r\n]+', ' ') -replace '\s{2,}', ' '))
        }
        if ($events.Count -eq 0) { Record 'EVENT none mentioning PeerSync' }
    } catch { Record ('EVENT_READ failed: ' + $_.Exception.Message) }
}

Record 'SERVICE_STARTUP_JOURNAL'
$logRoot = Join-Path $env:ProgramData 'PeerSync\logs\service'
$runs = @(Get-ChildItem -LiteralPath $logRoot -Directory -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 3)
foreach ($run in $runs) {
    $journal = Join-Path $run.FullName 'debug.jsonl'
    if (-not (Test-Path -LiteralPath $journal -PathType Leaf)) { continue }
    Record ('JOURNAL ' + $journal)
    $patterns = @('service.starting', 'service.start_failed', 'service.settings_unavailable',
        'service.folder_engine_failed', 'service.folder_host_ready',
        'service.quic_listener_ready', 'service.control_ready')
    $matches = @(Select-String -LiteralPath $journal -Pattern $patterns -ErrorAction SilentlyContinue |
        Select-Object -Last 25)
    foreach ($match in $matches) { Record $match.Line }
    if ($matches.Count -eq 0) { Record 'JOURNAL no startup events found' }
}

$output | Set-Content -LiteralPath $outputPath -Encoding UTF8
Record ('DIAGNOSTICS ' + $outputPath)
