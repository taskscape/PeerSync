[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$script = Join-Path $repository 'installer\Set-UpdatePolicy.ps1'
$parent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$id = [guid]::NewGuid().ToString('N')
$root = Join-Path $parent ('PeerSync-update-rotation-' + $id)
$savedProgramData = $env:ProgramData
$rsa = $null
$certificate = $null

function Assert-OwnedFixture([string]$path) {
    $full = [IO.Path]::GetFullPath($path)
    if (-not [string]::Equals([IO.Path]::GetDirectoryName($full), $parent,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([IO.Path]::GetFileName($full), 'PeerSync-update-rotation-' + $id,
            [StringComparison]::Ordinal)) {
        throw 'Unexpected update rotation fixture path.'
    }
}

try {
    Assert-OwnedFixture $root
    $policyDirectory = Join-Path $root 'PeerSync\update-policy'
    $pending = Join-Path $root 'PeerSync\updates'
    New-Item -ItemType Directory -Path $policyDirectory, $pending -Force | Out-Null
    $policyPath = Join-Path $policyDirectory 'update-policy.json'
    $oldPin = 'A' * 64
    @{ enabled = $true; manifestUrl = 'https://updates.example.test/old.json';
        publisherCertificate = ''; publisherCertificateSha256 = $oldPin } |
        ConvertTo-Json -Compress | Set-Content -LiteralPath $policyPath
    $before = [IO.File]::ReadAllBytes($policyPath)
    $oldReady = Join-Path $pending ('b' * 32 + '.ready')
    New-Item -ItemType Directory -Path $oldReady | Out-Null

    $rsa = [Security.Cryptography.RSA]::Create(3072)
    $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
        'CN=PeerSync Rotation Fixture', $rsa, [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1),
        [DateTimeOffset]::UtcNow.AddDays(1))
    $publicPath = Join-Path $root 'new-publisher.cer'
    [IO.File]::WriteAllBytes($publicPath, $certificate.Export(
        [Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    $env:ProgramData = $root
    $parameters = @{ Enable = $true; ManifestUrl = 'https://updates.example.test/new.json';
        PublisherCertificate = $publicPath }

    $refused = $false
    try { & $script @parameters | Out-Null }
    catch { $refused = $_.Exception.Message.Contains('staged update') }
    if (-not $refused -or
        [BitConverter]::ToString($before) -cne [BitConverter]::ToString([IO.File]::ReadAllBytes($policyPath))) {
        throw 'Rotation did not preserve the policy while an old ready slot existed.'
    }
    if (-not [string]::Equals([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($oldReady)),
            [IO.Path]::GetFullPath($pending), [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($oldReady) -cne ('b' * 32 + '.ready')) {
        throw 'Unexpected old ready slot path.'
    }
    Remove-Item -LiteralPath $oldReady -Recurse -Force
    & $script @parameters | Out-Null
    $after = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json
    $actual = [Convert]::FromBase64String($after.publisherCertificate)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $pin = [BitConverter]::ToString($sha.ComputeHash($actual)).Replace('-', '') }
    finally { $sha.Dispose() }
    if (-not $after.enabled -or $after.publisherCertificateSha256 -cne $pin -or
        $after.manifestUrl -cne 'https://updates.example.test/new.json') {
        throw 'The new publisher policy was not installed after old-slot disposition.'
    }
    Write-Host 'Update publisher rotation guard passed.'
}
finally {
    $env:ProgramData = $savedProgramData
    if ($null -ne $certificate) { $certificate.Dispose() }
    if ($null -ne $rsa) { $rsa.Dispose() }
    Assert-OwnedFixture $root
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
