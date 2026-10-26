[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string]$Version,
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$version = $Version
$installer = Join-Path $root 'artifacts\installer'
$output = if ($OutputPath) { [IO.Path]::GetFullPath($OutputPath) } else {
    Join-Path $root "artifacts\deploy\PeerSync-P5-field-kit-$version.zip"
}
$client = "PeerSync-Setup-$version-x64.exe"
$central = "PeerSync-Rendezvous-Setup-$version-x64.exe"
$entries = [ordered]@{
    "client/$client" = Join-Path $installer $client
    "client/$client.manifest.json" = Join-Path $installer "$client.manifest.json"
    "client/$client.cer" = Join-Path $installer "$client.cer"
    "server/$central" = Join-Path $installer $central
    "server/$central.manifest.json" = Join-Path $installer "$central.manifest.json"
    "server/$central.cer" = Join-Path $installer "$central.cer"
    'tools/Allow-PeerSync-TestTraffic.ps1' = Join-Path $root 'build\Allow-PeerSync-TestTraffic.ps1'
    'tools/Test-ForcedRelayPeer.ps1' = Join-Path $root 'build\field\Test-ForcedRelayPeer.ps1'
    'tools/Measure-PeerSyncTree.ps1' = Join-Path $root 'build\Measure-PeerSyncTree.ps1'
    'tools/Get-PeerSyncTestTreeContentBytes.ps1' = Join-Path $root 'build\Get-PeerSyncTestTreeContentBytes.ps1'
    'tools/New-PeerSyncGateCorpus.ps1' = Join-Path $root 'build\New-PeerSyncGateCorpus.ps1'
    'tools/Invoke-PeerSyncSoakOperations.ps1' = Join-Path $root 'build\Invoke-PeerSyncSoakOperations.ps1'
    'tools/Invoke-PeerSyncSoakCaptureLoop.ps1' = Join-Path $root 'build\Invoke-PeerSyncSoakCaptureLoop.ps1'
    'tools/Compare-PeerSyncSoakReports.ps1' = Join-Path $root 'build\Compare-PeerSyncSoakReports.ps1'
    'docs/rendezvous-operations.md' = Join-Path $root 'docs\rendezvous-operations.md'
    'docs/diagnostics.md' = Join-Path $root 'docs\diagnostics.md'
    'docs/user-guide.md' = Join-Path $root 'docs\user-guide.md'
    'docs/physical-soak.md' = Join-Path $root 'docs\physical-soak.md'
}
foreach ($path in $entries.Values) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing field kit input: $path" }
}
$clientManifest = Get-Content $entries["client/$client.manifest.json"] -Raw | ConvertFrom-Json
$centralManifest = Get-Content $entries["server/$central.manifest.json"] -Raw | ConvertFrom-Json
if ($clientManifest.sourceRevision -ne $centralManifest.sourceRevision -or
    $clientManifest.version -ne $version -or $centralManifest.version -ne $version -or
    -not $clientManifest.localDevelopmentOnly -or -not $centralManifest.localDevelopmentOnly) {
    throw 'The client and server development packages do not match.'
}
if ((Get-FileHash $entries["client/$client"] -Algorithm SHA256).Hash -ne $clientManifest.installerSha256 -or
    (Get-FileHash $entries["server/$central"] -Algorithm SHA256).Hash -ne $centralManifest.installerSha256) {
    throw 'Installer manifest hash mismatch.'
}
$certificateHash = (Get-FileHash $entries["client/$client.cer"] -Algorithm SHA256).Hash
$treeToolHash = (Get-FileHash $entries['tools/Measure-PeerSyncTree.ps1'] -Algorithm SHA256).Hash
$treeBudgetHash = (Get-FileHash $entries['tools/Get-PeerSyncTestTreeContentBytes.ps1'] -Algorithm SHA256).Hash
$corpusToolHash = (Get-FileHash $entries['tools/New-PeerSyncGateCorpus.ps1'] -Algorithm SHA256).Hash
$operationToolHash = (Get-FileHash $entries['tools/Invoke-PeerSyncSoakOperations.ps1'] -Algorithm SHA256).Hash
$captureLoopHash = (Get-FileHash $entries['tools/Invoke-PeerSyncSoakCaptureLoop.ps1'] -Algorithm SHA256).Hash
$soakCompareHash = (Get-FileHash $entries['tools/Compare-PeerSyncSoakReports.ps1'] -Algorithm SHA256).Hash
$forcedRelayHash = (Get-FileHash $entries['tools/Test-ForcedRelayPeer.ps1'] -Algorithm SHA256).Hash
if ($certificateHash -ne $clientManifest.developmentCertificateSha256 -or
    $certificateHash -ne (Get-FileHash $entries["server/$central.cer"] -Algorithm SHA256).Hash) {
    throw 'Development signing certificates differ.'
}
foreach ($setup in @($entries["client/$client"],$entries["server/$central"])) {
    if ((Get-AuthenticodeSignature -LiteralPath $setup).Status -ne 'Valid') { throw 'Invalid package signature.' }
}
if (Test-Path -LiteralPath $output) { throw 'Field kit already exists.' }
$readme = @"
# PeerSync $version development field kit

This kit is for test deployment, not a public release. Both setups use the
CN=PeerSync Development signing identity. The .cer files contain only its
public certificate; the private code-signing key is not included. A publicly
trusted release signer and release feed remain open plan requirements.

Client setup SHA-256: $($clientManifest.installerSha256)
Central setup SHA-256: $($centralManifest.installerSha256)
Installer source revision: $($clientManifest.sourceRevision)
Public development certificate SHA-256: $certificateHash
Tree-measurement tool SHA-256: $treeToolHash
Tree-budget preflight SHA-256: $treeBudgetHash
Mixed-corpus generator SHA-256: $corpusToolHash
Scoped operation tool SHA-256: $operationToolHash
Hourly capture loop SHA-256: $captureLoopHash
Soak comparison tool SHA-256: $soakCompareHash
Bounded forced-relay peer tool SHA-256: $forcedRelayHash

If an isolated test machine does not trust this development signer, review
the .cer fingerprint above before importing that *public* certificate to its
LocalMachine Root/TrustedPublisher stores. Do not import the development
signer as production fleet trust. No private key or production server TLS
certificate is included in this kit.

On the Windows Server, first provision a separate Server Authentication TLS
certificate for sync.taskscape.com in LocalMachine\My, point DNS to that
server, and allow TCP and UDP 99 in Windows Firewall and at the provider.
Install server/$central elevated, then run:

  & "`$env:ProgramFiles\PeerSync Rendezvous\Collect-DeploymentEvidence.ps1" -Role Rendezvous

On each test PC, install client/$client elevated. Give each PC its own device
identity, then Add/Join one folder through the dashboard or installed CLI.
Run the collector on an outside-network PC:

  & "`$env:ProgramFiles\PeerSync\tools\Collect-DeploymentEvidence.ps1" -Role Client -ProbeHost sync.taskscape.com:99

The commands print the path to a new local deployment-evidence.json and
sanitized ZIP. A nonzero exit retains the summary and any collected ZIP.
Compare file hashes on both peers and correlate the probe/relay session IDs
across all three bundles. Use tools/Allow-PeerSync-TestTraffic.ps1 only with
the real listener port and peer address, from elevated PowerShell, if scoped
firewall rules are needed before first listen. See the included operations,
diagnostics and user guides. The virtual-machine validation and remaining
physical gates are recorded in plan.md in the repository.

For the three-PC 9 GiB LAN corpus (10 GiB total test-tree ceiling), pause the test folder on the first PC,
then use tools/New-PeerSyncGateCorpus.ps1 as described in docs/physical-soak.md.
Keep tools/Get-PeerSyncTestTreeContentBytes.ps1 beside the corpus and soak
scripts; they check old content and reserve room before writing.
Resume the folder after generation and verify all peers with the tree tool.
For physical LAN/WAN soak evidence, use tools/Invoke-PeerSyncSoakOperations.ps1,
tools/Invoke-PeerSyncSoakCaptureLoop.ps1 and tools/Measure-PeerSyncTree.ps1
plus tools/Compare-PeerSyncSoakReports.ps1 with docs/physical-soak.md. These
tools keep the native synchronized root
path out of their shareable reports. They do not close a gate without the
required machines, elapsed time and network evidence.

The optional tools/Test-ForcedRelayPeer.ps1 blocks only the installed
PeerSync service's TCP/UDP listener and the explicitly listed remote peer
ports for a bounded interval, then restores the service and its own firewall
rules. A SYSTEM recovery task provides cleanup if the shell ends. Run its
read-only -PreflightOnly check first on each physical test PC; verify that
the supplied peer-port list includes every possible direct peer path, and
inspect selected Relay sessions before treating a transfer as forced relay.
It does not affect central TCP 99, and a partial/failed run is not a gate pass.
"@
$stream = [IO.File]::Open($output, [IO.FileMode]::CreateNew)
try {
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        foreach ($entry in $entries.GetEnumerator()) {
            $destination = $zip.CreateEntry($entry.Key, [IO.Compression.CompressionLevel]::NoCompression)
            $inputStream = [IO.File]::OpenRead($entry.Value)
            $outputStream = $destination.Open()
            try { $inputStream.CopyTo($outputStream) }
            finally { $outputStream.Dispose(); $inputStream.Dispose() }
        }
        $destination = $zip.CreateEntry('README.md', [IO.Compression.CompressionLevel]::NoCompression)
        $writer = [IO.StreamWriter]::new($destination.Open(), [Text.Encoding]::UTF8)
        try { $writer.Write($readme) } finally { $writer.Dispose() }
    } finally { $zip.Dispose() }
} finally { $stream.Dispose() }
[pscustomobject]@{ package = $output; bytes = (Get-Item $output).Length;
    sha256 = (Get-FileHash $output -Algorithm SHA256).Hash; sourceRevision = $clientManifest.sourceRevision }
