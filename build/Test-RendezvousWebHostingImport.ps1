[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath 'Cert:\LocalMachine\WebHosting')) {
    throw 'This native control requires an existing LocalMachine\WebHosting store.'
}
$root = Split-Path $PSScriptRoot -Parent
$import = Join-Path $root 'installer/Import-RendezvousWebHostingCertificate.ps1'
$grant = Join-Path $root 'installer/Grant-RendezvousCertificate.ps1'
$dnsName = 'peersync-import-' + [guid]::NewGuid().ToString('N') + '.invalid'
$thumb = $null
$passed = $false

try {
    $created = New-SelfSignedCertificate -Type SSLServerAuthentication `
        -Subject "CN=$dnsName" -DnsName $dnsName `
        -CertStoreLocation 'Cert:\LocalMachine\My' `
        -KeyAlgorithm RSA -KeyLength 2048 -KeyExportPolicy NonExportable `
        -NotAfter (Get-Date).AddDays(1)
    $thumb = $created.Thumbprint
    Move-Item -LiteralPath "Cert:\LocalMachine\My\$thumb" `
        -Destination 'Cert:\LocalMachine\WebHosting'
    if (Test-Path -LiteralPath "Cert:\LocalMachine\My\$thumb") {
        throw 'The isolated test certificate remained in LocalMachine\My.'
    }

    $imported = @(& $import -DnsName $dnsName)
    if ($imported.Count -ne 1 -or $imported[0] -ne "CERTIFICATE_IMPORTED $thumb") {
        throw 'The WebHosting certificate was not imported into My.'
    }
    $selected = @(& $grant -DnsName $dnsName -CheckOnly)
    if ($selected.Count -ne 1 -or $selected[0] -ne "CERTIFICATE $thumb") {
        throw 'The imported machine key did not pass the certificate grant preflight.'
    }
    if (@(& $import -DnsName $dnsName).Count -ne 0) {
        throw 'The second import was not idempotent.'
    }
    $passed = $true
} finally {
    if ($thumb) {
        $myPath = "Cert:\LocalMachine\My\$thumb"
        $webPath = "Cert:\LocalMachine\WebHosting\$thumb"
        if (Test-Path -LiteralPath $myPath) { Remove-Item -LiteralPath $myPath }
        if (Test-Path -LiteralPath $webPath) {
            Remove-Item -LiteralPath $webPath -DeleteKey
        }
    }
}

if (-not $passed -or (Test-Path -LiteralPath "Cert:\LocalMachine\My\$thumb") -or
    (Test-Path -LiteralPath "Cert:\LocalMachine\WebHosting\$thumb")) {
    throw 'The isolated WebHosting import control did not complete and clean up.'
}
Write-Output 'WEBHOSTING_IMPORT_PASSED'
