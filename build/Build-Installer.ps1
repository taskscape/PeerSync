[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')][string]$Arch = 'x64',
    [ValidatePattern('^\d+\.\d+\.\d+(?:\.\d+)?$')][string]$Version = '1.0.0',
    [string]$IsccPath,
    [string]$CertificateThumbprint = $env:PEERSYNC_SIGNING_THUMBPRINT,
    [switch]$DevelopmentSigning,
    [switch]$UnsignedDevelopment,
    [switch]$UseExistingRestoredAssets
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$runtime = "win-$Arch"
$stage = Join-Path $root "publish/$Arch"
$output = Join-Path $root 'artifacts/installer'

if (-not $IsccPath) {
    $IsccPath = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7/ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 7/ISCC.exe')
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}
if (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
    throw 'Inno Setup 7.1 ISCC.exe was not found. Install Inno Setup 7.1 or pass -IsccPath.'
}
$isccVersion = [version]((& $IsccPath --version | Select-Object -First 1).Trim())
if ($isccVersion -lt [version]'7.1') { throw "Inno Setup 7.1 or newer is required; found $isccVersion." }
if (-not $UnsignedDevelopment -and [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    throw 'Signed installer requires -CertificateThumbprint or PEERSYNC_SIGNING_THUMBPRINT. Use -UnsignedDevelopment only for local validation.'
}
if ($UnsignedDevelopment -and $DevelopmentSigning) { throw 'Choose signed development or unsigned development, not both.' }
if ($UseExistingRestoredAssets -and -not ($DevelopmentSigning -or $UnsignedDevelopment)) {
    throw 'Existing restored assets are permitted only for local development builds.'
}

# Never reuse an older publish tree: it might contain stale runtime files.
$canonicalRoot = [IO.Path]::GetFullPath($root).TrimEnd('\')
$canonicalStage = [IO.Path]::GetFullPath($stage).TrimEnd('\')
if (-not $canonicalStage.StartsWith($canonicalRoot + '\publish\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clear publish path outside this workspace: $canonicalStage"
}
if (Test-Path -LiteralPath $stage) {
    $stageItem = Get-Item -LiteralPath $stage -Force
    if ($stageItem.LinkType) { throw "Refusing to clear linked publish path: $stage" }
    Remove-Item -LiteralPath $stage -Recurse -Force
}
New-Item -ItemType Directory -Path $stage, $output -Force | Out-Null
$redistCache = Join-Path $root "artifacts/tools/vc_redist.$Arch.exe"
$redistStage = Join-Path $stage 'redist/vc_redist.exe'
$redistUri = "https://aka.ms/vc14/vc_redist.$Arch.exe"
New-Item -ItemType Directory -Path (Split-Path $redistCache -Parent), (Split-Path $redistStage -Parent) -Force | Out-Null
if (-not (Test-Path -LiteralPath $redistCache -PathType Leaf)) {
    Invoke-WebRequest -Uri $redistUri -OutFile $redistCache
}
$redistSignature = Get-AuthenticodeSignature -LiteralPath $redistCache
if ($redistSignature.Status -ne 'Valid' -or
    $redistSignature.SignerCertificate.Subject -notmatch '^CN=Microsoft Corporation,') {
    throw "Microsoft Visual C++ Redistributable signature is invalid: $redistCache"
}
Copy-Item -LiteralPath $redistCache -Destination $redistStage

$projects = @(
    @{ Name = 'service'; Project = 'src/PeerSync.Service/PeerSync.Service.csproj'; Executable = 'PeerSync.Service.exe' },
    @{ Name = 'app'; Project = 'src/PeerSync.App/PeerSync.App.csproj'; Executable = 'PeerSync.exe' },
    @{ Name = 'cli'; Project = 'src/PeerSync.Cli/PeerSync.Cli.csproj'; Executable = 'psp.exe' }
)
Push-Location $root
try {
    if (-not $UseExistingRestoredAssets) {
        & dotnet restore PeerSync.slnx --locked-mode
        if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
    }
    foreach ($item in $projects) {
        $destination = Join-Path $stage $item.Name
        $publishArguments = @($item.Project, '-c', 'Release', '-r', $runtime, '--self-contained',
            '-p:RestoreLockedMode=true', '-p:PublishReadyToRun=true',
            '-p:PublishSingleFile=false', "-p:Version=$Version", '-o', $destination)
        if ($UseExistingRestoredAssets) { $publishArguments += '--no-restore' }
        & dotnet publish @publishArguments
        if ($LASTEXITCODE -ne 0) { throw "Publish failed: $($item.Name)" }
        $executable = Join-Path $destination $item.Executable
        if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
            throw "Publish did not produce $executable."
        }
    }

    $env:PEERSYNC_SIGNING_THUMBPRINT = $CertificateThumbprint
    $arguments = [Collections.Generic.List[string]]::new()
    $arguments.Add("/DArch=$Arch")
    if ($UnsignedDevelopment) {
        $arguments.Add('/DUnsignedDevelopment')
    } else {
        Get-Command pwsh.exe -CommandType Application -ErrorAction Stop | Out-Null
        $signScript = Join-Path $PSScriptRoot 'Sign-Inno.ps1'
        $arguments.Add("--signtool=peersync=pwsh -NoProfile -File `$q$signScript`$q `$f")
    }
    $arguments.Add((Join-Path $root 'installer/PeerSync.iss'))
    & $IsccPath @arguments
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)." }

    $serviceVersion = (Get-Item -LiteralPath (Join-Path $stage 'service/PeerSync.Service.exe')).VersionInfo.FileVersion
    $setup = Join-Path $output "PeerSync-Setup-$serviceVersion-$Arch.exe"
    if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) { throw "Installer not found: $setup" }
    $signature = $null
    if (-not $UnsignedDevelopment) {
        $signature = Get-AuthenticodeSignature -LiteralPath $setup
        if ($signature.Status -ne 'Valid') { throw "Installer signature is $($signature.Status): $($signature.StatusMessage)" }
        if ($DevelopmentSigning) {
            # Public certificate only. Test PCs can trust it without receiving the private key.
            [IO.File]::WriteAllBytes("$setup.cer",
                $signature.SignerCertificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
        }
    }
    $manifest = [ordered]@{
        sourceRevision = (& git rev-parse HEAD).Trim()
        workingTreeDirty = @(& git status --porcelain).Count -gt 0
        architecture = $Arch
        version = $serviceVersion
        selfContained = $true
        readyToRun = $true
        restoreMode = if ($UseExistingRestoredAssets) { 'existing-assets' } else { 'locked' }
        signed = -not $UnsignedDevelopment
        localDevelopmentOnly = [bool]($UnsignedDevelopment -or $DevelopmentSigning)
        publicReleaseSigning = $false
        signerSubject = if ($signature) { $signature.SignerCertificate.Subject } else { $null }
        developmentCertificateSha256 = if ($DevelopmentSigning) {
            (Get-FileHash -LiteralPath "$setup.cer" -Algorithm SHA256).Hash
        } else { $null }
        vcRedistributableSha256 = (Get-FileHash -LiteralPath $redistStage -Algorithm SHA256).Hash
        vcRedistributableSigner = $redistSignature.SignerCertificate.Subject
        publishedUtc = [DateTime]::UtcNow.ToString('O')
        installerSha256 = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
    }
    $manifest | ConvertTo-Json | Set-Content -LiteralPath "$setup.manifest.json" -Encoding utf8
    Write-Output "INSTALLER $setup"
    Write-Output "MANIFEST $setup.manifest.json"
}
finally {
    Pop-Location
}
