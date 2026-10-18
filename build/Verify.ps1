[CmdletBinding()]
param([ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64', [switch]$SkipRuntimeChecks, [int]$FuzzSeconds = 600)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    New-Item -ItemType Directory -Force artifacts | Out-Null
    & "$PSScriptRoot/Write-Environment.ps1" -Path "artifacts/environment-$Runtime.json"
    $clock = [Diagnostics.Stopwatch]::StartNew()
    & dotnet restore PeerSync.slnx --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed' }
    $architecture = if ($Runtime -eq 'win-arm64') { 'arm64' } else { 'x64' }
    & dotnet build PeerSync.slnx -c Release -p:PlatformTarget=$architecture --no-restore -bl:"artifacts/build-$Runtime.binlog"
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    if (-not $SkipRuntimeChecks) {
        & "$PSScriptRoot/Test-Diagnostics.ps1"
        # P0 has tests only in these projects. Empty future-phase projects are not counted as validation.
        foreach ($name in @('Core','Crypto')) {
            & dotnet test --project "tests/PeerSync.$name.Tests/PeerSync.$name.Tests.csproj" -c Release --no-build --report-trx --results-directory artifacts/tests
            if ($LASTEXITCODE -ne 0) { throw "Tests failed: $name" }
        }
        # Build without RID for scripts whose process paths deliberately use the regular output directory.
        & dotnet build PeerSync.slnx -c Release --no-restore
        if ($LASTEXITCODE -ne 0) { throw 'Probe build failed' }
        & "$PSScriptRoot/Run-P0.ps1"
        & dotnet run --project tests/PeerSync.Fuzz -c Release --no-build -- --seconds $FuzzSeconds
        if ($LASTEXITCODE -ne 0) { throw 'P0 boundary fuzz failed' }
    }
    & "$PSScriptRoot/Publish.ps1" -Runtime $Runtime
    $clock.Stop()
    @{status='passed';runtime=$Runtime;runtimeChecks=(-not $SkipRuntimeChecks);seconds=$clock.Elapsed.TotalSeconds;phase='P0';gateP0Passed=$false} | ConvertTo-Json | Set-Content "artifacts/verification-$Runtime.json"
}
finally { Pop-Location }
