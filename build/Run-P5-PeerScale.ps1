[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Fixture,
    [ValidateSet(1000, 1000000)][int]$Files = 1000000,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$scaleRoot = Join-Path $root 'artifacts\scale'
$fixturePath = [IO.Path]::GetFullPath($Fixture)
if (-not (Test-Path -LiteralPath $fixturePath -PathType Container)) {
    throw 'The retained scale fixture does not exist.'
}
if (-not (Test-Path -LiteralPath (Join-Path $fixturePath 'tree') -PathType Container)) {
    throw 'The retained scale fixture has no source tree.'
}
[IO.Directory]::CreateDirectory($scaleRoot) | Out-Null
$evidence = Join-Path $scaleRoot ('p5-peer-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmss') + '-' +
    [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($evidence) | Out-Null

Push-Location $root
try {
    if (-not $NoBuild) {
        & dotnet build tests/PeerSync.Benchmarks/PeerSync.Benchmarks.csproj -c Release -p:RestoreLockedMode=true
        if ($LASTEXITCODE -ne 0) { throw 'The P5 peer benchmark build failed.' }
    }
    $binary = Join-Path $root 'tests\PeerSync.Benchmarks\bin\Release\net10.0-windows10.0.20348.0\PeerSync.Benchmarks.exe'
    if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) { throw 'The P5 peer benchmark binary is missing.' }
    $revision = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read the Git revision.' }
    [ordered]@{
        sourceRevision = $revision
        trackedSourceDirty = @(& git diff --name-only HEAD).Count -ne 0
        binarySha256 = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash
        requestedFiles = $Files
        fixture = $fixturePath
        startedUtc = [DateTime]::UtcNow.ToString('O')
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'provenance.json')

    $log = Join-Path $evidence 'benchmark.log'
    & $binary --scan-peer-existing $fixturePath --files $Files 2>&1 | ForEach-Object {
        $line = [string]$_
        Add-Content -LiteralPath $log -Value $line
        Write-Output $line
    }
    $runnerExit = $LASTEXITCODE
    $rows = Get-Content -LiteralPath $log | Where-Object { $_ -match '^\{"target":' } |
        ForEach-Object { $_ | ConvertFrom-Json }
    $scanner = $rows | Where-Object { $_.target -in @('Scanner', 'P5 million-file scanner') } | Select-Object -Last 1
    $peer = $rows | Where-Object { $_.target -eq 'P5 million-file fresh peer' -and $null -ne $_.correct } | Select-Object -Last 1
    $passed = $runnerExit -eq 0 -and $scanner -and $scanner.correct -and $scanner.targetMet -and $peer -and $peer.correct
    [ordered]@{
        status = if ($passed) { 'passed' } else { 'failed' }
        runnerExitCode = $runnerExit
        scanner = $scanner
        peer = $peer
        endedUtc = [DateTime]::UtcNow.ToString('O')
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidence 'summary.json')
    Write-Output "P5_PEER_SCALE_EVIDENCE $evidence"
    if (-not $passed) { throw "P5 peer scale target not met; fixture and evidence retained: $evidence" }
}
finally { Pop-Location }
