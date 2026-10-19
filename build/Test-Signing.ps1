[CmdletBinding()]
param([ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$source = Join-Path $root "artifacts/publish/$Runtime/QuicBasics/QuicBasics.exe"
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Publish the $Runtime probe before running the signing smoke test" }
$output = Join-Path $root "artifacts/signing/$Runtime"
New-Item -ItemType Directory -Force $output | Out-Null
$signed = Join-Path $output 'QuicBasics-signed-test.exe'
$tampered = Join-Path $output 'QuicBasics-tampered-test.exe'
$certificate = $null
$report = [ordered]@{
    status = 'started'
    runtime = $Runtime
    sourceRevision = (& git -C $root rev-parse HEAD)
    workingTreeDirty = (@(& git -C $root status --porcelain).Count -gt 0)
    publicReleaseSigning = $false
    privateKeyExported = $false
}
try {
    # A fresh, non-exportable test identity is generated on each runner. No user signing key is needed.
    $certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=PeerSync CI Signing Smoke' `
        -CertStoreLocation Cert:\LocalMachine\My -KeyAlgorithm RSA -KeyLength 3072 `
        -HashAlgorithm SHA256 -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddDays(1)
    $report.thumbprint = $certificate.Thumbprint
    $publicCertificate = Join-Path $output 'PeerSync-CI-test.cer'
    Export-Certificate -Cert $certificate -FilePath $publicCertificate | Out-Null
    # Use the machine-store API: CurrentUser Root import can display a consent dialog.
    # This smoke already requires an elevated runner for its LocalMachine signing key.
    $publicOnly = Get-PfxCertificate -FilePath $publicCertificate
    $trustStore = [Security.Cryptography.X509Certificates.X509Store]::new('Root', 'LocalMachine')
    try {
        $trustStore.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        $trustStore.Add($publicOnly)
    } finally { $trustStore.Dispose(); $publicOnly.Dispose() }
    Copy-Item -LiteralPath $source -Destination $signed -Force
    & "$PSScriptRoot/Sign.ps1" -Path $signed -CertificateThumbprint $certificate.Thumbprint
    $signature = Get-AuthenticodeSignature -LiteralPath $signed
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint `
        -or $null -eq $signature.TimeStamperCertificate) {
        throw 'The smoke EXE must have a valid, timestamped signature from the temporary test certificate'
    }
    $report.signatureStatus = $signature.Status.ToString()
    $report.timestamped = $true
    $report.signedSha256 = (Get-FileHash -LiteralPath $signed -Algorithm SHA256).Hash

    # Change a byte in the first PE section, outside the excluded checksum and certificate table.
    $bytes = [IO.File]::ReadAllBytes($signed)
    $peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
    $optionalHeaderSize = [BitConverter]::ToUInt16($bytes, $peOffset + 20)
    $section = $peOffset + 24 + $optionalHeaderSize
    $rawOffset = [BitConverter]::ToInt32($bytes, $section + 20)
    if ($rawOffset -le 0 -or $rawOffset -ge $bytes.Length) { throw 'Invalid PE section offset in the smoke EXE' }
    $bytes[$rawOffset] = $bytes[$rawOffset] -bxor 1
    [IO.File]::WriteAllBytes($tampered, $bytes)
    $tamperSignature = Get-AuthenticodeSignature -LiteralPath $tampered
    $report.tamperStatus = $tamperSignature.Status.ToString()
    if ($tamperSignature.Status -ne 'HashMismatch') { throw 'Authenticode did not reject the deliberately changed EXE' }
    $report.status = 'passed'
}
catch {
    $report.status = 'failed'
    $report.errorType = $_.Exception.GetType().Name
    throw
}
finally {
    if ($null -ne $certificate) {
        $thumbprint = $certificate.Thumbprint
        $trustPath = "Cert:\LocalMachine\Root\$thumbprint"
        $privatePath = "Cert:\LocalMachine\My\$thumbprint"
        try {
            if (Test-Path -LiteralPath $trustPath) { Remove-Item -LiteralPath $trustPath -Force }
            if (Test-Path -LiteralPath $privatePath) { Remove-Item -LiteralPath $privatePath -DeleteKey -Force }
            $report.testTrustRemoved = -not (Test-Path -LiteralPath $trustPath)
            $report.privateCertificateRemoved = -not (Test-Path -LiteralPath $privatePath)
        } finally { $certificate.Dispose() }
    }
    if (Test-Path -LiteralPath $tampered) { Remove-Item -LiteralPath $tampered -Force }
    $report.completedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    $report | ConvertTo-Json | Set-Content (Join-Path $output 'summary.json')
}
Write-Host "Development signing smoke passed for $Runtime; test trust and private key removed."
