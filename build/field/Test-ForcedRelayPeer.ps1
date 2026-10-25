[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateRange(1, 65535)][int]$LocalPort,
    [Parameter(Mandatory)][int[]]$PeerPorts,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateRange(60, 900)][int]$DurationSeconds = 300,
    [switch]$PreflightOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($LocalPort -eq 99 -or $PeerPorts.Count -eq 0) {
    throw 'Specify the installed peer listener ports, never central port 99.'
}
$peerPortSet = @($PeerPorts | Sort-Object -Unique)
foreach ($port in $peerPortSet) {
    if ($port -lt 1 -or $port -gt 65535 -or $port -eq 99) {
        throw 'PeerPorts must be valid peer listener ports and must exclude central port 99.'
    }
}

$output = [IO.Path]::GetFullPath($OutputDirectory)
if (-not [IO.Directory]::Exists($output)) {
    throw 'OutputDirectory must already exist.'
}
$service = Get-CimInstance Win32_Service -Filter "Name='PeerSync'"
if ($null -eq $service -or $service.State -ne 'Running' -or $service.ProcessId -le 0) {
    throw 'PeerSync must be running before the forced-relay check.'
}
$image = [string]$service.PathName
if ($image -match '^\s*"([^"]+\\PeerSync\.Service\.exe)"(?:\s|$)') {
    $serviceExe = $Matches[1]
} elseif ($image -match '^\s*(.+?\\PeerSync\.Service\.exe)(?:\s|$)') {
    $serviceExe = $Matches[1]
} else {
    throw 'The PeerSync service image path is not recognizable.'
}
$serviceExe = [IO.Path]::GetFullPath($serviceExe)
$helper = [IO.Path]::Combine([IO.Path]::GetDirectoryName(
    [IO.Path]::GetDirectoryName($serviceExe)), 'tools', 'Stop-ServiceAndWait.ps1')
if (-not [IO.File]::Exists($serviceExe) -or -not [IO.File]::Exists($helper)) {
    throw 'The installed service or process-exit helper is missing.'
}

$tcp = @(Get-NetTCPConnection -State Listen -LocalPort $LocalPort -ErrorAction SilentlyContinue |
    Where-Object OwningProcess -eq $service.ProcessId)
$udp = @(Get-NetUDPEndpoint -LocalPort $LocalPort -ErrorAction SilentlyContinue |
    Where-Object OwningProcess -eq $service.ProcessId)
if ($tcp.Count -eq 0 -or $udp.Count -eq 0) {
    throw 'The supplied LocalPort is not bound for both TCP and UDP by PeerSync.'
}
$preflight = [ordered]@{
    serviceVersion = (Get-Item -LiteralPath $serviceExe).VersionInfo.FileVersion
    serviceProcessId = [int]$service.ProcessId
    localPort = $LocalPort
    peerPorts = $peerPortSet
    durationSeconds = $DurationSeconds
    centralPortExcluded = $true
}
'PREFLIGHT'
$preflight | ConvertTo-Json -Depth 4
if ($PreflightOnly) { return }

$principal = [Security.Principal.WindowsPrincipal]::new(
    [Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run the mutating forced-relay check from elevated PowerShell.'
}

$token = [guid]::NewGuid().ToString('N').Substring(0, 12)
$prefix = "PeerSync-RelayCheck-$token"
$rules = @("$prefix-InTcp", "$prefix-InUdp", "$prefix-OutTcp", "$prefix-OutUdp")
$taskName = "$prefix-Recover"
$reportPath = Join-Path $output "forced-relay-$token.json"
$report = [ordered]@{
    schema = 'peersync-forced-relay-peer-v1'
    preflight = $preflight
    startedUtc = $null
    endedUtc = $null
    rules = $rules
    rulesPresentAtEnd = $false
    serviceRunningAtEnd = $false
    rulesRemoved = $false
    recoveryTaskRemoved = $false
    serviceRunningAfterCleanup = $false
    failure = $null
}
$taskRegistered = $false
$serviceTouched = $false

function Restart-PeerSyncSafely {
    & $helper -ServiceName PeerSync -TimeoutSeconds 60 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'The installed process-exit helper failed.' }
    Start-Service -Name PeerSync -ErrorAction Stop
    (Get-Service -Name PeerSync).WaitForStatus(
        [System.ServiceProcess.ServiceControllerStatus]::Running,
        [TimeSpan]::FromSeconds(60))
}

try {
    # The independent SYSTEM task restores networking if this shell ends.
    $recovery = @'
$ErrorActionPreference = 'SilentlyContinue'
Remove-NetFirewallRule -Name @('__IN_TCP__','__IN_UDP__','__OUT_TCP__','__OUT_UDP__')
& '__HELPER__' -ServiceName PeerSync -TimeoutSeconds 60 | Out-Null
Start-Service -Name PeerSync
Unregister-ScheduledTask -TaskName '__TASK__' -Confirm:$false
'@
    $recovery = $recovery.Replace('__IN_TCP__', $rules[0]).Replace('__IN_UDP__', $rules[1]).
        Replace('__OUT_TCP__', $rules[2]).Replace('__OUT_UDP__', $rules[3]).
        Replace('__HELPER__', $helper.Replace("'", "''")).Replace('__TASK__', $taskName)
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($recovery))
    $action = New-ScheduledTaskAction -Execute (
        Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') `
        -Argument "-NoProfile -NonInteractive -EncodedCommand $encoded"
    $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddSeconds($DurationSeconds + 180)
    $taskPrincipal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger `
        -Principal $taskPrincipal -Settings (New-ScheduledTaskSettingsSet -StartWhenAvailable) `
        -Description 'Restore PeerSync after bounded forced-relay check' | Out-Null
    $taskRegistered = $true

    New-NetFirewallRule -Name $rules[0] -DisplayName 'PeerSync relay check inbound TCP' `
        -Direction Inbound -Action Block -Program $serviceExe -Protocol TCP `
        -LocalPort $LocalPort -Profile Any | Out-Null
    New-NetFirewallRule -Name $rules[1] -DisplayName 'PeerSync relay check inbound UDP' `
        -Direction Inbound -Action Block -Program $serviceExe -Protocol UDP `
        -LocalPort $LocalPort -Profile Any | Out-Null
    New-NetFirewallRule -Name $rules[2] -DisplayName 'PeerSync relay check outbound TCP' `
        -Direction Outbound -Action Block -Program $serviceExe -Protocol TCP `
        -RemotePort $peerPortSet -Profile Any | Out-Null
    New-NetFirewallRule -Name $rules[3] -DisplayName 'PeerSync relay check outbound UDP' `
        -Direction Outbound -Action Block -Program $serviceExe -Protocol UDP `
        -RemotePort $peerPortSet -Profile Any | Out-Null
    foreach ($name in $rules) {
        $rule = Get-NetFirewallRule -Name $name -ErrorAction Stop
        if ($rule.Enabled -ne 'True' -or $rule.Action -ne 'Block' -or
            ($rule | Get-NetFirewallApplicationFilter).Program -ne $serviceExe) {
            throw "Temporary firewall rule is not active: $name"
        }
    }

    $serviceTouched = $true
    Restart-PeerSyncSafely
    $report.startedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    "READY token=$token untilUtc=$([DateTimeOffset]::UtcNow.AddSeconds($DurationSeconds).ToString('O'))"
    'Create a fresh file only after every participating PC prints READY.'
    Start-Sleep -Seconds $DurationSeconds
    $report.endedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    $report.rulesPresentAtEnd = @(Get-NetFirewallRule -Name $rules -ErrorAction SilentlyContinue).Count -eq 4
    $report.serviceRunningAtEnd = (Get-Service -Name PeerSync).Status -eq 'Running'
    if (-not $report.rulesPresentAtEnd -or -not $report.serviceRunningAtEnd) {
        throw 'Firewall or service state changed before the interval ended.'
    }
} catch {
    $report.failure = $_.Exception.GetBaseException().Message
    throw
} finally {
    Remove-NetFirewallRule -Name $rules -ErrorAction SilentlyContinue
    $report.rulesRemoved = -not (Get-NetFirewallRule -Name $rules -ErrorAction SilentlyContinue)
    if ($taskRegistered -and $report.rulesRemoved) {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    }
    $report.recoveryTaskRemoved = -not (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue)
    if ($serviceTouched) {
        try { Restart-PeerSyncSafely }
        catch {
            if ($null -eq $report.failure) {
                $report.failure = "Post-test restart: $($_.Exception.GetBaseException().Message)"
            }
        }
    }
    if ($null -eq $report.endedUtc) { $report.endedUtc = [DateTimeOffset]::UtcNow.ToString('O') }
    $report.serviceRunningAfterCleanup = (Get-Service -Name PeerSync -ErrorAction SilentlyContinue).Status -eq 'Running'
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding UTF8
    "CLEANED rulesRemoved=$($report.rulesRemoved) recoveryTaskRemoved=$($report.recoveryTaskRemoved) serviceRunning=$($report.serviceRunningAfterCleanup)"
    "REPORT $reportPath"
}
