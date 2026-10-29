[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExecutableNames,
    [switch]$Remove,
    [switch]$CurrentUserForTest
)
$ErrorActionPreference = 'Stop'
$names = $ExecutableNames.Split(',')
if ($names.Count -eq 0 -or $names.Count -gt 8 -or
    @($names | Where-Object { $_ -notmatch '^[A-Za-z0-9.-]+\.exe$' -or $_.Length -gt 260 }).Count -ne 0) {
    throw 'WER executable names must be a short comma-separated list of executable basenames.'
}

# The installer calls this elevated for the machine. The user scope exists only
# so the ownership behavior can be tested without changing machine policy.
if (-not ('PeerSync.Installer.WerPolicyNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace PeerSync.Installer {
    public static class WerPolicyNative {
        [DllImport("wer.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int WerAddExcludedApplication(string exeName, [MarshalAs(UnmanagedType.Bool)] bool allUsers);
        [DllImport("wer.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int WerRemoveExcludedApplication(string exeName, [MarshalAs(UnmanagedType.Bool)] bool allUsers);
    }
}
'@
}

$hive = if ($CurrentUserForTest) { [Microsoft.Win32.RegistryHive]::CurrentUser } else { [Microsoft.Win32.RegistryHive]::LocalMachine }
$view = if ($CurrentUserForTest) { [Microsoft.Win32.RegistryView]::Default } else { [Microsoft.Win32.RegistryView]::Registry64 }
$allUsers = -not $CurrentUserForTest
$exclusionPath = 'SOFTWARE\Microsoft\Windows\Windows Error Reporting\ExcludedApplications'
$ownershipPath = 'SOFTWARE\taskscape\PeerSync\WerExclusions'
$registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, $view)
try {
    foreach ($name in $names) {
        $owned = $false
        $exists = $false
        $value = $null
        $owner = $registry.OpenSubKey($ownershipPath, $true)
        try { $owned = $null -ne $owner -and $owner.GetValue($name, $null) -eq 1 }
        finally { if ($owner) { $owner.Dispose() } }
        $exclusions = $registry.OpenSubKey($exclusionPath, $false)
        try {
            if ($exclusions) {
                $value = $exclusions.GetValue($name, $null)
                $exists = $null -ne $value
            }
        }
        finally { if ($exclusions) { $exclusions.Dispose() } }

        if ($Remove) {
            if (-not $owned) { continue }
            # A changed or missing value belongs to the current administrator;
            # relinquish our marker without changing that policy.
            if ($exists -and $value -eq 1) {
                $result = [PeerSync.Installer.WerPolicyNative]::WerRemoveExcludedApplication($name, $allUsers)
                if ($result -ne 0) { throw ('WER removal failed for {0}: 0x{1:X8}' -f $name, ([int64]$result -band [int64]4294967295)) }
            }
            $owner = $registry.OpenSubKey($ownershipPath, $true)
            try { if ($owner) { $owner.DeleteValue($name, $false) } }
            finally { if ($owner) { $owner.Dispose() } }
            continue
        }

        # An existing administrator exclusion remains administrator-owned.
        if ($exists -and -not $owned) {
            if ($value -ne 1) { throw "An administrator WER value for $name does not exclude reporting." }
            continue
        }
        if ($owned -and $exists -and $value -ne 1) {
            throw "An administrator changed the WER value for $name; repair that policy before upgrading."
        }
        if (-not $exists -or $value -ne 1) {
            $result = [PeerSync.Installer.WerPolicyNative]::WerAddExcludedApplication($name, $allUsers)
            if ($result -ne 0) { throw ('WER exclusion failed for {0}: 0x{1:X8}' -f $name, ([int64]$result -band [int64]4294967295)) }
        }
        $verify = $registry.OpenSubKey($exclusionPath, $false)
        try {
            if ($null -eq $verify -or $verify.GetValue($name, $null) -ne 1) {
                throw "WER did not retain the exclusion for $name."
            }
        }
        finally { if ($verify) { $verify.Dispose() } }
        $owner = $registry.CreateSubKey($ownershipPath, $true)
        try { $owner.SetValue($name, 1, [Microsoft.Win32.RegistryValueKind]::DWord) }
        finally { $owner.Dispose() }
    }
}
finally { $registry.Dispose() }
