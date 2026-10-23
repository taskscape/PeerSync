[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$audit = Join-Path $root 'installer/Audit-CrashPolicy.ps1'
$policy = Join-Path $root 'installer/Set-WerPolicy.ps1'
$name = 'PeerSync-Audit-' + [guid]::NewGuid().ToString('N') + '.exe'
$hive = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
    [Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Default)
$wer = 'SOFTWARE\Microsoft\Windows\Windows Error Reporting'
$dumpsPath = "$wer\LocalDumps"
$priorGlobal = $hive.OpenSubKey($dumpsPath)
$hadGlobalKey = $null -ne $priorGlobal
if ($priorGlobal) { $priorGlobal.Dispose() }
function Check([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }
function ReadAudit {
    $result = & $audit -CurrentUserForTest -ExecutableNamesForTest $name | ConvertFrom-Json
    Check ($null -ne $result) 'Audit did not return JSON.'
    Check ($result.scope -eq 'CurrentUserTest') 'Audit used the wrong registry scope.'
    Check ($result.executables.Count -eq 1 -and $result.executables[0].name -eq $name) 'Audit used the wrong executable.'
    return $result
}
try {
    $before = ReadAudit
    Check (-not $before.allAutomaticReportsExcluded) 'Audit missed absent WER exclusion.'
    Check (-not $before.executables[0].localDumpsConfigured) 'Audit found an unexpected per-executable dump policy.'
    & $policy -ExecutableNames $name -CurrentUserForTest
    $installed = ReadAudit
    Check ($installed.allAutomaticReportsExcluded -and $installed.executables[0].automaticReportsExcluded) 'Audit missed installed WER exclusion.'
    Check ($installed.executables[0].ownedByPeerSyncInstaller) 'Audit missed installer ownership.'
    $global = $hive.CreateSubKey($dumpsPath, $true)
    try {
        $specific = $global.CreateSubKey($name, $true)
        try { $specific.SetValue('DumpType', 2, [Microsoft.Win32.RegistryValueKind]::DWord) }
        finally { $specific.Dispose() }
    } finally { $global.Dispose() }
    $configured = ReadAudit
    Check ($configured.globalLocalDumpsKeyPresent -and $configured.anyPerExecutableLocalDumpsConfigured) 'Audit missed configured LocalDumps.'
    Check ($configured.executables[0].localDumpsConfigured) 'Audit missed executable LocalDumps override.'
    & $policy -ExecutableNames $name -CurrentUserForTest -Remove
    $removed = ReadAudit
    Check (-not $removed.allAutomaticReportsExcluded -and -not $removed.executables[0].ownedByPeerSyncInstaller) 'Audit missed uninstall cleanup.'
    'Read-only crash policy audit controls passed.'
} finally {
    $global = $hive.OpenSubKey($dumpsPath, $true)
    try {
        if ($global) { $global.DeleteSubKey($name, $false) }
    } finally { if ($global) { $global.Dispose() } }
    if (-not $hadGlobalKey -and $global) {
        $hive.DeleteSubKey($dumpsPath, $false)
    }
    & $policy -ExecutableNames $name -CurrentUserForTest -Remove
    $hive.Dispose()
}
