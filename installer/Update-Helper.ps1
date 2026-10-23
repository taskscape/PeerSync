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

    $setup = Start-Process -FilePath $trustedSetup `
        -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') `
        -WorkingDirectory $trusted -WindowStyle Hidden -Wait -PassThru
    if ($setup.ExitCode -notin @(0, 3010)) { throw ('Update setup failed with exit code ' + $setup.ExitCode) }
    Write-UpdateEvent 4301 ('PeerSync signed update setup completed with exit code ' + $setup.ExitCode)
    if ($setup.ExitCode -eq 0) {
        $service = Get-Service -Name PeerSync -ErrorAction Stop
        $service.WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
        $installedVersion = [version](Get-Item -LiteralPath $serviceExe).VersionInfo.FileVersion
        $savedEnvelope = Get-Content -LiteralPath (Join-Path $trusted 'manifest.json') -Raw | ConvertFrom-Json
        $savedPayload = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($savedEnvelope.payload)) |
            ConvertFrom-Json
        if ($installedVersion -lt [version]$savedPayload.version) {
            throw 'Installed service version is older than the signed offer.'
        }
        $registered = Get-ScheduledTask -TaskName $taskName -ErrorAction Stop
        if ($registered.Principal.UserId -ne 'SYSTEM' -or
            $registered.Actions.Arguments -notmatch [regex]::Escape('PeerSync\tools\Update-Helper.ps1')) {
            throw 'Update task was not retained by Setup.'
        }
        # Remove only the already-validated GUID slot under the intended parent.
        if ([string]::Equals([IO.Path]::GetFullPath($ready.Parent.FullName),
                [IO.Path]::GetFullPath($pendingRoot), [StringComparison]::OrdinalIgnoreCase) -and
            $ready.Name -cmatch '^[0-9a-f]{32}\.ready$') {
            Remove-Item -LiteralPath $ready.FullName -Recurse -Force
        }
        if ([string]::Equals([IO.Path]::GetFullPath((Get-Item -LiteralPath $trusted).Parent.FullName),
                [IO.Path]::GetFullPath($trustedRoot), [StringComparison]::OrdinalIgnoreCase) -and
            (Split-Path $trusted -Leaf) -cmatch '^[0-9a-f]{32}$') {
            Remove-Item -LiteralPath $trusted -Recurse -Force
        }
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
