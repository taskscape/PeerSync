[CmdletBinding()]
param([string]$BaseRef)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$current = & (Join-Path $PSScriptRoot 'Get-Version.ps1')
if ($BaseRef -and $BaseRef -notmatch '^0+$') {
    & git -C $root merge-base --is-ancestor $BaseRef HEAD
    if ($LASTEXITCODE -ne 0) { throw 'The previous main head must be an ancestor of this push.' }
    $commits = @(& git -C $root rev-list --first-parent --reverse "$BaseRef..HEAD")
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate main commits for the version check.' }
    foreach ($commit in $commits) {
        $versions = @()
        foreach ($ref in @("$commit^", $commit)) {
            $xml = & git -C $root show "${ref}:Directory.Build.props"
            if ($LASTEXITCODE -ne 0) { throw "Cannot read the version at $ref." }
            $value = [string]([xml]($xml -join "`n")).Project.PropertyGroup.Version
            # Before the version source existed, SDK/CI packages used 1.0.0.
            if (-not $value) { $value = '1.0.0' }
            if ($value -cnotmatch '^\d+\.\d+\.\d+$') { throw "Invalid version at $ref." }
            $versions += [version]$value
        }
        if ($versions[1] -le $versions[0]) { throw "Main commit $commit must increase Version above $($versions[0])." }
    }
}
"Version validated: $current"
