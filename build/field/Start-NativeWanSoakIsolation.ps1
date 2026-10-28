[CmdletBinding()]
param(
    [Parameter(Mandatory)][guid]$RunId,
    [Parameter(Mandatory)][ValidatePattern('^\d{4}-\d{2}-\d{2}T\d{2}:00:00Z$')]
    [string]$FirstHourUtc,
    [Parameter(Mandatory)][string]$Root,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$KitDirectory,
    [string]$ExpectedUplinkAlias = 'Wi-Fi',
    [string]$OverlayAlias = 'Tailscale',
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$DisallowedUplinkPrefix,
    [ValidateRange(10, 25)][int]$CaptureGraceMinutes = 20,
    [switch]$PreflightOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$run = $RunId.ToString('N')
$first = [DateTime]::ParseExact($FirstHourUtc, 'yyyy-MM-ddTHH:mm:ssZ',
    [Globalization.CultureInfo]::InvariantCulture,
    [Globalization.DateTimeStyles]::AssumeUniversal).ToUniversalTime()
$activeEnd = $first.AddHours(3)
$cleanup = $activeEnd.AddMinutes($CaptureGraceMinutes)
$now = [DateTime]::UtcNow
if ($first -le $now.AddMinutes(2)) { throw 'The first capture hour must be at least two minutes ahead.' }
if (-not $PreflightOnly -and $first -gt $now.AddMinutes(10)) {
    throw 'Start native-WAN isolation no earlier than ten minutes before the first hour.'
}

$rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\')
$output = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
$kit = [IO.Path]::GetFullPath($KitDirectory).TrimEnd('\')
if (-not [IO.Directory]::Exists($rootPath) -or -not [IO.Directory]::Exists($output) -or
    -not [IO.Directory]::Exists($kit) -or
    $output.Equals($rootPath, [StringComparison]::OrdinalIgnoreCase) -or
    $output.StartsWith($rootPath + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Existing root, separate evidence directory and staged kit are required.'
}
foreach ($path in @($rootPath, $output, $kit)) {
    if (([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Test paths cannot be reparse points.'
    }
}

$launcher = Join-Path $kit 'Invoke-PeerSyncPhysicalSoak.ps1'
if (-not [IO.File]::Exists($launcher)) { throw 'The staged physical-soak launcher is missing.' }
$preflight = & $launcher -Root $rootPath -RunId $RunId -MachineLabel 'wan' `
    -FirstHourUtc $FirstHourUtc -OutputDirectory $output -PreflightOnly
if ($preflight.status -ne 'ready') { throw 'The physical-soak preflight did not pass.' }

$service = Get-CimInstance Win32_Service -Filter "Name='PeerSync'"
if (-not $service -or $service.State -ne 'Running') { throw 'PeerSync must be running.' }
$command = $service.PathName.Trim()
if ($command -match '^"([^\"]+\.exe)"') {
    $serviceExe = $Matches[1]
} elseif ($command -match '^(.+?\.exe)(?:\s|$)') {
    $serviceExe = $Matches[1]
} else { throw 'Cannot resolve the installed service executable.' }
if (-not [IO.File]::Exists($serviceExe)) { throw 'Installed service executable is missing.' }

$overlay = Get-NetAdapter -Name $OverlayAlias -ErrorAction Stop
$uplink = Get-NetAdapter -Name $ExpectedUplinkAlias -ErrorAction Stop
if ($overlay.Status -ne 'Up') { throw 'Management overlay must remain connected.' }
if ($uplink.Status -ne 'Up' -or -not $uplink.HardwareInterface) {
    throw 'The expected physical uplink must be connected.'
}
$serverAddress = Resolve-DnsName sync.taskscape.com -Type A -ErrorAction Stop |
    Where-Object IPAddress | Select-Object -First 1 -ExpandProperty IPAddress
if (-not $serverAddress) { throw 'Rendezvous IPv4 address was not resolved.' }
$route = Find-NetRoute -RemoteIPAddress $serverAddress | Select-Object -First 1
if (-not $route -or $route.InterfaceAlias -ne $ExpectedUplinkAlias) {
    throw 'Rendezvous traffic is not routed through the expected physical uplink.'
}
if ([string]$route.IPAddress -like ($DisallowedUplinkPrefix + '*')) {
    throw 'The physical uplink still has a disallowed LAN address.'
}
$cli = Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($serviceExe))) 'cli\psp.exe'
if (-not [IO.File]::Exists($cli)) { throw 'Installed PeerSync CLI is missing.' }
$probeText = & $cli probe 'sync.taskscape.com:99'
if ($LASTEXITCODE -ne 0) { throw 'Rendezvous TCP/UDP probe failed.' }
$probe = $probeText | ConvertFrom-Json
if (-not $probe.tcp.reachable -or -not $probe.udp.reachable -or
    -not $probe.tcp.correlated -or -not $probe.udp.correlated) {
    throw 'Rendezvous TCP/UDP probe is incomplete.'
}

$report = [ordered]@{
    schema='peersync-native-wan-soak-isolation-v1';runId=$run;
    status='ready';checkedUtc=[DateTime]::UtcNow.ToString('o');
    firstHourUtc=$FirstHourUtc;activeEndUtc=$activeEnd.ToString('o');
    cleanupDueUtc=$cleanup.ToString('o');captureGraceMinutes=$CaptureGraceMinutes;
    contentBytes=[long]$preflight.initialContentBytes;
    serviceVersion=(Get-Item -LiteralPath $serviceExe).VersionInfo.FileVersion;
    uplinkAlias=$route.InterfaceAlias;uplinkAddress=$route.IPAddress;
    disallowedUplinkPrefix=$DisallowedUplinkPrefix;
    overlayAlias=$OverlayAlias;serverAddress=$serverAddress;
    probeSessionId=$probe.sessionId;probeTcp=$probe.tcp.reachable;probeUdp=$probe.udp.reachable;
    launcherSha256=(Get-FileHash -LiteralPath $launcher -Algorithm SHA256).Hash;
    helperSha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
}
if ($PreflightOnly) { return [pscustomobject]$report }

$principal = [Security.Principal.WindowsPrincipal]::new(
    [Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'The native-WAN isolation start requires elevation.'
}
$prefix = 'PeerSync-NativeWanSoak-' + $run
$inRule = $prefix + '-In'
$outRule = $prefix + '-Out'
$taskName = $prefix + '-Recover'
$reportPath = Join-Path $output ($prefix + '.json')
$recoveryReportPath = Join-Path $output ($prefix + '-recovery.json')
if ([IO.File]::Exists($reportPath) -or
    [IO.File]::Exists($recoveryReportPath) -or
    (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) -or
    (Get-NetFirewallRule -Name $inRule,$outRule -ErrorAction SilentlyContinue)) {
    throw 'This native-WAN isolation run has already been started.'
}

function Restart-PeerSyncAfterExit {
    $current = Get-CimInstance Win32_Service -Filter "Name='PeerSync'" -ErrorAction Stop
    $old = $null
    try {
        if ($current.ProcessId -gt 0) { $old = Get-Process -Id $current.ProcessId -ErrorAction SilentlyContinue }
        if ($current.State -eq 'Running') { Stop-Service -Name PeerSync -ErrorAction Stop }
        (Get-Service PeerSync).WaitForStatus(
            [System.ServiceProcess.ServiceControllerStatus]::Stopped,[TimeSpan]::FromSeconds(45))
        if ($old -and -not $old.WaitForExit(45000)) { throw 'Old PeerSync process did not exit.' }
        Start-Service -Name PeerSync -ErrorAction Stop
        (Get-Service PeerSync).WaitForStatus(
            [System.ServiceProcess.ServiceControllerStatus]::Running,[TimeSpan]::FromSeconds(45))
    } finally { if ($old) { $old.Dispose() } }
}

$registered = $false
$serviceTouched = $false
try {
    # The SYSTEM recovery task owns only these two run-specific rules. Its
    # later trigger covers the final read-only snapshot after active work ends.
    $recoverySource = @'
$ErrorActionPreference = 'Continue'
$issue = $null
Remove-NetFirewallRule -Name '__IN__','__OUT__' -ErrorAction SilentlyContinue
$rulesRemoved = -not @(Get-NetFirewallRule -Name '__IN__','__OUT__' -ErrorAction SilentlyContinue).Count
try {
    $service = Get-CimInstance Win32_Service -Filter "Name='PeerSync'" -ErrorAction Stop
    $old = if ($service.ProcessId -gt 0) { Get-Process -Id $service.ProcessId -ErrorAction SilentlyContinue }
    if ($service.State -eq 'Running') { Stop-Service PeerSync -ErrorAction Stop }
    if ($old) {
        if (-not $old.WaitForExit(45000)) { throw 'Old PeerSync process did not exit.' }
        $old.Dispose()
    }
    Start-Service PeerSync -ErrorAction Stop
    (Get-Service PeerSync).WaitForStatus(
        [System.ServiceProcess.ServiceControllerStatus]::Running,[TimeSpan]::FromSeconds(45))
} catch { $issue = $_.Exception.Message }
$running = (Get-Service PeerSync -ErrorAction SilentlyContinue).Status -eq 'Running'
[ordered]@{schema='peersync-native-wan-isolation-recovery-v1';
    utc=[DateTime]::UtcNow.ToString('o');runId='__RUN__';
    rulesRemoved=[bool]$rulesRemoved;serviceRunning=[bool]$running;issue=$issue} |
    ConvertTo-Json -Depth 3 | Set-Content -LiteralPath '__REPORT__' -Encoding UTF8
if (-not $rulesRemoved -or -not $running -or $issue) { exit 1 }
'@
    $recoverySource = $recoverySource.Replace('__IN__',$inRule).Replace('__OUT__',$outRule).
        Replace('__RUN__',$run).Replace('__REPORT__',$recoveryReportPath.Replace("'", "''"))
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($recoverySource))
    $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $action = New-ScheduledTaskAction -Execute $powershell -Argument "-NoProfile -NonInteractive -EncodedCommand $encoded"
    $trigger = New-ScheduledTaskTrigger -Once -At $cleanup.ToLocalTime()
    $taskPrincipal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $taskSettings = New-ScheduledTaskSettingsSet -StartWhenAvailable -WakeToRun
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger `
        -Principal $taskPrincipal -Settings $taskSettings -ErrorAction Stop | Out-Null
    $registered = $true
    $task = Get-ScheduledTask -TaskName $taskName -ErrorAction Stop
    $taskInfo = $task | Get-ScheduledTaskInfo
    if ($task.Principal.UserId -notin @('SYSTEM','NT AUTHORITY\SYSTEM','S-1-5-18') -or
        $task.State -notin @('Ready','Running') -or
        $task.Actions[0].Execute -ne $powershell -or
        [Math]::Abs(($taskInfo.NextRunTime.ToUniversalTime() - $cleanup).TotalSeconds) -gt 60) {
        throw 'The independent firewall cleanup task failed read-back.'
    }

    New-NetFirewallRule -Name $inRule -DisplayName 'PeerSync native WAN soak inbound' `
        -Direction Inbound -Action Block -Program $serviceExe -InterfaceAlias $OverlayAlias `
        -Profile Any -Protocol Any -ErrorAction Stop | Out-Null
    New-NetFirewallRule -Name $outRule -DisplayName 'PeerSync native WAN soak outbound' `
        -Direction Outbound -Action Block -Program $serviceExe -InterfaceAlias $OverlayAlias `
        -Profile Any -Protocol Any -ErrorAction Stop | Out-Null
    foreach ($name in @($inRule,$outRule)) {
        $rule = Get-NetFirewallRule -Name $name -ErrorAction Stop
        $application = $rule | Get-NetFirewallApplicationFilter
        $interface = $rule | Get-NetFirewallInterfaceFilter
        if ($rule.Enabled -ne 'True' -or $rule.Action -ne 'Block' -or
            $application.Program -ne $serviceExe -or
            $interface.InterfaceAlias -notcontains $OverlayAlias) {
            throw 'Program-scoped overlay block did not pass read-back.'
        }
    }
    $serviceTouched = $true
    Restart-PeerSyncAfterExit
    $finalRoute = Find-NetRoute -RemoteIPAddress $serverAddress | Select-Object -First 1
    if (-not $finalRoute -or $finalRoute.InterfaceAlias -ne $ExpectedUplinkAlias -or
        [string]$finalRoute.IPAddress -like ($DisallowedUplinkPrefix + '*') -or
        (Get-NetAdapter -Name $OverlayAlias).Status -ne 'Up') {
        throw 'The physical route or management overlay changed during service restart.'
    }
    $report.status = 'active'
    $report.startedUtc = [DateTime]::UtcNow.ToString('o')
    $report.rules = @($inRule,$outRule)
    $report.recoveryTask = $taskName
    $report.recoveryReport = $recoveryReportPath
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $reportPath -Encoding UTF8
    "READY runId=$run activeEndUtc=$($activeEnd.ToString('o')) cleanupDueUtc=$($cleanup.ToString('o'))"
    "REPORT $reportPath"
} catch {
    $reason = $_.Exception.Message
    Remove-NetFirewallRule -Name $inRule,$outRule -ErrorAction SilentlyContinue
    if ($registered) { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue }
    if ($serviceTouched) {
        try { Restart-PeerSyncAfterExit } catch { $reason += '; service recovery: ' + $_.Exception.Message }
    }
    $report.status = 'failed'
    $report.failure = $reason
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $reportPath -Encoding UTF8
    throw
}
