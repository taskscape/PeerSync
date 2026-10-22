[CmdletBinding()]
param(
    [string]$DnsName = 'sync.taskscape.com',
    [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($DnsName) -or $DnsName.Contains('*')) {
    throw 'A specific certificate DNS name is required.'
}
$now = [DateTime]::UtcNow
$serverAuth = '1.3.6.1.5.5.7.3.1'
$certificate = Get-ChildItem Cert:\LocalMachine\My |
    Where-Object {
        $_.HasPrivateKey -and $_.NotBefore.ToUniversalTime() -le $now -and
        $_.NotAfter.ToUniversalTime() -gt $now -and
        $_.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::DnsName, $false) -eq $DnsName -and
        @($_.EnhancedKeyUsageList | ForEach-Object { $_.ObjectId }) -contains $serverAuth
    } | Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $certificate) { throw "No current LocalMachine\My Server Authentication certificate for $DnsName has an accessible private key." }

$rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
$ecdsa = if (-not $rsa) {
    [Security.Cryptography.X509Certificates.ECDsaCertificateExtensions]::GetECDsaPrivateKey($certificate)
} else { $null }
try {
    $key = if ($rsa) { $rsa } else { $ecdsa }
    if (-not $key) { throw 'The server certificate must use RSA or ECDSA.' }
    if ($key -is [Security.Cryptography.RSACng] -or $key -is [Security.Cryptography.ECDsaCng]) {
        $name = $key.Key.UniqueName
        $base = Join-Path $env:ProgramData 'Microsoft\Crypto\Keys'
    } elseif ($key -is [Security.Cryptography.RSACryptoServiceProvider]) {
        if (-not $key.CspKeyContainerInfo.MachineKeyStore) { throw 'The certificate key is not in the machine store.' }
        $name = $key.CspKeyContainerInfo.UniqueKeyContainerName
        $base = Join-Path $env:ProgramData 'Microsoft\Crypto\RSA\MachineKeys'
    } else { throw "Unsupported private-key provider: $($key.GetType().Name)" }
    if ([string]::IsNullOrWhiteSpace($name) -or $name -match '[\\/:]') {
        throw 'The certificate key container name is invalid.'
    }
    $keyPath = Join-Path $base $name
    if (-not (Test-Path -LiteralPath $keyPath -PathType Leaf)) {
        throw 'The certificate private-key file was not found in the machine key store.'
    }
    if (-not $CheckOnly) {
        & icacls.exe $keyPath /grant 'NT SERVICE\PeerSyncRendezvous:R' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Granting the service access to the certificate private key failed.' }
    }
    Write-Output "CERTIFICATE $($certificate.Thumbprint)"
} finally {
    if ($rsa) { $rsa.Dispose() }
    if ($ecdsa) { $ecdsa.Dispose() }
}
