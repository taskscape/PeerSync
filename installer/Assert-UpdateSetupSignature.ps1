[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Path,
    [Parameter(Mandatory=$true)][ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string]$PublisherCertificateSha256
)

$ErrorActionPreference = 'Stop'
$signature = Get-AuthenticodeSignature -LiteralPath $Path
if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate) {
    throw 'Update setup Authenticode validation failed.'
}

$sha = [Security.Cryptography.SHA256]::Create()
try {
    $publisherHash = [BitConverter]::ToString(
        $sha.ComputeHash($signature.SignerCertificate.RawData)).Replace('-', '')
}
finally { $sha.Dispose() }
if (-not [string]::Equals($publisherHash, $PublisherCertificateSha256,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Update setup publisher does not match the administrator pin.'
}

# Get-AuthenticodeSignature checks the PE signature and platform trust. Require
# a separate current code-signing chain with online revocation for every
# non-root certificate; an unknown or unavailable revocation status fails closed.
$chain = [Security.Cryptography.X509Certificates.X509Chain]::new()
try {
    $chain.ChainPolicy.RevocationMode = [Security.Cryptography.X509Certificates.X509RevocationMode]::Online
    $chain.ChainPolicy.RevocationFlag = [Security.Cryptography.X509Certificates.X509RevocationFlag]::ExcludeRoot
    $chain.ChainPolicy.VerificationFlags = [Security.Cryptography.X509Certificates.X509VerificationFlags]::NoFlag
    $chain.ChainPolicy.UrlRetrievalTimeout = [TimeSpan]::FromSeconds(20)
    [void]$chain.ChainPolicy.ApplicationPolicy.Add(
        [Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3')) # Code Signing
    if (-not $chain.Build($signature.SignerCertificate)) {
        throw 'Update setup publisher chain or revocation validation failed.'
    }
}
finally { $chain.Dispose() }
