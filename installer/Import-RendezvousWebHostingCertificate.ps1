[CmdletBinding()]
param([string]$DnsName = 'sync.taskscape.com')

$ErrorActionPreference = 'Stop'
$grant = Join-Path $PSScriptRoot 'Grant-RendezvousCertificate.ps1'
if (-not (Test-Path -LiteralPath 'Cert:\LocalMachine\WebHosting')) { return }

function Get-UsableThumbprint([string]$storeName) {
    try {
        $lines = @(& $grant -DnsName $DnsName -Store $storeName -CheckOnly)
        $line = @($lines | Where-Object { $_ -match '^CERTIFICATE [0-9A-Fa-f]{40}$' })
        if ($line.Count -ne 1) { return $null }
        return ($line[0] -split ' ')[1]
    } catch {
        # An absent or stale certificate in either store must not prevent the
        # other store from supplying the current server identity.
        return $null
    }
}

$webThumb = Get-UsableThumbprint 'WebHosting'
if (-not $webThumb) { return }
$webCertificate = Get-Item -LiteralPath "Cert:\LocalMachine\WebHosting\$webThumb"
$myThumb = Get-UsableThumbprint 'My'
if ($myThumb) {
    $myCertificate = Get-Item -LiteralPath "Cert:\LocalMachine\My\$myThumb"
    if ($myCertificate.NotAfter -ge $webCertificate.NotAfter) { return }
}

# Copy the certificate context and its existing machine-key association. A
# public .cer import would lose that association; never export the private key.
$store = [Security.Cryptography.X509Certificates.X509Store]::new(
    'My', [Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
try {
    $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    $store.Add($webCertificate)
} finally {
    $store.Close()
}

$importedThumb = Get-UsableThumbprint 'My'
if ($importedThumb -ne $webThumb) {
    throw 'The renewed WebHosting certificate was not usable in LocalMachine\My after import.'
}
Write-Output "CERTIFICATE_IMPORTED $webThumb"
