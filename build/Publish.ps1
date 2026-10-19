[CmdletBinding()]
param([ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$destination = Join-Path $root "artifacts/publish/$Runtime"
New-Item -ItemType Directory -Force $destination | Out-Null
$projects = @('QuicBasics','QuicPunch','ControlPipe','DeviceCert','Rendezvous99','WatcherStress')
foreach($name in $projects) {
    # These are temporary diagnostic probes. Recompiling six self-contained
    # bundles with crossgen2 exhausted the CI time budget after tests passed.
    & dotnet publish (Join-Path $root "spikes/$name/$name.csproj") -c Release -r $Runtime --self-contained -p:RestoreLockedMode=true -p:PublishReadyToRun=false -o "$destination/$name"
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $name" }
}
# Keep executables isolated: merging project output folders would overwrite host runtime configuration.
Copy-Item -LiteralPath (Join-Path $root 'docs/diagnostics.md') -Destination "$destination/README.md"
Copy-Item -LiteralPath (Join-Path $root 'docs/quic-two-pc-retest.md') -Destination "$destination/quic-two-pc-retest.md"
Copy-Item -LiteralPath (Join-Path $root 'build/Collect-Diagnostics.ps1') -Destination $destination
@{sourceRevision=(& git -C $root rev-parse HEAD);workingTreeDirty=(@(& git -C $root status --porcelain).Count -gt 0);runtime=$Runtime;phase='P0';selfContained=$true;readyToRun=$false;productionSyncAvailable=$false;publishedUtc=[DateTime]::UtcNow.ToString('O')} | ConvertTo-Json | Set-Content "$destination/manifest.json"
