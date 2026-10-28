[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Root,
    [Parameter(Mandatory)][guid]$RunId,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_-]{1,32}$')][string]$MachineLabel,
    [Parameter(Mandatory)][ValidatePattern('^\d{4}-\d{2}-\d{2}T\d{2}:00:00Z$')][string]$FirstHourUtc,
    [Parameter(Mandatory)][string]$KitDirectory,
    [ValidateRange(10,60)][int]$LaunchLeadMinutes = 20,
    [switch]$Register
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$kit = [IO.Path]::GetFullPath($KitDirectory).TrimEnd('\')
$launcher = [IO.Path]::Combine($kit, 'Invoke-PeerSyncPhysicalSoak.ps1')
$powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
if (-not [IO.File]::Exists($launcher)) { throw 'Staged soak launcher missing.' }
if (-not [IO.File]::Exists($powershell)) { throw 'Windows PowerShell 5.1 missing.' }
foreach ($value in @($Root, $kit)) {
    if ($value.Contains('"')) { throw 'Paths cannot contain a quotation mark.' }
}
$first = [DateTime]::ParseExact($FirstHourUtc, 'yyyy-MM-ddTHH:mm:ssZ',
    [Globalization.CultureInfo]::InvariantCulture,
    [Globalization.DateTimeStyles]::AssumeUniversal).ToUniversalTime()
$launchUtc = $first.AddMinutes(-$LaunchLeadMinutes)
if ($launchUtc -le [DateTime]::UtcNow.AddMinutes(2)) {
    throw 'Scheduled launch is too close; select a later common UTC hour.'
}

$preflight = & $launcher -Root $Root -RunId $RunId -MachineLabel $MachineLabel `
    -FirstHourUtc $FirstHourUtc -OutputDirectory $kit -PreflightOnly
if ($preflight.status -ne 'ready') { throw 'Soak launcher preflight failed.' }
$launcherHash = (Get-FileHash -LiteralPath $launcher -Algorithm SHA256).Hash
$taskName = 'PeerSync-PhysicalSoak-' + $RunId.ToString('N') + '-' + $MachineLabel
$receipt = [ordered]@{
    schema = 'peersync-physical-soak-task-v1'
    computer = $env:COMPUTERNAME
    runId = $RunId.ToString('N')
    machineLabel = $MachineLabel
    firstHourUtc = $FirstHourUtc
    launchUtc = $launchUtc.ToString('yyyy-MM-ddTHH:mm:ssZ')
    launchLeadMinutes = $LaunchLeadMinutes
    initialContentBytes = $preflight.initialContentBytes
    launcherSha256 = $launcherHash
    taskName = $taskName
    status = if ($Register) { 'registering' } else { 'preflight-only' }
    utc = [DateTimeOffset]::UtcNow.ToString('O')
}
if (-not $Register) { return [pscustomobject]$receipt }

$existing = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($existing) { throw "The exact soak task already exists: $taskName" }
$receiptPath = Join-Path $kit ('soak-task-' + $RunId.ToString('N') + '-' + $MachineLabel + '.json')
if (Test-Path -LiteralPath $receiptPath) { throw 'Soak task receipt already exists.' }
$quote = { param([string]$value) "'" + $value.Replace("'", "''") + "'" }
$stdout = Join-Path $kit ("soak-task-$($RunId.ToString('N'))-$MachineLabel-stdout.txt")
$stderr = Join-Path $kit ("soak-task-$($RunId.ToString('N'))-$MachineLabel-stderr.txt")
if ((Test-Path -LiteralPath $stdout) -or (Test-Path -LiteralPath $stderr)) {
    throw 'Task output files already exist.'
}
$command = 'try { & ' + (& $quote $launcher) +
    ' -Root ' + (& $quote $Root) +
    ' -RunId ' + (& $quote $RunId.ToString()) +
    ' -MachineLabel ' + (& $quote $MachineLabel) +
    ' -FirstHourUtc ' + (& $quote $FirstHourUtc) +
    ' -OutputDirectory ' + (& $quote $kit) +
    ' 1> ' + (& $quote $stdout) + ' 2> ' + (& $quote $stderr) +
    '; if ($?) { exit 0 } else { exit 1 } } catch { ' +
    '[IO.File]::AppendAllText(' + (& $quote $stderr) +
    ', $_.Exception.ToString() + [Environment]::NewLine); exit 1 }'
$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
$args = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ' + $encoded
$receipt.taskStdout = $stdout
$receipt.taskStderr = $stderr
$receipt.actionCommandSha256 = [BitConverter]::ToString(
    [Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::Unicode.GetBytes($command))).Replace('-','')
$action = New-ScheduledTaskAction -Execute $powershell -Argument $args -WorkingDirectory $kit
$trigger = New-ScheduledTaskTrigger -Once -At $launchUtc.ToLocalTime()
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Hours 4 -Minutes 10) `
    -MultipleInstances IgnoreNew -WakeToRun -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$task = New-ScheduledTask -Action $action -Trigger $trigger -Settings $settings -Principal $principal
$registered = $false
try {
    Register-ScheduledTask -TaskName $taskName -InputObject $task -ErrorAction Stop | Out-Null
    $registered = $true
    $readBack = Get-ScheduledTask -TaskName $taskName -ErrorAction Stop
    if ($readBack.Principal.UserId -notin @('SYSTEM', 'NT AUTHORITY\SYSTEM', 'S-1-5-18') -or
        $readBack.State -notin @('Ready', 'Running')) {
        throw "Registered soak task failed read-back: $taskName"
    }
    $receipt.status = 'registered'
    $receipt.registeredUtc = [DateTimeOffset]::UtcNow.ToString('O')
    $receipt | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $receiptPath -Encoding UTF8
} catch {
    if ($registered) {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    }
    throw
}
[pscustomobject]$receipt
