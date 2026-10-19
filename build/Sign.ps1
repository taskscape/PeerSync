[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$Path,
    [string]$CertificateThumbprint = $env:PEERSYNC_SIGNING_THUMBPRINT,
    [string]$TimestampServer = 'http://timestamp.digicert.com',
    [string]$SignToolPath = $env:PEERSYNC_SIGNTOOL_PATH
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) { throw 'BLOCKED: no code-signing certificate configured' }
if (-not [string]::IsNullOrWhiteSpace($SignToolPath)) {
    if (-not (Test-Path -LiteralPath $SignToolPath -PathType Leaf)) {
        throw "SignTool not found at '$SignToolPath'. Supply the full path to signtool.exe."
    }
    $signTool = (Resolve-Path -LiteralPath $SignToolPath).Path
} else {
    $command = Get-Command signtool.exe -CommandType Application -ErrorAction SilentlyContinue
    $signTool = if ($command) { $command.Source } else { $null }
    if (-not $signTool) {
        # The Windows SDK installer does not normally add its versioned tools to PATH.
        $kit = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots' -ErrorAction SilentlyContinue
        $sdkRoots = @($kit.KitsRoot10)
        if (${env:ProgramFiles(x86)}) {
            $sdkRoots += Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
        }
        $architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
        foreach ($sdkRoot in ($sdkRoots | Where-Object { $_ } | Select-Object -Unique)) {
            $versions = Get-ChildItem -LiteralPath (Join-Path $sdkRoot 'bin') -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
                Sort-Object { [version]$_.Name } -Descending
            foreach ($version in $versions) {
                foreach ($toolArchitecture in (@($architecture, 'x86') | Select-Object -Unique)) {
                    $candidate = Join-Path $version.FullName "$toolArchitecture\signtool.exe"
                    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                        $signTool = $candidate
                        break
                    }
                }
                if ($signTool) { break }
            }
            if ($signTool) { break }
        }
    }
    if (-not $signTool) {
        throw 'SignTool was not found. Install Windows SDK Signing Tools, or pass -SignToolPath <full-path-to-signtool.exe>.'
    }
}
Write-Host "Using SignTool: $signTool"
foreach ($file in $Path) {
    $resolved = (Resolve-Path -LiteralPath $file).Path
    & $signTool sign /sha1 $CertificateThumbprint /sm /fd SHA256 /tr $TimestampServer /td SHA256 $resolved
    if ($LASTEXITCODE -ne 0) { throw "Signing failed: $resolved" }
    & $signTool verify /pa /all $resolved
    if ($LASTEXITCODE -ne 0) { throw "Signature verification failed: $resolved" }
}
