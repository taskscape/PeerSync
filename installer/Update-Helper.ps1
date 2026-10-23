[CmdletBinding()]
param([switch]$Register, [switch]$Unregister, [switch]$Apply)

$ErrorActionPreference = 'Stop'
if (([int][bool]$Register + [int][bool]$Unregister + [int][bool]$Apply) -ne 1) {
    throw 'Specify exactly one of -Register, -Unregister or -Apply.'
}
$taskName = 'PeerSyncUpdateInstall'

function Write-UpdateEvent([int]$eventId, [string]$message, [string]$kind = 'Information') {
    try { Write-EventLog -LogName Application -Source PeerSync -EventId $eventId -EntryType $kind -Message $message }
    catch { }
}

function Confirm-InstalledUpdate([string]$serviceExe, [version]$offerVersion, [string]$taskName) {
    $service = Get-Service -Name PeerSync -ErrorAction Stop
    $service.WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
    $installedVersion = [version](Get-Item -LiteralPath $serviceExe).VersionInfo.FileVersion
    if ($installedVersion -lt $offerVersion) {
        throw 'Installed service version is older than the signed offer.'
    }
    $registered = Get-ScheduledTask -TaskName $taskName -ErrorAction Stop
    if ($registered.Principal.UserId -ne 'SYSTEM' -or
        $registered.Actions.Arguments -notmatch [regex]::Escape('PeerSync\tools\Update-Helper.ps1')) {
        throw 'Update task was not retained by Setup.'
    }
}

function Remove-ReadySlot([IO.DirectoryInfo]$ready, [string]$pendingRoot) {
    # The service can write pending slots. Recheck the exact owned GUID path
    # and reject a reparse point immediately before the recursive removal.
    $current = Get-Item -LiteralPath $ready.FullName -Force
    if (-not $current.PSIsContainer -or
        ($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $current.Name -cnotmatch '^[0-9a-f]{32}\.ready$' -or
        -not [string]::Equals([IO.Path]::GetFullPath($current.Parent.FullName),
            [IO.Path]::GetFullPath($pendingRoot), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Pending update slot changed before cleanup.'
    }
    Remove-Item -LiteralPath $current.FullName -Recurse -Force
}

function Remove-TrustedAttempt([IO.DirectoryInfo]$attempt, [string]$trustedRoot) {
    $current = Get-Item -LiteralPath $attempt.FullName -Force
    if (-not $current.PSIsContainer -or
        ($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $current.Name -cnotmatch '^[0-9a-f]{32}$' -or
        -not [string]::Equals([IO.Path]::GetFullPath($current.Parent.FullName),
            [IO.Path]::GetFullPath($trustedRoot), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Trusted update attempt changed before cleanup.'
    }
    Remove-Item -LiteralPath $current.FullName -Recurse -Force
}

function Write-UpdateAttempt([string]$trusted, [string]$readyName, [long]$startedUtcTicks,
    [string]$outcome) {
    $record = @{ readyName = $readyName; startedUtcTicks = $startedUtcTicks; outcome = $outcome } |
        ConvertTo-Json -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($record)
    $file = [IO.FileStream]::new((Join-Path $trusted 'attempt.json'),
        [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $file.Write($bytes, 0, $bytes.Length)
        $file.Flush($true)
    } finally { $file.Dispose() }
}

function Find-RestartedAttempt([string]$readyName, [string]$trustedRoot) {
    $matched = @()
    foreach ($directory in (Get-ChildItem -LiteralPath $trustedRoot -Directory)) {
        if ($directory.Name -cnotmatch '^[0-9a-f]{32}$' -or
            ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
        $path = Join-Path $directory.FullName 'attempt.json'
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        $file = Get-Item -LiteralPath $path -Force
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
        try {
            $record = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
            if ($record.readyName -ceq $readyName -and $record.outcome -ceq 'restartRequired' -and
                $record.startedUtcTicks -match '^[0-9]{15,19}$') {
                $matched += [pscustomobject]@{ Directory = $directory; Started = [long]$record.startedUtcTicks }
            }
        } catch { continue }
    }
    if ($matched.Count -eq 0) { return $null }
    if ($matched.Count -ne 1) { throw 'Multiple protected update attempts match the same package.' }
    $boot = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().Ticks
    if ($boot -le $matched[0].Started) { throw 'Restart is still required before update cleanup.' }
    return $matched[0].Directory
}

if ($Register) {
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $script = [IO.Path]::GetFullPath($PSCommandPath)
    $action = New-ScheduledTaskAction -Execute $windowsPowerShell `
        -Argument ('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}" -Apply' -f $script)
    $trigger = New-ScheduledTaskTrigger -Daily -At '03:00'
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 2)
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger `
        -Principal $principal -Settings $settings -Force | Out-Null
    exit 0
}
if ($Unregister) {
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    }
    exit 0
}

$mutex = [Threading.Mutex]::new($false, 'Global\PeerSyncUpdateInstall')
if (-not $mutex.WaitOne(0)) { $mutex.Dispose(); exit 0 }
try {
    $root = Join-Path $env:ProgramData 'PeerSync'
    $pendingRoot = Join-Path $root 'updates'
    $trustedRoot = Join-Path $root 'update-trusted'
    $policyPath = Join-Path $root 'update-policy\update-policy.json'
    $serviceExe = Join-Path $env:ProgramFiles 'PeerSync\service\PeerSync.Service.exe'
    if (-not (Test-Path -LiteralPath $policyPath)) { exit 0 }
    if (-not (Test-Path -LiteralPath $pendingRoot)) { exit 0 }
    if (-not (Test-Path -LiteralPath $trustedRoot -PathType Container)) { throw 'Trusted update directory is missing.' }
    $trustedInfo = Get-Item -LiteralPath $trustedRoot -Force
    if (($trustedInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Trusted update directory is a reparse point.' }
    $policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
    if ($policy.publisherCertificateSha256 -notmatch '^[0-9a-fA-F]{64}$') {
        throw 'Invalid update policy.'
    }
    if (-not $policy.enabled) { exit 0 }

    $candidates = @(Get-ChildItem -LiteralPath $pendingRoot -Directory -Filter '*.ready' | Sort-Object Name)
    if ($candidates.Count -eq 0) { exit 0 }
    $ready = $candidates[0]
    if ($ready.Name -cnotmatch '^[0-9a-f]{32}\.ready$' -or
        -not [string]::Equals([IO.Path]::GetFullPath($ready.Parent.FullName),
            [IO.Path]::GetFullPath($pendingRoot), [StringComparison]::OrdinalIgnoreCase) -or
        ($ready.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Invalid pending update directory.'
    }
    foreach ($name in @('manifest.json', 'setup.exe')) {
        $file = Get-Item -LiteralPath (Join-Path $ready.FullName $name) -Force
        if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Invalid pending update file.'
        }
    }

    # A successful 3010 install keeps its package until the next boot. Once
    # the installed version is current and healthy, retire that verified slot
    # so it does not block every subsequent feed check.
    $restartedAttempt = Find-RestartedAttempt $ready.Name $trustedRoot
    $classification = @(& $serviceExe --classify-update $ready.FullName)
    if ($LASTEXITCODE -ne 0 -or $classification.Count -ne 1 -or
        $classification[0] -notin @('CURRENT', 'NEWER')) {
        throw 'Signed pending update classification failed.'
    }
    if ($classification[0] -eq 'CURRENT') {
        if ($null -eq $restartedAttempt) { throw 'Current package has no protected restart receipt.' }
        $savedEnvelope = Get-Content -LiteralPath (Join-Path $ready.FullName 'manifest.json') -Raw | ConvertFrom-Json
        $savedPayload = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($savedEnvelope.payload)) |
            ConvertFrom-Json
        Confirm-InstalledUpdate $serviceExe ([version]$savedPayload.version) $taskName
        Remove-ReadySlot $ready $pendingRoot
        Remove-TrustedAttempt $restartedAttempt $trustedRoot
        Write-UpdateEvent 4303 'PeerSync completed update slot retired after restart.'
        exit 0
    }
    if ($null -ne $restartedAttempt) { throw 'Setup is still incomplete after restart.' }
    & $serviceExe --verify-update $ready.FullName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Signed pending update verification failed.' }
    $trusted = Join-Path $trustedRoot ([guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $trusted | Out-Null
    Copy-Item -LiteralPath (Join-Path $ready.FullName 'manifest.json') -Destination (Join-Path $trusted 'manifest.json')
    $trustedSetup = Join-Path $trusted 'setup.exe'
    Copy-Item -LiteralPath (Join-Path $ready.FullName 'setup.exe') -Destination $trustedSetup
    & $serviceExe --verify-update $trusted | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Trusted-copy update verification failed.' }
    & (Join-Path $PSScriptRoot 'Assert-UpdateSetupSignature.ps1') -Path $trustedSetup `
        -PublisherCertificateSha256 $policy.publisherCertificateSha256

    # This SYSTEM-owned receipt is written before Setup can replace the
    # service. A 3010 result must survive a later Windows boot before cleanup.
    $attemptStarted = [DateTime]::UtcNow.Ticks
    Write-UpdateAttempt $trusted $ready.Name $attemptStarted 'started'

    $setup = Start-Process -FilePath $trustedSetup `
        -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') `
        -WorkingDirectory $trusted -WindowStyle Hidden -Wait -PassThru
    if ($setup.ExitCode -notin @(0, 3010)) { throw ('Update setup failed with exit code ' + $setup.ExitCode) }
    Write-UpdateEvent 4301 ('PeerSync signed update setup completed with exit code ' + $setup.ExitCode)
    if ($setup.ExitCode -eq 3010) {
        Write-UpdateAttempt $trusted $ready.Name $attemptStarted 'restartRequired'
    }
    if ($setup.ExitCode -eq 0) {
        $savedEnvelope = Get-Content -LiteralPath (Join-Path $trusted 'manifest.json') -Raw | ConvertFrom-Json
        $savedPayload = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($savedEnvelope.payload)) |
            ConvertFrom-Json
        Confirm-InstalledUpdate $serviceExe ([version]$savedPayload.version) $taskName
        Remove-ReadySlot $ready $pendingRoot
        Remove-TrustedAttempt (Get-Item -LiteralPath $trusted) $trustedRoot
    }
    exit 0
}
catch {
    Write-UpdateEvent 4302 ('PeerSync signed update helper failed: ' + $_.Exception.GetType().Name) 'Warning'
    exit 1
}
finally {
    $mutex.ReleaseMutex()
    $mutex.Dispose()
}
