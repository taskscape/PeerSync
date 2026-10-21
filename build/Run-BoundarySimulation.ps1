[CmdletBinding()]
param(
    [ValidateRange(1,86400)][int]$FuzzSeconds = 600,
    [ValidateRange(1,10000)][int]$Cases = 8,
    [ValidateRange(1,10000)][int]$HierarchicalCases = 4,
    [ValidateRange(1,2700)][int]$VerificationBudgetSeconds = 2700,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$clock = [Diagnostics.Stopwatch]::StartNew()
$runId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmss') + '-' + [Guid]::NewGuid().ToString('N')
$evidence = Join-Path $root "artifacts/simulation/$runId"
$results = Join-Path $root "artifacts/tests/simulation/$runId"
$workers = [Collections.Generic.List[object]]::new()
$hashes = @()
$source = $null
$buildSeconds = $null

function Assert-Budget {
    if ($clock.Elapsed.TotalSeconds -ge $VerificationBudgetSeconds) { throw 'Boundary verification exceeded its shared build/execution budget' }
}

function Start-Worker([string]$Name, [string]$Executable, [string[]]$Arguments, [switch]$Simulation) {
    Assert-Budget
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.WorkingDirectory = $root
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    if ($Simulation) {
        $start.Environment['PEERSYNC_SIMULATION_ARTIFACTS'] = $evidence
        $start.Environment['PEERSYNC_SIMULATION_CASES'] = $Cases.ToString([Globalization.CultureInfo]::InvariantCulture)
        $start.Environment['PEERSYNC_SIMULATION_HIERARCHY_CASES'] = $HierarchicalCases.ToString([Globalization.CultureInfo]::InvariantCulture)
        $start.Environment.Remove('PEERSYNC_SIMULATION_CASE') | Out-Null
        $start.Environment.Remove('PEERSYNC_SIMULATION_HIERARCHY_CASE') | Out-Null
    }
    $process = [Diagnostics.Process]::Start($start)
    # Register ownership before any further operation that can fail.
    $worker = [pscustomobject]@{name=$Name;process=$process;stdout=$null;stderr=$null;startedUtc=[DateTime]::UtcNow.ToString('O');startedSeconds=$clock.Elapsed.TotalSeconds;pid=$process.Id}
    $workers.Add($worker)
    $worker.stdout = $process.StandardOutput.ReadToEndAsync()
    $worker.stderr = $process.StandardError.ReadToEndAsync()
    Write-Host "START worker=$Name pid=$($process.Id) elapsedSeconds=$([int]$clock.Elapsed.TotalSeconds)"
}

function Read-Json([string]$Path) { Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }

Push-Location $root
try {
    New-Item -ItemType Directory -Force artifacts/fuzz,$evidence,$results | Out-Null
    & "$PSScriptRoot/Write-Environment.ps1" -Path artifacts/fuzz/environment.json
    $source = (Read-Json artifacts/fuzz/environment.json).sourceRevision
    if (-not $NoBuild) {
        foreach ($project in @('PeerSync.Fuzz','PeerSync.Simulation')) {
            Assert-Budget
            & dotnet restore "tests/$project/$project.csproj" --locked-mode
            if ($LASTEXITCODE -ne 0) { throw "Restore failed: $project" }
            Assert-Budget
            & dotnet build "tests/$project/$project.csproj" -c Release -p:PlatformTarget=x64 --no-restore
            if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
        }
    }
    $buildSeconds = $clock.Elapsed.TotalSeconds
    Assert-Budget
    # All copied outputs are immutable before any worker starts. Baseline and
    # retained fixtures are independent; their evidence filenames are disjoint.
    foreach ($project in @('PeerSync.Fuzz','PeerSync.Simulation')) {
        foreach ($file in Get-ChildItem -LiteralPath "tests/$project/bin/Release/net10.0-windows10.0.20348.0" -Filter 'PeerSync*.dll' -File) {
            $hashes += [pscustomobject]@{path=$file.FullName;sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}
        }
    }
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $root 'tests/PeerSync.Simulation/bin/Release/net10.0-windows10.0.20348.0/PeerSync.Simulation.dll')).ProductVersion
    if (-not $version.EndsWith("+$source")) { throw 'Simulation executable does not match the current source revision' }
    Start-Worker -Name fuzz -Executable (Join-Path $root 'tests/PeerSync.Fuzz/bin/Release/net10.0-windows10.0.20348.0/PeerSync.Fuzz.exe') -Arguments @('--seconds',"$FuzzSeconds")
    foreach ($cohort in @('baseline','retained')) {
        $arguments = @('test','--project','tests/PeerSync.Simulation','-c','Release','--no-build','--report-trx','--results-directory',(Join-Path $results $cohort))
        $filter = if ($cohort -eq 'baseline') { '--filter-not-method' } else { '--filter-method' }
        $arguments += @($filter,'*RetainedHierarchicalRegressions*')
        Start-Worker -Name $cohort -Executable dotnet -Arguments $arguments -Simulation
    }
    $nextProgress = 0
    while ($true) {
        foreach ($worker in $workers) {
            if ($worker.process.HasExited -and $worker.process.ExitCode -ne 0) { throw "Boundary worker $($worker.name) failed ($($worker.process.ExitCode)); inspect owned stdout/stderr and TRX" }
            if ($worker.name -eq 'fuzz' -and -not $worker.process.HasExited -and $clock.Elapsed.TotalSeconds - $worker.startedSeconds -gt $FuzzSeconds + 60) { throw 'Boundary fuzz exceeded its duration/cleanup deadline' }
        }
        Assert-Budget
        if (@($workers | Where-Object { -not $_.process.HasExited }).Count -eq 0) { break }
        if ($clock.Elapsed.TotalSeconds -ge $nextProgress) {
            Write-Host "PROGRESS elapsedSeconds=$([int]$clock.Elapsed.TotalSeconds) active=$((@($workers | Where-Object { -not $_.process.HasExited }).name) -join ',')"
            $nextProgress = $clock.Elapsed.TotalSeconds + 30
        }
        Start-Sleep -Milliseconds 250
    }
    $trx = @()
    foreach ($cohort in @('baseline','retained')) {
        $files = @(Get-ChildItem -LiteralPath (Join-Path $results $cohort) -Filter '*.trx' -File)
        if ($files.Count -ne 1) { throw "Cannot identify the fresh $cohort Simulation TRX" }
        $trx += $files[0].FullName
    }
    $scope = & "$PSScriptRoot/Read-SimulationScope.ps1" -Path $trx
    foreach ($item in @(@('summary.json',$Cases),@('hierarchical-summary.json',$HierarchicalCases))) {
        $summary = Read-Json (Join-Path $evidence $item[0])
        if ($summary.status -ne 'passed' -or $summary.completed -ne $item[1]) { throw "Incomplete generated Simulation evidence: $($item[0])" }
    }
    foreach ($item in @(@('cases.jsonl',$Cases),@('hierarchical-cases.jsonl',$HierarchicalCases))) {
        $casesRead = @(Get-Content -LiteralPath (Join-Path $evidence $item[0]) | ForEach-Object { $_ | ConvertFrom-Json })
        if ($casesRead.Count -ne $item[1] -or @($casesRead.Seed | Sort-Object -Unique).Count -ne $item[1] -or @($casesRead | Where-Object { $_.Rounds -le 0 -or $_.Rounds -gt 24 }).Count -ne 0) { throw "Incomplete generated Simulation case list: $($item[0])" }
    }
    $registry = Read-Json (Join-Path $PSScriptRoot simulation-scope.json)
    $retained = @(Get-ChildItem -LiteralPath (Join-Path $evidence regressions) -Filter result.json -Recurse -File | ForEach-Object { Read-Json $_.FullName })
    if ($retained.Count -ne $registry.retainedSeeds.Count -or @(Compare-Object @($registry.retainedSeeds | Sort-Object) @($retained.Seed | Sort-Object)).Count -ne 0 -or @($retained | Where-Object { $_.Rounds -le 0 -or $_.Rounds -gt 24 }).Count -ne 0) { throw 'Incomplete retained Simulation evidence' }
    $maintenance = Read-Json (Join-Path $evidence connected-maintenance.json)
    if ($maintenance.status -ne 'passed' -or -not $maintenance.sourceInformationalVersion.EndsWith("+$source") -or $maintenance.retiredArchives -ne 2 -or $maintenance.deletedBlocks -ne 2 -or $maintenance.publishedVersions -ne 2) { throw 'Incomplete connected maintenance evidence' }
    $result = ($workers | Where-Object name -EQ fuzz).stdout.GetAwaiter().GetResult() | ConvertFrom-Json
    if ($result.target -ne 'FastCDC,PathRules,VersionVector,IgnoreMatcher' -or $result.seconds -lt $FuzzSeconds -or $result.crashes -ne 0 -or $result.seeds -le 0) { throw 'Boundary fuzz result does not prove the requested duration and targets' }
    foreach ($file in $hashes) {
        if ((Get-FileHash -LiteralPath $file.path -Algorithm SHA256).Hash -ne $file.sha256) { throw 'A worker build output changed during verification' }
    }
    Assert-Budget
    @{status='passed';requestedSeconds=$FuzzSeconds;seconds=$result.seconds;target=$result.target;seeds=$result.seeds;gateP0Passed=$false} |
        ConvertTo-Json | Set-Content artifacts/fuzz/verification.json
    @{status='passed';sourceRevision=$source;runId=$runId;fuzzSeconds=$FuzzSeconds;simulationCases=$Cases;hierarchicalCases=$HierarchicalCases;retainedRegressionCases=$scope.retainedRegressionCases;maintenanceCases=$scope.maintenanceCases;executedMethods=$scope.executedMethods;actualFuzzSeconds=$result.seconds;parallel=$true;simulationCohorts=2;verificationBudgetSeconds=$VerificationBudgetSeconds;buildSeconds=$buildSeconds;seconds=$clock.Elapsed.TotalSeconds;binaryHashes=$hashes;binaryOutputsUnchanged=$true;simulationEvidence=$evidence;trx=$trx;gateP1Passed=$false} |
        ConvertTo-Json -Depth 6 | Set-Content artifacts/fuzz/boundary-simulation.json
}
catch {
    @{status='failed';sourceRevision=$source;runId=$runId;fuzzSeconds=$FuzzSeconds;simulationCases=$Cases;hierarchicalCases=$HierarchicalCases;parallel=$true;simulationCohorts=2;verificationBudgetSeconds=$VerificationBudgetSeconds;buildSeconds=$buildSeconds;seconds=$clock.Elapsed.TotalSeconds;error=$_.Exception.Message;errorType=$_.Exception.GetType().Name;hResult=$_.Exception.HResult;workers=@($workers | Select-Object name,pid,startedUtc);gateP1Passed=$false} |
        ConvertTo-Json -Depth 5 | Set-Content artifacts/fuzz/boundary-simulation.json
    throw
}
finally {
    try {
        $cleanupErrors = @()
        $workerResults = @()
        foreach ($worker in $workers) {
            try {
                if (-not $worker.process.HasExited) {
                    $worker.process.Kill($true)
                    if (-not $worker.process.WaitForExit(10000)) { throw "Owned worker $($worker.name) did not stop" }
                }
                $logRoot = if ($worker.name -eq 'fuzz') { Join-Path $root artifacts/fuzz } else { $evidence }
                if ($worker.stdout) { $worker.stdout.GetAwaiter().GetResult() | Set-Content (Join-Path $logRoot "$($worker.name).stdout.log") }
                if ($worker.stderr) { $worker.stderr.GetAwaiter().GetResult() | Set-Content (Join-Path $logRoot "$($worker.name).stderr.log") }
                if ($worker.name -eq 'fuzz' -and $worker.stdout) { $worker.stdout.GetAwaiter().GetResult() | Set-Content artifacts/fuzz/summary.log }
                $workerResults += @{name=$worker.name;pid=$worker.pid;startedUtc=$worker.startedUtc;exited=$worker.process.HasExited;exitCode=$worker.process.ExitCode}
            }
            catch { $cleanupErrors += $_ }
            finally { $worker.process.Dispose() }
        }
        @{runId=$runId;workers=$workerResults;cleanupErrors=@($cleanupErrors | ForEach-Object { $_.Exception.Message })} | ConvertTo-Json -Depth 5 | Set-Content artifacts/fuzz/workers.json
        if ($cleanupErrors.Count -gt 0) { throw "Boundary cleanup failed: $($cleanupErrors -join '; ')" }
    }
    finally { Pop-Location }
}
