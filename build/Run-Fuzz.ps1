[CmdletBinding()]
param([ValidateRange(1,86400)][int]$Seconds = 600)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    New-Item -ItemType Directory -Force artifacts/fuzz | Out-Null
    & "$PSScriptRoot/Write-Environment.ps1" -Path artifacts/fuzz/environment.json
    $clock = [Diagnostics.Stopwatch]::StartNew()
    # A separate required CI job gives fuzzing its full duration without competing
    # with QUIC probes, ReadyToRun compilation, artifact uploads or cache saving.
    & dotnet restore tests/PeerSync.Fuzz/PeerSync.Fuzz.csproj --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Fuzz restore failed' }
    & dotnet build tests/PeerSync.Fuzz/PeerSync.Fuzz.csproj -c Release -p:PlatformTarget=x64 --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Fuzz build failed' }
    $exe = Join-Path $root 'tests/PeerSync.Fuzz/bin/Release/net10.0-windows10.0.20348.0/PeerSync.Fuzz.exe'
    & $exe --seconds $Seconds 2> artifacts/fuzz/stderr.log | Tee-Object -FilePath artifacts/fuzz/summary.log
    if ($LASTEXITCODE -ne 0) { throw 'Fuzz failed; inspect artifacts/fuzz' }
    $result = Get-Content artifacts/fuzz/summary.log | ConvertFrom-Json
    if ($result.target -ne 'FastCDC,PathRules,VersionVector,IgnoreMatcher,FrameCodec' -or $result.seconds -lt $Seconds -or $result.crashes -ne 0 -or $result.seeds -le 0) {
        throw 'Fuzz result does not prove the requested duration and target'
    }
    $clock.Stop()
    @{status='passed';requestedSeconds=$Seconds;seconds=$clock.Elapsed.TotalSeconds;target=$result.target;seeds=$result.seeds;gateP0Passed=$false} |
        ConvertTo-Json | Set-Content artifacts/fuzz/verification.json
}
finally { Pop-Location }
