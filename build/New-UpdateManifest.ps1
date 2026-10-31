[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SetupPath,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory)][string]$SetupUrl,
    [Parameter(Mandatory)][string]$CertificateThumbprint,
    [Parameter(Mandatory)][string]$OutputPath,
    [ValidateRange(1, 30)][int]$ValidDays = 7
)

$ErrorActionPreference = 'Stop'
$setup = Get-Item -LiteralPath $SetupPath -ErrorAction Stop
if ($setup.PSIsContainer -or $setup.Length -le 0 -or $setup.Length -gt 1GB) {
    throw 'The setup file must be nonempty and at most 1 GiB.'
}
$uri = $null
if (-not [Uri]::TryCreate($SetupUrl, [UriKind]::Absolute, [ref]$uri) -or
    $uri.Scheme -ne [Uri]::UriSchemeHttps -or $uri.UserInfo.Length -ne 0 -or
    $uri.Fragment.Length -ne 0 -or $SetupUrl.Length -gt 2048) {
    throw 'The setup URL must be a direct HTTPS URL without credentials or a fragment.'
}
$expectedName = 'PeerSync-Setup-{0}-' -f $Version
if (-not $setup.Name.StartsWith($expectedName, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The setup filename does not match the manifest version.'
}
$thumbprint = $CertificateThumbprint.Replace(' ', '').ToUpperInvariant()
if ($thumbprint -notmatch '^[0-9A-F]{40}$') { throw 'The certificate thumbprint is invalid.' }
$certificate = Get-Item -LiteralPath ('Cert:\LocalMachine\My\' + $thumbprint) -ErrorAction Stop
if (-not $certificate.HasPrivateKey) { throw 'The publisher certificate has no private key.' }
$authenticode = Get-AuthenticodeSignature -LiteralPath $setup.FullName
if ($authenticode.Status -ne 'Valid' -or $null -eq $authenticode.SignerCertificate -or
    -not [Security.Cryptography.CryptographicOperations]::FixedTimeEquals(
        $authenticode.SignerCertificate.RawData, $certificate.RawData)) {
    throw 'The setup Authenticode signature is invalid or uses another publisher.'
}

$rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
if ($null -eq $rsa) { throw 'The publisher certificate does not have an RSA signing key.' }
try {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $stream = [IO.File]::OpenRead($setup.FullName)
        try { $setupDigest = $sha.ComputeHash($stream) }
        finally { $stream.Dispose() }
    }
    finally { $sha.Dispose() }
    $payload = [ordered]@{
        version = $Version
        url = $uri.AbsoluteUri
        sha256 = [Convert]::ToHexString($setupDigest)
        size = [long]$setup.Length
        expiresUtcTicks = [DateTime]::UtcNow.AddDays($ValidDays).Ticks
    }
    $payloadBytes = [Text.UTF8Encoding]::new($false).GetBytes(($payload | ConvertTo-Json -Compress))
    $signature = $rsa.SignData($payloadBytes, [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.RSASignaturePadding]::Pss)
    $envelope = [ordered]@{
        payload = [Convert]::ToBase64String($payloadBytes)
        signature = [Convert]::ToBase64String($signature)
    }
    $output = [IO.Path]::GetFullPath($OutputPath)
    $outputDirectory = [IO.Path]::GetDirectoryName($output)
    if (-not [IO.Directory]::Exists($outputDirectory)) { throw 'The output directory does not exist.' }
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($envelope | ConvertTo-Json -Compress))
    if ($bytes.Length -gt 32KB) { throw 'The signed update manifest exceeds 32 KiB.' }
    $file = [IO.FileStream]::new($output, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write,
        [IO.FileShare]::None, 4096, [IO.FileOptions]::WriteThrough)
    try { $file.Write($bytes, 0, $bytes.Length); $file.Flush($true) }
    finally { $file.Dispose() }
    Write-Output "MANIFEST $output"
}
finally { $rsa.Dispose() }
