[CmdletBinding(DefaultParameterSetName='Disable')]
param(
    [Parameter(Mandatory=$true, ParameterSetName='Enable')][switch]$Enable,
    [Parameter(Mandatory=$true, ParameterSetName='Enable')][string]$ManifestUrl,
    [Parameter(Mandatory=$true, ParameterSetName='Enable')][string]$PublisherCertificate,
    [Parameter(Mandatory=$true, ParameterSetName='Disable')][switch]$Disable
)

$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run update policy changes from elevated PowerShell.'
}
$directory = Join-Path $env:ProgramData 'PeerSync\update-policy'
$path = Join-Path $directory 'update-policy.json'
$info = Get-Item -LiteralPath $directory -ErrorAction Stop
if (-not $info.PSIsContainer -or ($info.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'The installed update-policy directory is invalid. Repair the PeerSync installation.'
}

if ($Enable) {
    $uri = $null
    if (-not [Uri]::TryCreate($ManifestUrl, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -ne [Uri]::UriSchemeHttps -or $uri.UserInfo.Length -ne 0 -or
        $uri.Fragment.Length -ne 0 -or $ManifestUrl.Length -gt 2048) {
        throw 'The update manifest URL must be a direct HTTPS URL without credentials or a fragment.'
    }
    $cert = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
        [IO.Path]::GetFullPath($PublisherCertificate))
    try {
        if ($cert.PublicKey.Oid.Value -ne '1.2.840.113549.1.1.1') {
            throw 'The update publisher certificate must have an RSA public key.'
        }
        $der = $cert.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert)
        if ($der.Length -gt 4096) { throw 'The update publisher certificate is too large.' }
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $digest = $sha.ComputeHash($der) }
        finally { $sha.Dispose() }
        $policy = [ordered]@{
            enabled = $true
            manifestUrl = $uri.AbsoluteUri
            publisherCertificate = [Convert]::ToBase64String($der)
            publisherCertificateSha256 = [BitConverter]::ToString($digest).Replace('-', '')
        }
    }
    finally { $cert.Dispose() }
} else {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { Write-Output 'Automatic updates are already disabled.'; exit 0 }
    if ((Get-Item -LiteralPath $path).Length -gt 16384) { throw 'The current update policy is oversized.' }
    $current = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    $policy = [ordered]@{
        enabled = $false
        manifestUrl = [string]$current.manifestUrl
        publisherCertificate = [string]$current.publisherCertificate
        publisherCertificateSha256 = [string]$current.publisherCertificateSha256
    }
}

$temporary = Join-Path $directory ([guid]::NewGuid().ToString('N') + '.tmp')
try {
    $content = $policy | ConvertTo-Json -Compress
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($content)
    if ($bytes.Length -gt 16384) { throw 'The update policy exceeds its size limit.' }
    $file = [IO.FileStream]::new($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write,
        [IO.FileShare]::None, 4096, [IO.FileOptions]::WriteThrough)
    try { $file.Write($bytes, 0, $bytes.Length); $file.Flush($true) }
    finally { $file.Dispose() }
    # A missing policy disables updates, so interrupted replacement fails closed.
    if (Test-Path -LiteralPath $path) { [IO.File]::Delete($path) }
    [IO.File]::Move($temporary, $path)
    Write-Output ($(if ($Enable) { 'Automatic updates enabled.' } else { 'Automatic updates disabled.' }))
}
finally { if (Test-Path -LiteralPath $temporary) { [IO.File]::Delete($temporary) } }
