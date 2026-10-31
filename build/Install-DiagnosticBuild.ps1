[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Client', 'Rendezvous')]
    [string]$Role,
    [string]$PackageRoot = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this package installer from an elevated PowerShell process.'
}

$package = [IO.Path]::GetFullPath($PackageRoot).TrimEnd('\')
if (-not (Test-Path -LiteralPath $package -PathType Container)) { throw "Package not found: $package" }
$serviceName = if ($Role -eq 'Client') { 'PeerSync' } else { 'PeerSyncRendezvous' }
$service = Get-Service -Name $serviceName -ErrorAction Stop
$programFiles64 = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
$installRoot = if ($Role -eq 'Client') {
    Join-Path $programFiles64 'PeerSync'
} else {
    Join-Path $programFiles64 'PeerSync Rendezvous'
}
if (-not (Test-Path -LiteralPath $installRoot -PathType Container)) {
    throw "Existing installation not found: $installRoot"
}
if (-not $WhatIfPreference -and $Role -eq 'Client' -and
    (Get-Process -Name PeerSync -ErrorAction SilentlyContinue)) {
    throw 'Close the PeerSync desktop app, including its tray icon, before upgrading the client.'
}

$parts = if ($Role -eq 'Client') { @('service', 'app', 'cli') } else { @('rendezvous') }
foreach ($part in $parts) {
    $source = Join-Path $package $part
    $expected = switch ($part) {
        service { 'PeerSync.Service.exe' }
        app { 'PeerSync.exe' }
        cli { 'psp.exe' }
        rendezvous { 'PeerSync.Rendezvous.exe' }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $source $expected) -PathType Leaf)) {
        throw "Package is incomplete: $source\\$expected"
    }
}

$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$backup = Join-Path $env:ProgramData "PeerSync\backups\diagnostic-$Role-$stamp"

function Copy-Contents([string]$from, [string]$to, [bool]$preserveServerSettings) {
    New-Item -ItemType Directory -Path $to -Force | Out-Null
    foreach ($entry in Get-ChildItem -LiteralPath $from -Force) {
        if ($preserveServerSettings -and $entry.Name -eq 'appsettings.json') { continue }
        Copy-Item -LiteralPath $entry.FullName -Destination $to -Recurse -Force
    }
}

if (-not $PSCmdlet.ShouldProcess($installRoot, "Back up and install PeerSync diagnostic $Role build")) { return }
try {
    Stop-Service -Name $serviceName -ErrorAction Stop
    $service.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(40))
    foreach ($part in $parts) {
        $destination = if ($Role -eq 'Client') { Join-Path $installRoot $part } else { $installRoot }
        $backupPart = Join-Path $backup $part
        Copy-Contents $destination $backupPart ($Role -eq 'Rendezvous')
        Copy-Contents (Join-Path $package $part) $destination ($Role -eq 'Rendezvous')
    }
    Start-Service -Name $serviceName -ErrorAction Stop
    $service.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(40))
    Write-Output "Installed $Role diagnostic build. Previous files: $backup"
}
catch {
    $installError = $_
    Write-Warning "Install failed; restoring files from $backup"
    foreach ($part in $parts) {
        $backupPart = Join-Path $backup $part
        if (-not (Test-Path -LiteralPath $backupPart -PathType Container)) { continue }
        $destination = if ($Role -eq 'Client') { Join-Path $installRoot $part } else { $installRoot }
        Copy-Contents $backupPart $destination $false
    }
    if ((Get-Service -Name $serviceName).Status -ne 'Running') { Start-Service -Name $serviceName }
    throw $installError
}
