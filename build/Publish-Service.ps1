[CmdletBinding()]
param([ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$destination = Join-Path $root "artifacts/deploy/PeerSync-$Runtime-$stamp"
$service = Join-Path $destination 'service'
$cli = Join-Path $destination 'cli'
$tools = Join-Path $destination 'tools'
New-Item -ItemType Directory -Force $service, $cli, $tools | Out-Null
Push-Location $root
try {
    foreach ($project in @(@{Name='PeerSync.Service'; Path='src/PeerSync.Service/PeerSync.Service.csproj'; Output=$service},
                           @{Name='PeerSync.Cli'; Path='src/PeerSync.Cli/PeerSync.Cli.csproj'; Output=$cli})) {
        & dotnet publish $project.Path -c Release -r $Runtime --self-contained `
            -p:RestoreLockedMode=true -p:PublishReadyToRun=false -o $project.Output
        if ($LASTEXITCODE -ne 0) { throw "Publish failed: $($project.Name)" }
    }
    Copy-Item -LiteralPath (Join-Path $root 'docs/diagnostics.md') -Destination (Join-Path $destination 'diagnostics.md')
    Copy-Item -LiteralPath (Join-Path $root 'docs/peersync-service-two-pc-runbook.md') -Destination (Join-Path $destination 'README.md')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Allow-PeerSync-TestTraffic.ps1') -Destination $tools
    $revision = (& git -C $root rev-parse HEAD).Trim()
    $dirty = @(& git -C $root status --porcelain).Count -gt 0
    @{
        sourceRevision = $revision
        workingTreeDirty = $dirty
        runtime = $Runtime
        phase = 'P2'
        selfContained = $true
        productionSyncAvailable = $false
        gateP2Passed = $false
        publishedUtc = [DateTime]::UtcNow.ToString('O')
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'manifest.json') -Encoding utf8
    $archive = "$destination.zip"
    Compress-Archive -Path (Join-Path $destination '*') -DestinationPath $archive -CompressionLevel Optimal
    Write-Output "DEPLOYMENT_DIRECTORY $destination"
    Write-Output "DEPLOYMENT_ARCHIVE $archive"
    Write-Output "SERVICE_EXE $service/PeerSync.Service.exe"
    Write-Output "CLI_EXE $cli/psp.exe"
}
finally { Pop-Location }
