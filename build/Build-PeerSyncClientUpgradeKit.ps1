[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string]$Version,
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$name = "PeerSync-Setup-$Version-x64.exe"
$setup = Join-Path $root "artifacts\installer\$name"
$manifestPath = "$setup.manifest.json"
$certificatePath = "$setup.cer"
$upgradeScript = Join-Path $PSScriptRoot 'field\Upgrade-PeerSyncClient.ps1'
$output = if ($OutputPath) { [IO.Path]::GetFullPath($OutputPath) } else {
    Join-Path $root "artifacts\deploy\PeerSync-client-upgrade-$Version-x64.zip"
}
foreach ($path in @($setup, $manifestPath, $certificatePath, $upgradeScript)) {
    if (-not [IO.File]::Exists($path)) { throw "Missing upgrade-kit input: $path" }
}
if ([IO.File]::Exists($output) -or [IO.Directory]::Exists($output)) {
    throw 'The upgrade-kit output already exists.'
}
if (-not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($output))) {
    throw 'The upgrade-kit output directory must already exist.'
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$setupHash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
$certificateHash = (Get-FileHash -LiteralPath $certificatePath -Algorithm SHA256).Hash
$signature = Get-AuthenticodeSignature -LiteralPath $setup
$publicCertificate = Get-PfxCertificate -FilePath $certificatePath
if ($manifest.version -ne $Version -or $manifest.architecture -ne 'x64' -or
    $manifest.signed -ne $true -or $manifest.localDevelopmentOnly -ne $true -or
    $manifest.publicReleaseSigning -ne $false -or
    $manifest.installerSha256 -ne $setupHash -or
    $manifest.developmentCertificateSha256 -ne $certificateHash -or
    $signature.Status -ne 'Valid' -or
    $signature.SignerCertificate.Thumbprint -ne $publicCertificate.Thumbprint) {
    throw 'The development installer, certificate or manifest failed validation.'
}

$launcher = @"
@echo off
setlocal
set "SCRIPT=%~dp0Upgrade-PeerSyncClient.ps1"
if not exist "%SCRIPT%" (
    echo Keep this launcher beside Upgrade-PeerSyncClient.ps1 and the setup files.
    pause
    exit /b 2
)
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" -Version "$Version"
set "RESULT=%ERRORLEVEL%"
echo PeerSync upgrade exit code: %RESULT%
pause
exit /b %RESULT%
"@
$readme = @"
# PeerSync $Version client upgrade (development x64)

This ZIP contains a development-signed client installer, its public-only
signing certificate and manifest, plus an upgrade launcher. It contains no
folder key, password, private signing key or Rendezvous server certificate.
It is for already joined test PCs; it is not a public release.

Installer source revision: $($manifest.sourceRevision)
Installer SHA-256: $setupHash
Public certificate SHA-256: $certificateHash
Signer thumbprint: $($publicCertificate.Thumbprint)

1. Verify the entire ZIP's SHA-256 against the value supplied separately by
   the build PC, then extract it to a local folder outside the synchronized
   PeerSync folder. Do not run setup from inside the ZIP.
2. In the PeerSync tray menu choose **Exit app**. Closing only the dashboard
   window leaves the tray process running.
3. Right-click `Run-PeerSync-Upgrade.cmd` and choose **Run as administrator**.
   The launcher checks setup/certificate hashes, Authenticode trust and
   installed version, runs silent setup, then checks the service and CLI.
4. Send back its `READY` or `FAILED` line and the named JSON report if one was
   produced. The setup log stays beside it for troubleshooting. If it reports reboot
   required, reboot and verify service status before continuing.

The same development signer was used for the 1.0.26.0 test installer. If this
PC does not trust it, the launcher stops before setup. Inspect the public
certificate's fingerprint against the value above before importing that
certificate into test-PC trust stores; never transfer a private key. Do not
use this development certificate as production fleet trust.
"@

function Add-TextEntry([IO.Compression.ZipArchive]$archive, [string]$entryName,
    [string]$value) {
    $entry = $archive.CreateEntry($entryName, [IO.Compression.CompressionLevel]::Optimal)
    $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
    try { $writer.Write($value) } finally { $writer.Dispose() }
}

$stream = [IO.File]::Open($output, [IO.FileMode]::CreateNew)
try {
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        foreach ($entry in @(
            @{ Name = $name; Path = $setup },
            @{ Name = "$name.manifest.json"; Path = $manifestPath },
            @{ Name = "$name.cer"; Path = $certificatePath },
            @{ Name = 'Upgrade-PeerSyncClient.ps1'; Path = $upgradeScript }
        )) {
            $destination = $zip.CreateEntry($entry.Name, [IO.Compression.CompressionLevel]::NoCompression)
            $input = [IO.File]::OpenRead($entry.Path)
            $target = $destination.Open()
            try { $input.CopyTo($target) }
            finally { $target.Dispose(); $input.Dispose() }
        }
        Add-TextEntry $zip 'Run-PeerSync-Upgrade.cmd' $launcher
        Add-TextEntry $zip 'README.md' $readme
    } finally { $zip.Dispose() }
} finally { $stream.Dispose() }

[pscustomobject]@{
    package = $output
    bytes = ([IO.FileInfo]::new($output)).Length
    sha256 = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
    sourceRevision = $manifest.sourceRevision
    setupSha256 = $setupHash
}
