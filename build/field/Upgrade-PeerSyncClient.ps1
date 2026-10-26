[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string]$Version,
    [switch]$PreflightOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
trap {
    "FAILED $($_.Exception.Message)"
    exit 1
}

$setupName = "PeerSync-Setup-$Version-x64.exe"
$setup = Join-Path $PSScriptRoot $setupName
$manifestPath = "$setup.manifest.json"
$certificatePath = "$setup.cer"
$serviceExe = Join-Path $env:ProgramFiles 'PeerSync\service\PeerSync.Service.exe'
$cli = Join-Path $env:ProgramFiles 'PeerSync\cli\psp.exe'

foreach ($path in @($setup, $manifestPath, $certificatePath)) {
    if (-not [IO.File]::Exists($path)) { throw "Upgrade package is incomplete: $path" }
}
if (-not [Environment]::Is64BitOperatingSystem) { throw 'This package requires x64 Windows.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.version -ne $Version -or $manifest.architecture -ne 'x64' -or
    $manifest.signed -ne $true -or $manifest.localDevelopmentOnly -ne $true -or
    $manifest.publicReleaseSigning -ne $false) {
    throw 'The installer manifest is not for this development x64 upgrade.'
}
$setupHash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
$certificateHash = (Get-FileHash -LiteralPath $certificatePath -Algorithm SHA256).Hash
if ($setupHash -ne $manifest.installerSha256 -or
    $certificateHash -ne $manifest.developmentCertificateSha256) {
    throw 'The installer or public certificate differs from its build manifest.'
}
$publicCertificate = Get-PfxCertificate -FilePath $certificatePath
$signature = Get-AuthenticodeSignature -LiteralPath $setup
if (-not $signature.SignerCertificate -or
    $signature.SignerCertificate.Thumbprint -ne $publicCertificate.Thumbprint -or
    $signature.Status -ne 'Valid') {
    throw 'The development installer signature is not valid or trusted on this PC. Check the ZIP SHA-256 before trusting its public certificate.'
}
$oldVersion = if ([IO.File]::Exists($serviceExe)) {
    ([IO.FileInfo]::new($serviceExe)).VersionInfo.FileVersion
} else { $null }
if ($oldVersion -and [version]$oldVersion -gt [version]$Version) {
    throw 'A newer PeerSync service is already installed; refusing a downgrade.'
}
if ($PreflightOnly) {
    "READY_TO_INSTALL version=$Version source=$($manifest.sourceRevision) setupSha256=$setupHash"
    exit 0
}
if (-not ([Security.Principal.WindowsPrincipal]::new(
        [Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run the upgrade launcher as administrator.'
}
if (Get-Process -Name PeerSync -ErrorAction SilentlyContinue) {
    throw 'Exit the PeerSync tray app, then run this launcher again. Closing its dashboard window is not enough.'
}

$run = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' +
    [Guid]::NewGuid().ToString('N').Substring(0, 8)
$stem = "PeerSync-upgrade-$Version-$($env:COMPUTERNAME)-$run"
$log = Join-Path $PSScriptRoot "$stem.log"
$reportPath = Join-Path $PSScriptRoot "$stem.json"
$report = [ordered]@{
    schema = 'peersync-client-upgrade-v1'
    startedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    endedUtc = $null
    targetVersion = $Version
    previousVersion = $oldVersion
    sourceRevision = $manifest.sourceRevision
    setupSha256 = $setupHash
    signerThumbprint = $publicCertificate.Thumbprint
    setupExitCode = $null
    installedVersion = $null
    serviceStatus = $null
    cliStatusExitCode = $null
    error = $null
}
try {
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
        ('/LOG="' + $log + '"'))
    $process = Start-Process -FilePath $setup -ArgumentList $arguments `
        -WindowStyle Hidden -Wait -PassThru
    $report.setupExitCode = $process.ExitCode
    if ($process.ExitCode -eq 3010) {
        throw 'Setup requires a reboot. Reboot this PC, then verify PeerSync before continuing the field test.'
    }
    if ($process.ExitCode -ne 0) { throw "Setup exited $($process.ExitCode)." }
    for ($attempt = 0; $attempt -lt 45; $attempt++) {
        $service = Get-Service -Name PeerSync -ErrorAction SilentlyContinue
        if ($service -and $service.Status -eq 'Running') { break }
        Start-Sleep -Seconds 2
    }
    $report.serviceStatus = (Get-Service -Name PeerSync -ErrorAction Stop).Status.ToString()
    $report.installedVersion = ([IO.FileInfo]::new($serviceExe)).VersionInfo.FileVersion
    if ($report.serviceStatus -ne 'Running' -or $report.installedVersion -ne $Version) {
        throw 'The upgraded service is not Running at the requested version.'
    }
    for ($attempt = 0; $attempt -lt 15; $attempt++) {
        & $cli status --json | Out-Null
        $report.cliStatusExitCode = $LASTEXITCODE
        if ($report.cliStatusExitCode -eq 0) { break }
        Start-Sleep -Seconds 2
    }
    if ($report.cliStatusExitCode -ne 0) { throw 'The installed CLI could not read service status.' }
} catch {
    $report.error = $_.Exception.Message
} finally {
    $report.endedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    $report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $reportPath -Encoding UTF8
}
if ($report.error) {
    "FAILED $($report.error)"
    "REPORT $reportPath"
    "SETUP_LOG $log"
    exit 1
}
"READY version=$($report.installedVersion) service=$($report.serviceStatus)"
"REPORT $reportPath"
"SETUP_LOG $log"
exit 0
