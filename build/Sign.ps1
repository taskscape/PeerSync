[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$Path,
    [string]$CertificateThumbprint = $env:PEERSYNC_SIGNING_THUMBPRINT,
    [string]$TimestampServer = 'http://timestamp.digicert.com'
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) { throw 'BLOCKED: no code-signing certificate configured' }
$signTool = (Get-Command signtool.exe -ErrorAction Stop).Source
foreach ($file in $Path) {
    $resolved = (Resolve-Path -LiteralPath $file).Path
    & $signTool sign /sha1 $CertificateThumbprint /sm /fd SHA256 /tr $TimestampServer /td SHA256 $resolved
    if ($LASTEXITCODE -ne 0) { throw "Signing failed: $resolved" }
    & $signTool verify /pa /all $resolved
    if ($LASTEXITCODE -ne 0) { throw "Signature verification failed: $resolved" }
}
