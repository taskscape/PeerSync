[CmdletBinding()]
param([Parameter(Mandatory, Position = 0)][string]$Path)
$ErrorActionPreference = 'Stop'
if ($Path.StartsWith('\\?\', [StringComparison]::Ordinal)) {
    $Path = $Path.Substring(4)
}
& (Join-Path $PSScriptRoot 'Sign.ps1') -Path $Path
if ($LASTEXITCODE -ne 0) { throw "Signing failed: $Path" }
