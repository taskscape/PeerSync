[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:\.\d+)?$')][string]$Version = '1.0.0',
    [string]$IsccPath,
    [string]$CertificateThumbprint = $env:PEERSYNC_SIGNING_THUMBPRINT,
    [switch]$DevelopmentSigning,
    [switch]$UnsignedDevelopment
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$stage = Join-Path $root 'publish/rendezvous/x64'
$output = Join-Path $root 'artifacts/installer'
if (-not $IsccPath) {
    $IsccPath = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7/ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 7/ISCC.exe')
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}
if (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
    throw 'Inno Setup 7.1 ISCC.exe is required. Pass -IsccPath if it is installed elsewhere.'
}
$isccVersion = [version]((& $IsccPath --version | Select-Object -First 1).Trim())
if ($isccVersion -lt [version]'7.1') { throw "Inno Setup 7.1 or newer is required; found $isccVersion." }
if (-not $UnsignedDevelopment -and [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    throw 'Signed installer requires -CertificateThumbprint or PEERSYNC_SIGNING_THUMBPRINT.'
}
if ($UnsignedDevelopment -and $DevelopmentSigning) { throw 'Choose one development signing mode.' }

$canonicalRoot = [IO.Path]::GetFullPath($root).TrimEnd('\')
$canonicalStage = [IO.Path]::GetFullPath($stage).TrimEnd('\')
if (-not $canonicalStage.StartsWith($canonicalRoot + '\publish\rendezvous\',
        [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing to clear path outside the workspace: $canonicalStage" }
if (Test-Path -LiteralPath $stage) {
    $item = Get-Item -LiteralPath $stage -Force
    if ($item.LinkType) { throw "Refusing to clear linked publish path: $stage" }
    Remove-Item -LiteralPath $stage -Recurse -Force
}
New-Item -ItemType Directory -Path $stage, $output -Force | Out-Null
$redistCache = Join-Path $root 'artifacts/tools/vc_redist.x64.exe'
$redistStage = Join-Path $stage 'redist/vc_redist.exe'
New-Item -ItemType Directory -Path (Split-Path $redistCache -Parent) -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path $redistStage -Parent) -Force | Out-Null
if (-not (Test-Path -LiteralPath $redistCache -PathType Leaf)) {
    Invoke-WebRequest -Uri 'https://aka.ms/vc14/vc_redist.x64.exe' -OutFile $redistCache
}
$redistSignature = Get-AuthenticodeSignature -LiteralPath $redistCache
if ($redistSignature.Status -ne 'Valid' -or
    $redistSignature.SignerCertificate.Subject -notmatch '^CN=Microsoft Corporation,') {
    throw 'Microsoft Visual C++ Redistributable signature is invalid.'
}
Copy-Item -LiteralPath $redistCache -Destination $redistStage

Push-Location $root
try {
    & dotnet restore PeerSync.slnx --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
    & dotnet publish src/PeerSync.Rendezvous/PeerSync.Rendezvous.csproj -c Release -r win-x64 `
        --self-contained -p:RestoreLockedMode=true -p:PublishReadyToRun=true `
        -p:PublishSingleFile=false -p:Version=$Version -o $stage
    if ($LASTEXITCODE -ne 0) { throw 'Rendezvous publish failed.' }
    $executable = Join-Path $stage 'PeerSync.Rendezvous.exe'
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Rendezvous publish produced no executable.' }

    $env:PEERSYNC_SIGNING_THUMBPRINT = $CertificateThumbprint
    $arguments = [Collections.Generic.List[string]]::new()
    if ($UnsignedDevelopment) {
        $arguments.Add('/DUnsignedDevelopment')
    } else {
        Get-Command pwsh.exe -CommandType Application -ErrorAction Stop | Out-Null
        $signScript = Join-Path $PSScriptRoot 'Sign-Inno.ps1'
        $arguments.Add("--signtool=peersync=pwsh -NoProfile -File `$q$signScript`$q `$f")
    }
    $arguments.Add((Join-Path $root 'installer/PeerSync.Rendezvous.iss'))
    & $IsccPath @arguments
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)." }

    $fileVersion = (Get-Item -LiteralPath $executable).VersionInfo.FileVersion
    $setup = Join-Path $output "PeerSync-Rendezvous-Setup-$fileVersion-x64.exe"
    if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) { throw "Installer not found: $setup" }
    $signature = $null
    if (-not $UnsignedDevelopment) {
        $signature = Get-AuthenticodeSignature -LiteralPath $setup
        if ($signature.Status -ne 'Valid') { throw "Installer signature is $($signature.Status)." }
        if ($DevelopmentSigning) {
            [IO.File]::WriteAllBytes("$setup.cer",
                $signature.SignerCertificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
        }
    }
    [ordered]@{
        sourceRevision = (& git rev-parse HEAD).Trim()
        workingTreeDirty = @(& git status --porcelain).Count -gt 0
        architecture = 'x64'
        version = $fileVersion
        selfContained = $true
        signed = -not $UnsignedDevelopment
        localDevelopmentOnly = [bool]($UnsignedDevelopment -or $DevelopmentSigning)
        publicReleaseSigning = $false
        signerSubject = if ($signature) { $signature.SignerCertificate.Subject } else { $null }
        vcRedistributableSha256 = (Get-FileHash -LiteralPath $redistStage -Algorithm SHA256).Hash
        publishedUtc = [DateTime]::UtcNow.ToString('O')
        installerSha256 = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
    } | ConvertTo-Json | Set-Content -LiteralPath "$setup.manifest.json" -Encoding utf8
    Write-Output "INSTALLER $setup"
    Write-Output "MANIFEST $setup.manifest.json"
} finally { Pop-Location }
