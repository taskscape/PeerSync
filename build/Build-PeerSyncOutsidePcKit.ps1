[CmdletBinding()]
param(
    [string]$Version = '1.0.16.0',
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$setupName = "PeerSync-Setup-$Version-x64.exe"
$installerDir = Join-Path $root 'artifacts\installer'
$setup = Join-Path $installerDir $setupName
$certificate = "$setup.cer"
$manifestPath = "$setup.manifest.json"
$collector = Join-Path $root 'build\field\Collect-PeerSync-Diagnostics.cmd'
$output = if ($OutputPath) { [IO.Path]::GetFullPath($OutputPath) } else {
    Join-Path $root "artifacts\deploy\PeerSync-OutsidePC-$Version-x64.zip"
}

foreach ($path in @($setup, $certificate, $manifestPath, $collector)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing package input: $path" }
}
if (Test-Path -LiteralPath $output) { throw "Package already exists: $output" }

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$setupHash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
$certificateHash = (Get-FileHash -LiteralPath $certificate -Algorithm SHA256).Hash
if ($manifest.version -ne $Version -or $manifest.architecture -ne 'x64' -or
    -not $manifest.signed -or -not $manifest.localDevelopmentOnly -or
    $setupHash -ne $manifest.installerSha256 -or
    $certificateHash -ne $manifest.developmentCertificateSha256) {
    throw 'The setup, certificate, or manifest failed validation.'
}
if ((Get-AuthenticodeSignature -LiteralPath $setup).Status -ne 'Valid') {
    throw 'The client setup signature is invalid or untrusted on this build PC.'
}

$probe = @'
$ErrorActionPreference = 'Stop'
$cli = Join-Path $env:ProgramFiles 'PeerSync\cli\psp.exe'
if (-not [Environment]::Is64BitOperatingSystem) { throw 'This package requires x64 Windows.' }
if (-not (Test-Path -LiteralPath $cli -PathType Leaf)) { throw "Install PeerSync first: $cli" }

'ACTIVE_ADAPTERS'
Get-NetAdapter -Physical | Where-Object Status -eq 'Up' |
    Select-Object Name, InterfaceDescription, LinkSpeed | Format-Table -AutoSize
'ROUTE_TO_SERVER'
$serverAddress = Resolve-DnsName sync.taskscape.com -Type A |
    Where-Object IPAddress | Select-Object -First 1 -ExpandProperty IPAddress
"SERVER_ADDRESS=$serverAddress"
Find-NetRoute -RemoteIPAddress $serverAddress |
    Select-Object -First 1 IPAddress, InterfaceAlias, NextHop | Format-List
'RENDEZVOUS_PROBE'
& $cli probe sync.taskscape.com:99
$probeExit = $LASTEXITCODE
"PROBE_EXIT=$probeExit"
if ($probeExit -ne 0) { exit $probeExit }
'CLIENT_STATUS'
& $cli status --json
exit $LASTEXITCODE
'@

$readme = @"
# PeerSync outside-network test PC ($Version, x64)

This is a development test package for a third Windows x64 PC. It contains
only the PeerSync client setup, the public development signing certificate,
their manifest, a diagnostic collector, and a read-only network probe. It
contains no private key, folder key, or server setup.

Source revision: $($manifest.sourceRevision)
Setup SHA-256: $setupHash
Public certificate SHA-256: $certificateHash

If this PC is already joined and its outside-network probe passed earlier,
verify the setup in steps 2-3, upgrade in step 4, and collect diagnostics in
step 6. Keep the existing joined folder; no new folder key is needed.

1. Extract the entire ZIP to a local folder on the third PC. Connect that PC
   through the mobile hotspot or a genuinely separate internet connection.
   Disconnect its Ethernet or other path to the first two PCs for this test.
2. Open PowerShell as Administrator in the extracted folder. Check the setup
   and public-certificate hashes against the two values above:

   Get-FileHash '.\$setupName' -Algorithm SHA256
   Get-FileHash '.\$setupName.cer' -Algorithm SHA256

3. This development build has a self-signed code signer. On this isolated test
   PC, trust only the included public certificate, then check the setup:

   Import-Certificate -FilePath '.\$setupName.cer' -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
   Import-Certificate -FilePath '.\$setupName.cer' -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null
   Get-AuthenticodeSignature '.\$setupName' | Select-Object Status, SignerCertificate

   Continue only if Status is Valid. This certificate is for development
   testing; remove its trust after the test. It is unrelated to the public
   sync.taskscape.com TLS certificate.
4. From the same elevated PowerShell, run the client setup:

   & '.\$setupName'
   Get-Service PeerSync

   Let any bundled Visual C++ prerequisite finish. If it requests a reboot,
   reboot and rerun setup. The PeerSync service should report Running.
5. Run the read-only route and HTTP/2+HTTP/3 probe:

   powershell.exe -NoProfile -ExecutionPolicy Bypass -File '.\Probe-OutsideNetwork.ps1'

   Share the ACTIVE_ADAPTERS, ROUTE_TO_SERVER, RENDEZVOUS_PROBE and
   PROBE_EXIT output. The server-observed address may identify your mobile
   network. Do not share a folder key. Both tcp.reachable and udp.reachable
   must be true and correlated, and PROBE_EXIT must be 0.

6. After reproducing a connection problem, double-click
   Collect-PeerSync-Diagnostics.cmd in this extracted folder. It uses the
   installed CLI to create a sanitized PeerSync-diagnostics-*.zip beside the
   helper. The window remains open and reports any collection failure. Copy
   that ZIP to the first PC by a trusted transfer method even if folder sync
   is stalled. Review the ZIP before sharing it outside the test group. This
   helper needs no folder key or elevated shell.

If this PC has not yet joined, join the existing test folder using the dashboard
and the folder key through your trusted private channel. Each PC creates its
own device identity. A probe pass alone does not prove WAN synchronization.
"@

$parent = Split-Path -Parent $output
if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
}
$stream = [IO.File]::Open($output, [IO.FileMode]::CreateNew)
try {
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        foreach ($item in @(
            @{ Name = $setupName; Path = $setup },
            @{ Name = "$setupName.cer"; Path = $certificate },
            @{ Name = "$setupName.manifest.json"; Path = $manifestPath },
            @{ Name = 'Collect-PeerSync-Diagnostics.cmd'; Path = $collector }
        )) {
            $entry = $zip.CreateEntry($item.Name, [IO.Compression.CompressionLevel]::NoCompression)
            $inputStream = [IO.File]::OpenRead($item.Path)
            $outputStream = $entry.Open()
            try { $inputStream.CopyTo($outputStream) }
            finally { $outputStream.Dispose(); $inputStream.Dispose() }
        }
        foreach ($item in @(
            @{ Name = 'README.md'; Value = $readme },
            @{ Name = 'Probe-OutsideNetwork.ps1'; Value = $probe }
        )) {
            $entry = $zip.CreateEntry($item.Name, [IO.Compression.CompressionLevel]::Optimal)
            $writer = [IO.StreamWriter]::new($entry.Open(), [Text.Encoding]::UTF8)
            try { $writer.Write($item.Value) } finally { $writer.Dispose() }
        }
    } finally { $zip.Dispose() }
} finally { $stream.Dispose() }

[pscustomobject]@{
    package = $output
    bytes = (Get-Item -LiteralPath $output).Length
    sha256 = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
    sourceRevision = $manifest.sourceRevision
}
