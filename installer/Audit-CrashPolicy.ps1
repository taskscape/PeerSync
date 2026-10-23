[CmdletBinding()]
param(
    [ValidateSet('Client', 'Rendezvous', 'All')][string]$Component = 'Client',
    [switch]$CurrentUserForTest,
    [string]$ExecutableNamesForTest
)
$ErrorActionPreference = 'Stop'
if ($ExecutableNamesForTest -and -not $CurrentUserForTest) {
    throw 'Custom executable names are allowed only in the current-user test scope.'
}
$names = if ($ExecutableNamesForTest) {
    $ExecutableNamesForTest.Split(',')
} elseif ($Component -eq 'Client') {
    @('PeerSync.Service.exe', 'PeerSync.exe', 'psp.exe')
} elseif ($Component -eq 'Rendezvous') {
    @('PeerSync.Rendezvous.exe')
} else {
    @('PeerSync.Service.exe', 'PeerSync.exe', 'psp.exe', 'PeerSync.Rendezvous.exe')
}
if ($names.Count -eq 0 -or $names.Count -gt 8 -or
    @($names | Where-Object { $_ -notmatch '^[A-Za-z0-9.-]+\.exe$' -or $_.Length -gt 260 }).Count -ne 0) {
    throw 'Invalid executable basenames.'
}

$hive = if ($CurrentUserForTest) { [Microsoft.Win32.RegistryHive]::CurrentUser } else { [Microsoft.Win32.RegistryHive]::LocalMachine }
$view = if ($CurrentUserForTest) { [Microsoft.Win32.RegistryView]::Default } else { [Microsoft.Win32.RegistryView]::Registry64 }
$wer = 'SOFTWARE\Microsoft\Windows\Windows Error Reporting'
$registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, $view)
try {
    $excluded = $registry.OpenSubKey("$wer\ExcludedApplications")
    $localDumps = $registry.OpenSubKey("$wer\LocalDumps")
    $owned = $registry.OpenSubKey('SOFTWARE\taskscape\PeerSync\WerExclusions')
    try {
        $globalDumpValues = if ($localDumps) { @($localDumps.GetValueNames()) } else { @() }
        $executables = @(
            foreach ($name in $names) {
                $specific = $registry.OpenSubKey("$wer\LocalDumps\$name")
                try {
                    [pscustomobject]@{
                        name = $name
                        automaticReportsExcluded = ($null -ne $excluded -and $excluded.GetValue($name, $null) -eq 1)
                        localDumpsKeyPresent = ($null -ne $specific)
                        localDumpsConfigured = ($null -ne $specific -and @($specific.GetValueNames()).Count -ne 0)
                        ownedByPeerSyncInstaller = ($null -ne $owned -and $owned.GetValue($name, $null) -eq 1)
                    }
                } finally { if ($specific) { $specific.Dispose() } }
            }
        )
        [pscustomobject]@{
            schemaVersion = 1
            scope = $(if ($CurrentUserForTest) { 'CurrentUserTest' } else { 'LocalMachine64' })
            component = $Component
            allAutomaticReportsExcluded = @($executables | Where-Object { -not $_.automaticReportsExcluded }).Count -eq 0
            globalLocalDumpsKeyPresent = ($null -ne $localDumps)
            globalLocalDumpsConfigured = (@($globalDumpValues).Count -ne 0)
            anyPerExecutableLocalDumpsConfigured = @($executables | Where-Object { $_.localDumpsConfigured }).Count -ne 0
            executables = $executables
        } | ConvertTo-Json -Depth 4
    } finally {
        if ($owned) { $owned.Dispose() }
        if ($localDumps) { $localDumps.Dispose() }
        if ($excluded) { $excluded.Dispose() }
    }
} finally { $registry.Dispose() }
