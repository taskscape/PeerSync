[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Folder,
    [ValidateRange(60, 900)]
    [int]$DurationSeconds = 300,
    [string]$ExpectedUplinkAlias = 'Wi-Fi',
    [string]$OverlayAlias = 'Tailscale',
    [switch]$PreflightOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$folderPath = (Resolve-Path -LiteralPath $Folder).ProviderPath
if (-not (Test-Path -LiteralPath $folderPath -PathType Container)) {
    throw "Joined folder is not a directory: $Folder"
}

$service = Get-CimInstance Win32_Service -Filter "Name='PeerSync'"
if ($null -eq $service -or $service.State -ne 'Running') {
    throw 'The installed PeerSync service must be running before this test.'
}
$serviceCommand = $service.PathName.Trim()
if ($serviceCommand -match '^"([^\"]+\.exe)"') {
    $serviceExe = $Matches[1]
} elseif ($serviceCommand -match '^(.+?\.exe)(?:\s|$)') {
    $serviceExe = $Matches[1]
} else {
    throw 'Cannot identify the installed PeerSync service executable.'
}
if (-not (Test-Path -LiteralPath $serviceExe -PathType Leaf)) {
    throw "PeerSync service executable is missing: $serviceExe"
}
$overlay = Get-NetAdapter -Name $OverlayAlias -ErrorAction Stop
if ($overlay.Status -ne 'Up') {
    throw "The $OverlayAlias adapter must remain Up for management and Taildrop."
}
$uplink = Get-NetAdapter -Name $ExpectedUplinkAlias -ErrorAction Stop
if ($uplink.Status -ne 'Up' -or -not $uplink.HardwareInterface) {
    throw "The physical $ExpectedUplinkAlias adapter must be Up."
}
$serverAddress = Resolve-DnsName sync.taskscape.com -Type A |
    Where-Object IPAddress | Select-Object -First 1 -ExpandProperty IPAddress
if (-not $serverAddress) { throw 'No IPv4 address resolved for the rendezvous server.' }
$route = Find-NetRoute -RemoteIPAddress $serverAddress | Select-Object -First 1
if ($null -eq $route) { throw 'No route to the rendezvous server was found.' }
if ($route.InterfaceAlias -ne $ExpectedUplinkAlias) {
    throw "The server route must use $ExpectedUplinkAlias, not $($route.InterfaceAlias)."
}

$preflight = [ordered]@{
    serviceVersion = (Get-Item -LiteralPath $serviceExe).VersionInfo.FileVersion
    serviceExe = $serviceExe
    overlayAlias = $OverlayAlias
    overlayStatus = $overlay.Status.ToString()
    serverAddress = $serverAddress
    serverRouteAlias = $route.InterfaceAlias
    serverRouteSource = $route.IPAddress
    testDurationSeconds = $DurationSeconds
}
'PREFLIGHT'
$preflight | ConvertTo-Json -Depth 3
if ($PreflightOnly) { return }

$principal = [Security.Principal.WindowsPrincipal]::new(
    [Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this test from an elevated PowerShell window.'
}

$token = [guid]::NewGuid().ToString('N').Substring(0, 12)
$rulePrefix = "PeerSync-NativeWAN-$token"
$inRule = "$rulePrefix-In"
$outRule = "$rulePrefix-Out"
$recoveryTask = "$rulePrefix-Recover"
$reportPath = Join-Path $PSScriptRoot "NativeWAN-result-$token.json"
$report = [ordered]@{
    preflight = $preflight
    startedUtc = $null
    endedUtc = $null
    rules = @($inRule, $outRule)
    testFile = $null
    testFileSha256 = $null
    serviceRunningAtEnd = $false
    rulesPresentAtEnd = $false
    rulesRemoved = $false
    recoveryTaskRemoved = $false
    serviceRunningAfterCleanup = $false
    failure = $null
}
$taskRegistered = $false
$serviceTouched = $false

try {
    # A SYSTEM task removes the two test rules if the shell is killed. It
    # does not disable Tailscale itself or touch any other firewall rule.
    $recoverySource = "Remove-NetFirewallRule -Name '$inRule','$outRule' -ErrorAction SilentlyContinue; Restart-Service -Name PeerSync -ErrorAction SilentlyContinue"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($recoverySource))
    $action = New-ScheduledTaskAction -Execute (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') `
        -Argument "-NoProfile -NonInteractive -EncodedCommand $encoded"
    $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddSeconds($DurationSeconds + 180)
    $taskPrincipal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $taskSettings = New-ScheduledTaskSettingsSet -StartWhenAvailable
    Register-ScheduledTask -TaskName $recoveryTask -Action $action -Trigger $trigger `
        -Principal $taskPrincipal -Settings $taskSettings `
        -Description 'Remove temporary PeerSync native-WAN test rules' | Out-Null
    $taskRegistered = $true

    New-NetFirewallRule -Name $inRule -DisplayName 'PeerSync native WAN test inbound' `
        -Direction Inbound -Action Block -Program $serviceExe -InterfaceAlias $OverlayAlias `
        -Profile Any -Protocol Any | Out-Null
    New-NetFirewallRule -Name $outRule -DisplayName 'PeerSync native WAN test outbound' `
        -Direction Outbound -Action Block -Program $serviceExe -InterfaceAlias $OverlayAlias `
        -Profile Any -Protocol Any | Out-Null
    foreach ($name in @($inRule, $outRule)) {
        $rule = Get-NetFirewallRule -Name $name -ErrorAction Stop
        $application = $rule | Get-NetFirewallApplicationFilter
        $interface = $rule | Get-NetFirewallInterfaceFilter
        if ($rule.Enabled -ne 'True' -or $rule.Action -ne 'Block' -or
            $application.Program -ne $serviceExe -or
            $interface.InterfaceAlias -notcontains $OverlayAlias) {
            throw "Temporary firewall rule is not active: $name"
        }
    }

    # Existing overlay sockets must close before the measurement begins.
    $serviceTouched = $true
    Restart-Service -Name PeerSync -ErrorAction Stop
    (Get-Service -Name PeerSync).WaitForStatus(
        [System.ServiceProcess.ServiceControllerStatus]::Running,
        [TimeSpan]::FromSeconds(45))

    $started = (Get-Date).ToUniversalTime()
    $name = 'native-wan-third-{0}-{1}.txt' -f $started.ToString('yyyyMMddTHHmmssZ'), $token
    $testFile = Join-Path $folderPath $name
    if (Test-Path -LiteralPath $testFile) { throw "Test file already exists: $testFile" }
    $content = "PeerSync native WAN check`nCreated UTC: $($started.ToString('o'))`nNonce: $token`n"
    [IO.File]::WriteAllText($testFile, $content, [Text.UTF8Encoding]::new($false))
    $report.startedUtc = $started.ToString('o')
    $report.testFile = $name
    $report.testFileSha256 = (Get-FileHash -LiteralPath $testFile -Algorithm SHA256).Hash
    "READY file=$name sha256=$($report.testFileSha256) untilUtc=$($started.AddSeconds($DurationSeconds).ToString('o'))"
    "Tailscale remains available; only $serviceExe is blocked on $OverlayAlias."

    Start-Sleep -Seconds $DurationSeconds
    $report.endedUtc = (Get-Date).ToUniversalTime().ToString('o')
    $report.rulesPresentAtEnd = @(
        Get-NetFirewallRule -Name $inRule, $outRule -ErrorAction SilentlyContinue).Count -eq 2
    if (-not $report.rulesPresentAtEnd) {
        throw 'The temporary overlay-block rules disappeared before the test interval ended.'
    }
    $report.serviceRunningAtEnd = (Get-Service -Name PeerSync).Status -eq 'Running'
} catch {
    $report.failure = $_.Exception.Message
    throw
} finally {
    foreach ($name in @($inRule, $outRule)) {
        Remove-NetFirewallRule -Name $name -ErrorAction SilentlyContinue
    }
    $report.rulesRemoved = -not (Get-NetFirewallRule -Name $inRule, $outRule -ErrorAction SilentlyContinue)
    if ($taskRegistered -and $report.rulesRemoved) {
        Unregister-ScheduledTask -TaskName $recoveryTask -Confirm:$false -ErrorAction SilentlyContinue
    }
    $report.recoveryTaskRemoved = -not (Get-ScheduledTask -TaskName $recoveryTask -ErrorAction SilentlyContinue)
    if ($serviceTouched) {
        try {
            if ((Get-Service -Name PeerSync).Status -eq 'Running') {
                Restart-Service -Name PeerSync -ErrorAction Stop
            } else {
                Start-Service -Name PeerSync -ErrorAction Stop
            }
        } catch {
            if ($null -eq $report.failure) { $report.failure = "Post-test restart: $($_.Exception.Message)" }
        }
    }
    if ($null -eq $report.endedUtc) { $report.endedUtc = (Get-Date).ToUniversalTime().ToString('o') }
    $report.serviceRunningAfterCleanup = (Get-Service -Name PeerSync -ErrorAction SilentlyContinue).Status -eq 'Running'
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding UTF8
    "CLEANED rulesRemoved=$($report.rulesRemoved) recoveryTaskRemoved=$($report.recoveryTaskRemoved)"
    "REPORT $reportPath"
}
