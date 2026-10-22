[CmdletBinding()]
param(
    [ValidateRange(1000, 1000000)][int]$Files = 1000000,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$scaleRoot = Join-Path $root 'artifacts\scale'
[IO.Directory]::CreateDirectory($scaleRoot) | Out-Null
$runId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmss') + '-' + [guid]::NewGuid().ToString('N')
$evidence = Join-Path $scaleRoot ('p5-' + $runId)
$fixture = Join-Path $scaleRoot ('p5-' + $runId + '-fixture')
if ((Test-Path -LiteralPath $evidence) -or (Test-Path -LiteralPath $fixture)) {
    throw 'The scale evidence or fixture path already exists.'
}
$available = (Get-PSDrive -Name C).Free
if ($Files -eq 1000000 -and $available -lt 20GB) {
    throw 'A million-file run requires at least 20 GiB free on C: before starting.'
}

Push-Location $root
try {
    if (-not $NoBuild) {
        & dotnet build tests/PeerSync.Benchmarks/PeerSync.Benchmarks.csproj -c Release -p:RestoreLockedMode=true
        if ($LASTEXITCODE -ne 0) { throw 'The P5 benchmark build failed.' }
    }
    $binary = Join-Path $root 'tests\PeerSync.Benchmarks\bin\Release\net10.0-windows10.0.20348.0\PeerSync.Benchmarks.exe'
    if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) { throw 'The P5 benchmark binary is missing.' }
    $revision = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read the Git revision.' }
    $trackedDirty = @(& git diff --name-only HEAD).Count -ne 0
    [IO.Directory]::CreateDirectory($evidence) | Out-Null
    [ordered]@{
        sourceRevision = $revision
        trackedSourceDirty = $trackedDirty
        binarySha256 = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash
        requestedFiles = $Files
        fixture = $fixture
        freeBytesBefore = $available
        startedUtc = [DateTime]::UtcNow.ToString('O')
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'provenance.json')

    $log = Join-Path $evidence 'benchmark.log'
    & dotnet run --project tests/PeerSync.Benchmarks -c Release --no-build -- --scan $fixture --files $Files 2>&1 |
        Tee-Object -FilePath $log
    $runnerExit = $LASTEXITCODE
    $resultLine = Get-Content -LiteralPath $log |
        Where-Object { $_ -match '"target":"(P5 million-file scanner|Scanner)"' } |
        Select-Object -Last 1
    if (-not $resultLine) { throw "Benchmark did not produce a result; evidence retained: $evidence" }
    $result = $resultLine | ConvertFrom-Json
    [ordered]@{
        status = if ($runnerExit -eq 0 -and $result.correct -and $result.targetMet) { 'passed' } else { 'failed' }
        runnerExitCode = $runnerExit
        result = $result
        freeBytesAfter = (Get-PSDrive -Name C).Free
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidence 'summary.json')
    Write-Output "P5_SCALE_EVIDENCE $evidence"
    if ($runnerExit -ne 0 -or -not $result.correct -or -not $result.targetMet) {
        throw "P5 scale target not met; generated fixture and evidence retained: $evidence"
    }
}
finally { Pop-Location }
