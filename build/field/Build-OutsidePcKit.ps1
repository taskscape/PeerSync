[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$name = "PeerSync-Setup-$Version-x64.exe"
$setup = Join-Path $root "artifacts/installer/$name"
$certificate = "$setup.cer"
$manifestPath = "$setup.manifest.json"
$collector = Join-Path $PSScriptRoot 'Collect-PeerSync-Diagnostics.cmd'
$probe = Join-Path $PSScriptRoot 'Probe-OutsideNetwork.ps1'
foreach ($path in @($setup, $certificate, $manifestPath, $collector, $probe)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing kit input: $path" }
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$setupHash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
$certificateHash = (Get-FileHash -LiteralPath $certificate -Algorithm SHA256).Hash
$signature = Get-AuthenticodeSignature -LiteralPath $setup
$publicCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificate)
try {
    if ($manifest.version -ne $Version -or $manifest.architecture -ne 'x64' -or
        $manifest.signed -ne $true -or $manifest.localDevelopmentOnly -ne $true -or
        $manifest.installerSha256 -ne $setupHash -or
        $manifest.developmentCertificateSha256 -ne $certificateHash -or
        $signature.Status -ne 'Valid' -or
        $signature.SignerCertificate.Thumbprint -ne $publicCertificate.Thumbprint) {
        throw 'Installer, manifest, certificate, or Authenticode signature did not match.'
    }
} finally {
    $publicCertificate.Dispose()
}

$readme = @'
# PeerSync outside-network test PC (@VERSION@, x64)

This development-only kit contains the PeerSync client setup, its public
development signing certificate, a diagnostic collector and a read-only
network probe. It contains no private key, folder key or server installer.

Source revision: @SOURCE@
Setup SHA-256: @SETUP_HASH@
Public certificate SHA-256: @CERT_HASH@

1. Extract the entire ZIP on the third Windows x64 PC. For the independent
   network check, use the mobile hotspot and disconnect Ethernet or another
   route to the first two PCs.
2. Open PowerShell as Administrator in the extracted folder and check:

       Get-FileHash '.\@SETUP@' -Algorithm SHA256
       Get-FileHash '.\@SETUP@.cer' -Algorithm SHA256

   Compare both results with the hashes above. If this development signer is
   not already trusted on this test PC, import only the included public cert:

       Import-Certificate -FilePath '.\@SETUP@.cer' -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
       Import-Certificate -FilePath '.\@SETUP@.cer' -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null
       (Get-AuthenticodeSignature '.\@SETUP@').Status

   Continue only when the signature reports Valid. Remove this development
   certificate's trust after the test. It is unrelated to the public server
   TLS certificate.
3. Run the setup from the same elevated PowerShell:

       & '.\@SETUP@'
       Get-Service PeerSync

   The installer closes an open PeerSync dashboard if necessary. Choosing
   Exit app from its tray menu first is gentler when that is convenient.
   Keep the existing joined folder; no new folder key is needed for an upgrade.
4. Run the read-only independent-network check:

       powershell.exe -NoProfile -ExecutionPolicy Bypass -File '.\Probe-OutsideNetwork.ps1'

   Its HTTP/2 and HTTP/3 probe must both be reachable and correlated, with
   PROBE_EXIT=0. This establishes central reachability, not file convergence.
5. Double-click Collect-PeerSync-Diagnostics.cmd after a connection problem.
   It writes a sanitized PeerSync-diagnostics-*.zip beside the collector.
   Transfer that ZIP to the first PC by a trusted channel even if PeerSync
   folder sync is stalled. Review the ZIP before sharing it outside the test
   group. The collector needs no folder key or elevated shell.

If the PC has not joined yet, use the PeerSync dashboard and transfer the
folder key privately. Each PC creates its own device identity. Check an actual
new file in both directions to prove sync; the network probe alone is not
sufficient.
'@
$readme = $readme.Replace('@VERSION@', $Version).Replace('@SOURCE@', $manifest.sourceRevision).
    Replace('@SETUP_HASH@', $setupHash).Replace('@CERT_HASH@', $certificateHash).
    Replace('@SETUP@', $name)

$output = Join-Path $root "artifacts/deploy/PeerSync-OutsidePC-$Version-x64-upgrade.zip"
if (Test-Path -LiteralPath $output) { throw "Refusing to overwrite an existing kit: $output" }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($output, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($item in @(
        @{ Path = $setup; Name = $name },
        @{ Path = $certificate; Name = "$name.cer" },
        @{ Path = $manifestPath; Name = "$name.manifest.json" },
        @{ Path = $collector; Name = 'Collect-PeerSync-Diagnostics.cmd' },
        @{ Path = $probe; Name = 'Probe-OutsideNetwork.ps1' }
    )) {
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive, $item.Path, $item.Name, [IO.Compression.CompressionLevel]::Optimal)
    }
    $entry = $archive.CreateEntry('README.md', [IO.Compression.CompressionLevel]::Optimal)
    $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
    try { $writer.Write($readme) } finally { $writer.Dispose() }
} finally {
    $archive.Dispose()
}

$zipHash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
"KIT $output"
"SHA256 $zipHash"
