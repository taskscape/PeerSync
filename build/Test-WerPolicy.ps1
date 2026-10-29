[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$policy = Join-Path (Split-Path $PSScriptRoot -Parent) 'installer/Set-WerPolicy.ps1'
$suffix = [guid]::NewGuid().ToString('N')
$fresh = "PeerSync-Wer-Fresh-$suffix.exe"
$freshSecond = "PeerSync-Wer-FreshSecond-$suffix.exe"
$existing = "PeerSync-Wer-Existing-$suffix.exe"
$unexcluded = "PeerSync-Wer-Unexcluded-$suffix.exe"
$changed = "PeerSync-Wer-Changed-$suffix.exe"
$names = @($fresh, $freshSecond, $existing, $unexcluded, $changed)
$base = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
    [Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Default)
$excludedPath = 'SOFTWARE\Microsoft\Windows\Windows Error Reporting\ExcludedApplications'
$ownedPath = 'SOFTWARE\taskscape\PeerSync\WerExclusions'
function ValueOf([string]$path, [string]$name) {
    $key = $base.OpenSubKey($path)
    try { if ($key) { return $key.GetValue($name, $null) } }
    finally { if ($key) { $key.Dispose() } }
    return $null
}
function Require([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}
try {
    $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $policy -ExecutableNames "$fresh,$freshSecond" -CurrentUserForTest
    Require ($LASTEXITCODE -eq 0) 'Fresh setup-style WER command failed.'
    foreach ($name in @($fresh, $freshSecond)) {
        Require ($null -ne (ValueOf $excludedPath $name)) 'Fresh install did not create WER exclusion.'
        Require ((ValueOf $ownedPath $name) -eq 1) 'Fresh install did not mark ownership.'
    }
    & $policy -ExecutableNames "$fresh,$freshSecond" -CurrentUserForTest
    & $powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $policy -ExecutableNames "$fresh,$freshSecond" -CurrentUserForTest -Remove
    Require ($LASTEXITCODE -eq 0) 'Setup-style WER removal command failed.'
    foreach ($name in @($fresh, $freshSecond)) {
        Require ($null -eq (ValueOf $excludedPath $name)) 'Uninstall kept its owned exclusion.'
        Require ($null -eq (ValueOf $ownedPath $name)) 'Uninstall kept its ownership marker.'
    }

    Require ([PeerSync.Installer.WerPolicyNative]::WerAddExcludedApplication($existing, $false) -eq 0) 'Pre-existing setup failed.'
    & $policy -ExecutableNames $existing -CurrentUserForTest
    Require ($null -eq (ValueOf $ownedPath $existing)) 'Install claimed an administrator exclusion.'
    & $policy -ExecutableNames $existing -CurrentUserForTest -Remove
    Require ($null -ne (ValueOf $excludedPath $existing)) 'Uninstall removed an administrator exclusion.'

    $excluded = $base.OpenSubKey($excludedPath, $true)
    try { $excluded.SetValue($unexcluded, 0, [Microsoft.Win32.RegistryValueKind]::DWord) }
    finally { $excluded.Dispose() }
    $refused = $false
    try { & $policy -ExecutableNames $unexcluded -CurrentUserForTest } catch { $refused = $true }
    Require $refused 'Install accepted an administrator WER value that does not exclude reporting.'
    Require ($null -eq (ValueOf $ownedPath $unexcluded)) 'Install claimed an administrator WER override.'
    Require ((ValueOf $excludedPath $unexcluded) -eq 0) 'Install changed an administrator WER override.'

    & $policy -ExecutableNames $changed -CurrentUserForTest
    $excluded = $base.OpenSubKey($excludedPath, $true)
    try { $excluded.SetValue($changed, 0, [Microsoft.Win32.RegistryValueKind]::DWord) }
    finally { $excluded.Dispose() }
    $refused = $false
    try { & $policy -ExecutableNames $changed -CurrentUserForTest } catch { $refused = $true }
    Require $refused 'Upgrade overwrote an administrator-changed WER value.'
    Require ((ValueOf $excludedPath $changed) -eq 0) 'Upgrade changed administrator WER policy.'
    & $policy -ExecutableNames $changed -CurrentUserForTest -Remove
    Require ((ValueOf $excludedPath $changed) -eq 0) 'Uninstall overwrote a changed exclusion.'
    Require ($null -eq (ValueOf $ownedPath $changed)) 'Uninstall kept ownership after policy change.'
    'WER ownership controls passed.'
}
finally {
    foreach ($name in $names) {
        $key = $base.OpenSubKey($excludedPath, $true)
        try { if ($key) { $key.DeleteValue($name, $false) } }
        finally { if ($key) { $key.Dispose() } }
        $key = $base.OpenSubKey($ownedPath, $true)
        try { if ($key) { $key.DeleteValue($name, $false) } }
        finally { if ($key) { $key.Dispose() } }
    }
    $base.Dispose()
}
