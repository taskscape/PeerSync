[CmdletBinding()]
param(
    [string]$DnsName = 'sync.taskscape.com',
    [switch]$CheckOnly,
    [ValidateSet('My', 'WebHosting')][string]$Store = 'My'
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($DnsName) -or $DnsName.Contains('*')) {
    throw 'A specific certificate DNS name is required.'
}
$now = [DateTime]::UtcNow
$serverAuth = '1.3.6.1.5.5.7.3.1'
$candidates = @(Get-ChildItem "Cert:\LocalMachine\$Store" |
    Where-Object {
        $_.HasPrivateKey -and $_.NotBefore.ToUniversalTime() -le $now -and
        $_.NotAfter.ToUniversalTime() -gt $now -and
        $_.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::DnsName, $false) -eq $DnsName -and
        @($_.EnhancedKeyUsageList | ForEach-Object { $_.ObjectId }) -contains $serverAuth
    } | Sort-Object NotAfter -Descending)
$selected = $null
$challenge = [byte[]](1, 2, 3, 4, 5, 6, 7, 8)
foreach ($candidate in $candidates) {
    $rsa = $null
    $ecdsa = $null
    $public = $null
    try {
        $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($candidate)
        $ecdsa = if (-not $rsa) {
            [Security.Cryptography.X509Certificates.ECDsaCertificateExtensions]::GetECDsaPrivateKey($candidate)
        } else { $null }
        $key = if ($rsa) { $rsa } else { $ecdsa }
        if (-not $key) { throw 'The server certificate must use RSA or ECDSA.' }
        if ($key -is [Security.Cryptography.RSACng] -or $key -is [Security.Cryptography.ECDsaCng]) {
            if (-not $key.Key.IsMachineKey) { throw 'The certificate key is not in the machine store.' }
            $name = $key.Key.UniqueName
            $bases = @(Join-Path $env:ProgramData 'Microsoft\Crypto\Keys')
            if ($key -is [Security.Cryptography.RSACng]) {
                # Windows PowerShell 5.1 can expose a legacy CSP RSA key as RSACng.
                # Its file still lives in RSA\MachineKeys, not Crypto\Keys.
                $bases += Join-Path $env:ProgramData 'Microsoft\Crypto\RSA\MachineKeys'
            }
        } elseif ($key -is [Security.Cryptography.RSACryptoServiceProvider]) {
            if (-not $key.CspKeyContainerInfo.MachineKeyStore) { throw 'The certificate key is not in the machine store.' }
            $name = $key.CspKeyContainerInfo.UniqueKeyContainerName
            $bases = @(Join-Path $env:ProgramData 'Microsoft\Crypto\RSA\MachineKeys')
        } else { throw "Unsupported private-key provider: $($key.GetType().Name)" }
        if ([string]::IsNullOrWhiteSpace($name) -or $name -match '[\\/:]') {
            throw 'The certificate key container name is invalid.'
        }
        $keyPaths = @($bases | ForEach-Object { Join-Path $_ $name } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
        if ($keyPaths.Count -ne 1) { throw 'The certificate private-key file was not uniquely found in the machine key store.' }

        if ($rsa) {
            $public = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($candidate)
            $signature = $rsa.SignData($challenge, [Security.Cryptography.HashAlgorithmName]::SHA256,
                [Security.Cryptography.RSASignaturePadding]::Pkcs1)
            $valid = $public -and $public.VerifyData($challenge, $signature,
                [Security.Cryptography.HashAlgorithmName]::SHA256,
                [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        } else {
            $public = [Security.Cryptography.X509Certificates.ECDsaCertificateExtensions]::GetECDsaPublicKey($candidate)
            $signature = $ecdsa.SignData($challenge, [Security.Cryptography.HashAlgorithmName]::SHA256)
            $valid = $public -and $public.VerifyData($challenge, $signature,
                [Security.Cryptography.HashAlgorithmName]::SHA256)
        }
        if (-not $valid) { throw 'The private key does not match the certificate.' }
        $selected = [pscustomobject]@{ Certificate = $candidate; KeyPath = $keyPaths[0] }
        break
    } catch {
        # A stale certificate can claim HasPrivateKey even after its keyset was
        # removed. Try the next current matching certificate without exposing
        # its key container or retaining a misleading candidate.
    } finally {
        if ($public) { $public.Dispose() }
        if ($rsa) { $rsa.Dispose() }
        if ($ecdsa) { $ecdsa.Dispose() }
    }
}
if (-not $selected) {
    throw "No current LocalMachine\$Store Server Authentication certificate for $DnsName has a usable machine private key. Checked $($candidates.Count) matching certificate(s)."
}

if (-not $CheckOnly) {
    & icacls.exe $selected.KeyPath /grant 'NT SERVICE\PeerSyncRendezvous:R' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Granting the service access to the certificate private key failed.' }
}
Write-Output "CERTIFICATE $($selected.Certificate.Thumbprint)"
