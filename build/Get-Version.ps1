[CmdletBinding()]
param([switch]$FileVersion)
$ErrorActionPreference = 'Stop'
$props = [xml](Get-Content -LiteralPath (Join-Path (Split-Path $PSScriptRoot -Parent) 'Directory.Build.props') -Raw)
$value = [string]$props.Project.PropertyGroup.Version
if ($value -cnotmatch '^\d+\.\d+\.\d+$') { throw 'Directory.Build.props must contain one numeric major.minor.patch Version.' }
$parsed = [version]$value
if ($parsed.Major -gt 65535 -or $parsed.Minor -gt 65535 -or $parsed.Build -gt 65535) {
    throw 'Version components must fit the Windows file-version range (0..65535).'
}
if ($FileVersion) { "$value.0" } else { $value }
