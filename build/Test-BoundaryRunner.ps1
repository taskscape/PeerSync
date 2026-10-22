[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$fixture=Join-Path $root ('artifacts/boundary-runner-controls/'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force "$fixture/build","$fixture/bin" | Out-Null
# Exercise the actual supervisor in an owned fixture, never the caller's
# evidence directories. A native invalid-argument executable substitutes for
# dotnet, so no product Simulation fixture is started in this failure control.
Copy-Item -LiteralPath "$PSScriptRoot/Run-BoundarySimulation.ps1" -Destination "$fixture/build/Run-BoundarySimulation.ps1"
Copy-Item -LiteralPath "$PSScriptRoot/simulation-scope.json" -Destination "$fixture/build/simulation-scope.json"
$fuzzOutput=Join-Path $root tests/PeerSync.Fuzz/bin/Release/net10.0-windows10.0.20348.0
$simulationOutput=Join-Path $root tests/PeerSync.Simulation/bin/Release/net10.0-windows10.0.20348.0
foreach ($project in @('PeerSync.Fuzz','PeerSync.Simulation')) {
    New-Item -ItemType Directory -Force "$fixture/tests/$project/bin/Release/net10.0-windows10.0.20348.0" | Out-Null
}
Get-ChildItem -LiteralPath $fuzzOutput | Copy-Item -Destination "$fixture/tests/PeerSync.Fuzz/bin/Release/net10.0-windows10.0.20348.0" -Recurse
Copy-Item -LiteralPath "$simulationOutput/PeerSync.Simulation.dll" -Destination "$fixture/tests/PeerSync.Simulation/bin/Release/net10.0-windows10.0.20348.0/PeerSync.Simulation.dll"
$version=[Diagnostics.FileVersionInfo]::GetVersionInfo("$simulationOutput/PeerSync.Simulation.dll").ProductVersion
$source=$version.Split('+')[-1]
if ($source -notmatch '^[0-9a-f]{40}$') { throw 'Build Simulation before boundary runner controls' }
$stub='param([string]$Path); @{sourceRevision="'+$source+'"} | ConvertTo-Json | Set-Content -LiteralPath $Path'
Set-Content -LiteralPath "$fixture/build/Write-Environment.ps1" -Value $stub.Replace('param([string]$Path); ','param([string]$Path); Start-Sleep -Milliseconds 1200; ')
$rejected=$false
try { & "$fixture/build/Run-BoundarySimulation.ps1" -NoBuild -VerificationBudgetSeconds 1 *> "$fixture/prestart.log" }
catch { $rejected=$_.Exception.Message -like '*shared build/execution budget*' }
if (-not $rejected) { throw 'Expired build budget was accepted' }
$prestart=Get-Content "$fixture/artifacts/fuzz/boundary-simulation.json" -Raw | ConvertFrom-Json
if ($prestart.status -ne 'failed' -or @($prestart.workers).Count -ne 0) { throw 'A worker started after budget exhaustion' }
Set-Content -LiteralPath "$fixture/build/Write-Environment.ps1" -Value $stub
Copy-Item -LiteralPath "$env:SystemRoot/System32/whoami.exe" -Destination "$fixture/bin/dotnet.exe"
$oldPath=$env:PATH
$sentinel=$null
try {
    $start=[Diagnostics.ProcessStartInfo]::new((Get-Command pwsh).Source)
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    foreach ($argument in @('-NoProfile','-Command','Start-Sleep -Seconds 60')) { $start.ArgumentList.Add($argument) }
    $sentinel=[Diagnostics.Process]::Start($start)
    $env:PATH="$fixture/bin;$oldPath"
    $rejected=$false
    try { & "$fixture/build/Run-BoundarySimulation.ps1" -NoBuild -VerificationBudgetSeconds 30 *> "$fixture/failure.log" }
    catch { $rejected=$_.Exception.Message -like '*Boundary worker*failed*' }
    if (-not $rejected) { throw 'Failed worker was accepted' }
    $workers=Get-Content "$fixture/artifacts/fuzz/workers.json" -Raw | ConvertFrom-Json
    if ($workers.workers.Count -ne 5 -or @($workers.workers | Where-Object { -not $_.exited }).Count -ne 0 -or $workers.cleanupErrors.Count -ne 0) { throw 'Owned workers were not all stopped' }
    if ($sentinel.HasExited) { throw 'Supervisor stopped an unrelated process' }
    if (@($workers.workers | Where-Object { $_.name -eq 'fuzz' -and $_.exitCode -ne 0 }).Count -ne 1) { throw 'Control did not stop the running owned fuzz worker' }
}
finally {
    $env:PATH=$oldPath
    if ($sentinel) {
        try { if (-not $sentinel.HasExited) { $sentinel.Kill($true); $sentinel.WaitForExit(10000) | Out-Null } }
        finally { $sentinel.Dispose() }
    }
}
@{status='passed';controls=2;prestartBudget=$true;ownedFailureCleanup=$true;unrelatedProcessPreserved=$true;fixture=$fixture} | ConvertTo-Json
