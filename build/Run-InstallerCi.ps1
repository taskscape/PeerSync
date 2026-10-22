[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$tools = Join-Path $root 'artifacts/tools'
$inno = Join-Path $tools 'Inno7'
$download = Join-Path $tools 'innosetup-7.1.0-x64.exe'
$url = 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_0/innosetup-7.1.0-x64.exe'
New-Item -ItemType Directory -Path $tools -Force | Out-Null
if (-not (Test-Path -LiteralPath (Join-Path $inno 'ISCC.exe') -PathType Leaf)) {
    Invoke-WebRequest -Uri $url -OutFile $download
    $publisher = Get-AuthenticodeSignature -LiteralPath $download
    if ($publisher.Status -ne 'Valid' -or $publisher.SignerCertificate.Subject -notmatch '^CN=Pyrsys B\.V\.,') {
        throw 'Inno Setup download signature or publisher is invalid.'
    }
    $process = Start-Process -FilePath $download -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES',
        '/NORESTART', "/DIR=`"$inno`"") -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Inno Setup installation failed ($($process.ExitCode))." }
}
$iscc = Join-Path $inno 'ISCC.exe'
if (-not (Test-Path -LiteralPath $iscc -PathType Leaf)) { throw 'Inno Setup 7 ISCC.exe is missing.' }

$certificate = $null
try {
    $certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=PeerSync CI Installer Test' `
        -CertStoreLocation Cert:\LocalMachine\My -KeyAlgorithm RSA -KeyLength 3072 `
        -HashAlgorithm SHA256 -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddDays(1)
    $publicPath = Join-Path $tools 'PeerSync-CI-Installer-Test.cer'
    Export-Certificate -Cert $certificate -FilePath $publicPath | Out-Null
    $publicOnly = Get-PfxCertificate -FilePath $publicPath
    $trust = [Security.Cryptography.X509Certificates.X509Store]::new('Root', 'LocalMachine')
    try {
        $trust.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        $trust.Add($publicOnly)
    } finally { $trust.Dispose(); $publicOnly.Dispose() }

    foreach ($arch in @('x64', 'arm64')) {
        & (Join-Path $PSScriptRoot 'Build-Installer.ps1') -Arch $arch -Version 1.0.0 `
            -IsccPath $iscc -CertificateThumbprint $certificate.Thumbprint -DevelopmentSigning
        if ($LASTEXITCODE -ne 0) { throw "Installer build failed: $arch" }
        $setup = Join-Path $root "artifacts/installer/PeerSync-Setup-1.0.0.0-$arch.exe"
        $signature = Get-AuthenticodeSignature -LiteralPath $setup
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint `
            -or $null -eq $signature.TimeStamperCertificate) {
            throw "Installer signature verification failed: $arch"
        }
        $public = [Security.Cryptography.X509Certificates.X509Certificate2]::new("$setup.cer")
        try {
            if ($public.Thumbprint -ne $certificate.Thumbprint) {
                throw "Exported public certificate does not match the signer: $arch"
            }
        } finally { $public.Dispose() }
        $manifest = Get-Content -LiteralPath "$setup.manifest.json" -Raw | ConvertFrom-Json
        if ($manifest.developmentCertificateSha256 -ne
            (Get-FileHash -LiteralPath "$setup.cer" -Algorithm SHA256).Hash) {
            throw "Exported public certificate hash does not match the manifest: $arch"
        }
        $redist = Join-Path $root "publish/$arch/redist/vc_redist.exe"
        $redistSignature = Get-AuthenticodeSignature -LiteralPath $redist
        if ($redistSignature.Status -ne 'Valid' -or
            $redistSignature.SignerCertificate.Subject -notmatch '^CN=Microsoft Corporation,' -or
            $manifest.vcRedistributableSha256 -ne (Get-FileHash -LiteralPath $redist -Algorithm SHA256).Hash) {
            throw "Bundled Visual C++ runtime verification failed: $arch"
        }
        foreach ($component in @(
            @{ Directory = 'service'; Exe = 'PeerSync.Service.exe' },
            @{ Directory = 'app'; Exe = 'PeerSync.exe' },
            @{ Directory = 'cli'; Exe = 'psp.exe' })) {
            $payload = Join-Path $root "publish/$arch/$($component.Directory)/$($component.Exe)"
            $payloadSignature = Get-AuthenticodeSignature -LiteralPath $payload
            if ($payloadSignature.Status -ne 'Valid' -or
                $payloadSignature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint -or
                $null -eq $payloadSignature.TimeStamperCertificate) {
                throw "Payload signature verification failed: $arch $($component.Exe)"
            }
        }
    }
    & (Join-Path $PSScriptRoot 'Build-RendezvousInstaller.ps1') -Version 1.0.0 `
        -IsccPath $iscc -CertificateThumbprint $certificate.Thumbprint -DevelopmentSigning
    if ($LASTEXITCODE -ne 0) { throw 'Rendezvous installer build failed' }
    $serverSetup = Join-Path $root 'artifacts/installer/PeerSync-Rendezvous-Setup-1.0.0.0-x64.exe'
    $serverSignature = Get-AuthenticodeSignature -LiteralPath $serverSetup
    if ($serverSignature.Status -ne 'Valid' -or
        $serverSignature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint -or
        $null -eq $serverSignature.TimeStamperCertificate) {
        throw 'Rendezvous installer signature verification failed'
    }
    foreach ($component in @('PeerSync.Rendezvous.exe', 'PeerSync.Rendezvous.dll')) {
        $payload = Join-Path $root "publish/rendezvous/x64/$component"
        $payloadSignature = Get-AuthenticodeSignature -LiteralPath $payload
        if ($payloadSignature.Status -ne 'Valid' -or
            $payloadSignature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint -or
            $null -eq $payloadSignature.TimeStamperCertificate) {
            throw "Rendezvous payload signature verification failed: $component"
        }
    }
    Write-Output 'Signed development x64, Arm64 and Rendezvous installers built and verified.'
}
finally {
    if ($certificate) {
        $thumbprint = $certificate.Thumbprint
        $trustPath = "Cert:\LocalMachine\Root\$thumbprint"
        $keyPath = "Cert:\LocalMachine\My\$thumbprint"
        try {
            if (Test-Path -LiteralPath $trustPath) { Remove-Item -LiteralPath $trustPath -Force }
            if (Test-Path -LiteralPath $keyPath) { Remove-Item -LiteralPath $keyPath -DeleteKey -Force }
        } finally { $certificate.Dispose() }
    }
}
