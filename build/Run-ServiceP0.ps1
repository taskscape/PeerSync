[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'artifacts/p0-services'
New-Item -ItemType Directory -Force $out | Out-Null
& "$PSScriptRoot/Write-Environment.ps1" -Path (Join-Path $out 'environment.json')
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'This cross-account spike requires an elevated PowerShell'
}
$names = @('PeerSyncSpike','PeerSyncControlSpike')
foreach ($name in $names) { if (Get-Service $name -ErrorAction SilentlyContinue) { throw "Refusing to change existing service $name" } }
$tag = [Guid]::NewGuid().ToString('N').Substring(0,8)
$workspace = Join-Path $env:ProgramData "PeerSync-P0-$tag"
$userName = "PeerSyncP0_$tag"
$created = [Collections.Generic.List[string]]::new()
$thumbprint = $null
$userCreated = $false

function Invoke-Sc([string[]]$Arguments) {
    & sc.exe @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe $($Arguments[0]) failed ($LASTEXITCODE)" }
}
function Wait-Service([string]$Name, [ServiceProcess.ServiceControllerStatus]$Status) {
    $service = Get-Service $Name
    try { $service.WaitForStatus($Status, [TimeSpan]::FromSeconds(25)) }
    finally { $service.Dispose() }
}
function Invoke-UserProbe([string]$Executable, [string[]]$Arguments, [string]$Label, [int]$ExpectedExit = 0) {
    $stdout = Join-Path $workspace "output/$Label.stdout.log"
    $stderr = Join-Path $workspace "output/$Label.stderr.log"
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -Credential $credential -LoadUserProfile -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr -WorkingDirectory $workspace
    try {
        if (-not $process.WaitForExit(30000)) { Stop-Process -Id $process.Id -Force; throw "$Label timed out" }
        $process.Refresh()
        if ($process.ExitCode -ne $ExpectedExit) { throw "$Label exited $($process.ExitCode), expected $ExpectedExit" }
        Write-Host "$Label passed"
    }
    finally {
        Copy-Item -LiteralPath $stdout,$stderr -Destination $out -ErrorAction SilentlyContinue
        $process.Dispose()
    }
}

try {
    New-Item -ItemType Directory -Force "$workspace/DeviceCert","$workspace/ControlPipe","$workspace/output" | Out-Null
    foreach ($name in @('DeviceCert','ControlPipe')) {
        & dotnet publish (Join-Path $root "spikes/$name/$name.csproj") -c Release -r win-x64 --self-contained -o "$workspace/$name" *> (Join-Path $out "publish-$name.log")
        if ($LASTEXITCODE -ne 0) { throw "Publish failed: $name" }
    }
    $password = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48)) + '!aA1'
    $secure = ConvertTo-SecureString $password -AsPlainText -Force
    $credential = [PSCredential]::new("$env:COMPUTERNAME\$userName", $secure)
    $password = $null
    New-LocalUser -Name $userName -Password $secure -AccountNeverExpires -PasswordNeverExpires | Out-Null
    $userCreated = $true
    $userSid = (Get-LocalUser $userName).SID.Value
    Add-LocalGroupMember -SID 'S-1-5-32-545' -Member $userName
    & icacls.exe $workspace /grant "*${userSid}:(OI)(CI)(RX)" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Unable to grant probe read access' }
    & icacls.exe "$workspace/output" /grant "*${userSid}:(OI)(CI)(M)" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Unable to grant probe output access' }
    $certExe = Join-Path $workspace 'DeviceCert/DeviceCert.exe'
    $pipeExe = Join-Path $workspace 'ControlPipe/ControlPipe.exe'
    # Reserve a free TCP port; QUIC subsequently verifies the same UDP port is usable.
    $socket = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
    $socket.Start(); $port=$socket.LocalEndpoint.Port; $socket.Stop()
    Invoke-Sc @('create','PeerSyncSpike','binPath=',"`"$certExe`" serve pending $port",'start=','demand','obj=','NT SERVICE\PeerSyncSpike')
    $created.Add('PeerSyncSpike')
    $serviceSid = ([Security.Principal.NTAccount]::new('NT SERVICE\PeerSyncSpike')).Translate([Security.Principal.SecurityIdentifier]).Value
    $thumbprint = (& $certExe create $serviceSid | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $thumbprint -notmatch '^[0-9A-F]{40}$') { throw 'Machine certificate creation failed' }
    Invoke-Sc @('config','PeerSyncSpike','binPath=',"`"$certExe`" serve $thumbprint $port")
    Invoke-Sc @('start','PeerSyncSpike')
    Wait-Service 'PeerSyncSpike' Running
    Start-Sleep -Milliseconds 1500
    & $certExe verify $thumbprint 127.0.0.1 $port *> (Join-Path $out 'virtual-account-tls.log')
    if ($LASTEXITCODE -ne 0) { throw 'Virtual account QUIC/TLS failed' }
    Invoke-UserProbe $certExe @('assert-key-denied',$thumbprint) 'standard-user-private-key-denied'

    $pipe = "PeerSync.P0.$tag"
    Invoke-Sc @('create','PeerSyncControlSpike','binPath=',"`"$pipeExe`" server $pipe $($identity.User.Value)",'start=','demand','obj=','NT SERVICE\PeerSyncControlSpike')
    $created.Add('PeerSyncControlSpike')
    $pipeOwner = ([Security.Principal.NTAccount]::new('NT SERVICE\PeerSyncControlSpike')).Translate([Security.Principal.SecurityIdentifier]).Value
    Invoke-Sc @('start','PeerSyncControlSpike')
    Wait-Service 'PeerSyncControlSpike' Running
    Start-Sleep -Milliseconds 1000
    Invoke-UserProbe $pipeExe @('client',$pipe,$pipeOwner) 'unauthorized-standard-user' 1
    Invoke-Sc @('stop','PeerSyncControlSpike')
    Wait-Service 'PeerSyncControlSpike' Stopped
    Invoke-Sc @('config','PeerSyncControlSpike','binPath=',"`"$pipeExe`" server $pipe $userSid")
    Invoke-Sc @('start','PeerSyncControlSpike')
    Wait-Service 'PeerSyncControlSpike' Running
    Start-Sleep -Milliseconds 1000
    Invoke-UserProbe $pipeExe @('client',$pipe,$pipeOwner) 'authorized-standard-user'
    @{status='passed';utc=[DateTime]::UtcNow.ToString('O');virtualAccount=$true;privateKeyDenied=$true;unauthorizedPipeDenied=$true;authorizedPipeCallerSidVerified=$true} | ConvertTo-Json | Set-Content (Join-Path $out 'summary.json')
}
finally {
    foreach ($name in $created) {
        $service = Get-Service $name -ErrorAction SilentlyContinue
        if ($service -and $service.Status -ne 'Stopped') { Stop-Service $name -Force; Wait-Service $name Stopped }
        Invoke-Sc @('delete',$name)
    }
    if ($thumbprint) { & $certExe delete $thumbprint; if ($LASTEXITCODE -ne 0) { Write-Warning 'Spike certificate cleanup failed' } }
    if ($userCreated) {
        $profile = Get-CimInstance Win32_UserProfile -Filter "SID='$userSid'"
        if ($profile) {
            $expectedProfile = [IO.Path]::GetFullPath((Join-Path "$env:SystemDrive/Users" $userName))
            if ([IO.Path]::GetFullPath($profile.LocalPath) -ne $expectedProfile -or $profile.Loaded) {
                throw 'Refusing to remove an unexpected or still-loaded test user profile'
            }
            $profile | Remove-CimInstance
        }
        Remove-LocalUser -Name $userName
    }
    $resolved = [IO.Path]::GetFullPath($workspace)
    $parent = [IO.Path]::GetFullPath($env:ProgramData).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($parent,[StringComparison]::OrdinalIgnoreCase) -or (Split-Path $resolved -Leaf) -notmatch '^PeerSync-P0-[0-9a-f]{8}$') { throw 'Unexpected cleanup path' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    $identity.Dispose()
}
