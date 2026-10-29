[CmdletBinding()]
param(
    [ValidateRange(1,86400)][int]$FuzzSeconds = 600,
    [ValidateRange(1,10000)][int]$Cases = 8,
    [ValidateRange(1,10000)][int]$HierarchicalCases = 4,
    [ValidateRange(1,2700)][int]$VerificationBudgetSeconds = 2700,
    [ValidateSet('all','baseline','retained-1','retained-2','retained-3')][string]$Cohort = 'all',
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
    # All copied outputs are immutable before any worker starts. Simulation
    # cases have independent fixtures and disjoint evidence filenames.
    foreach ($project in @('PeerSync.Fuzz','PeerSync.Simulation')) {
        foreach ($file in Get-ChildItem -LiteralPath "tests/$project/bin/Release/net10.0-windows10.0.20348.0" -Filter 'PeerSync*.dll' -File) {
            $hashes += [pscustomobject]@{path=$file.FullName;sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}
        }
    }
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $root 'tests/PeerSync.Simulation/bin/Release/net10.0-windows10.0.20348.0/PeerSync.Simulation.dll')).ProductVersion
    if (-not $version.EndsWith("+$source")) { throw 'Simulation executable does not match the current source revision' }
    $registry = Read-Json (Join-Path $PSScriptRoot simulation-scope.json)
    $seeds = @($registry.retainedSeeds)
    if ($registry.schema -ne 'peersync-simulation-scope-v1' -or $seeds.Count -ne 10 -or @($seeds | Sort-Object -Unique).Count -ne 10) {
        throw 'Boundary retained seed registry is incomplete or duplicated'
    }
    $retainedGroups = @(
        [Collections.Generic.List[int]]::new(),
        [Collections.Generic.List[int]]::new(),
        [Collections.Generic.List[int]]::new()
    )
    for ($index = 0; $index -lt $seeds.Count; $index++) {
        $retainedGroups[$index % $retainedGroups.Count].Add([int]$seeds[$index])
    }
    $simulationCohorts = if ($Cohort -eq 'all') { @('baseline','retained-1','retained-2','retained-3') } else { @($Cohort) }
    if ($Cohort -in @('all','baseline')) {
        Start-Worker -Name fuzz -Executable (Join-Path $root 'tests/PeerSync.Fuzz/bin/Release/net10.0-windows10.0.20348.0/PeerSync.Fuzz.exe') -Arguments @('--seconds',"$FuzzSeconds")
    }
    foreach ($selectedCohort in $simulationCohorts) {
        $arguments = @('test','--project','tests/PeerSync.Simulation','-c','Release','--no-build','--report-trx','--results-directory',(Join-Path $results $selectedCohort))
        if ($selectedCohort -eq 'baseline') {
            $arguments += @('--filter-not-method','*RetainedHierarchicalRegressions*')
        }
        else {
            $group = $retainedGroups[[int]$selectedCohort.Substring('retained-'.Length) - 1]
            foreach ($seed in $group) {
                # Repeated display-name filters are ORed by xUnit. Each exact
                # seed appears once across the three independently owned jobs.
                $arguments += @('--filter-display-name',"*RetainedHierarchicalRegressionsConvergeAndPreservePublishedPayloads(seed: $seed)")
            }
        }
        Start-Worker -Name $selectedCohort -Executable dotnet -Arguments $arguments -Simulation
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
    foreach ($selectedCohort in $simulationCohorts) {
        $files = @(Get-ChildItem -LiteralPath (Join-Path $results $selectedCohort) -Filter '*.trx' -File)
        if ($files.Count -ne 1) { throw "Cannot identify the fresh $selectedCohort Simulation TRX" }
        $trx += $files[0].FullName
    }
    $scope = & "$PSScriptRoot/Read-SimulationScope.ps1" -Path $trx -Cohort $Cohort
    if ($Cohort -in @('all','baseline')) {
        foreach ($item in @(@('summary.json',$Cases),@('hierarchical-summary.json',$HierarchicalCases))) {
            $summary = Read-Json (Join-Path $evidence $item[0])
            if ($summary.status -ne 'passed' -or $summary.completed -ne $item[1]) { throw "Incomplete generated Simulation evidence: $($item[0])" }
        }
        foreach ($item in @(@('cases.jsonl',$Cases),@('hierarchical-cases.jsonl',$HierarchicalCases))) {
            $casesRead = @(Get-Content -LiteralPath (Join-Path $evidence $item[0]) | ForEach-Object { $_ | ConvertFrom-Json })
            if ($casesRead.Count -ne $item[1] -or @($casesRead.Seed | Sort-Object -Unique).Count -ne $item[1] -or @($casesRead | Where-Object { $_.Rounds -le 0 -or $_.Rounds -gt 32 }).Count -ne 0) { throw "Incomplete generated Simulation case list: $($item[0])" }
        }
        $maintenance = Read-Json (Join-Path $evidence connected-maintenance.json)
        if ($maintenance.status -ne 'passed' -or -not $maintenance.sourceInformationalVersion.EndsWith("+$source") -or $maintenance.retiredArchives -ne 2 -or $maintenance.deletedBlocks -ne 2 -or $maintenance.publishedVersions -ne 2) { throw 'Incomplete connected maintenance evidence' }
    }
    $expectedRetained = if ($Cohort -eq 'all') { @($seeds) }
        elseif ($Cohort -eq 'baseline') { @() }
        else { @($retainedGroups[[int]$Cohort.Substring('retained-'.Length) - 1]) }
    $retained = @(Get-ChildItem -LiteralPath (Join-Path $evidence regressions) -Filter result.json -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object { Read-Json $_.FullName })
    if ($retained.Count -ne $expectedRetained.Count -or @(Compare-Object @($expectedRetained | Sort-Object) @($retained.Seed | Sort-Object)).Count -ne 0 -or
        @($retained | Where-Object { $_.Rounds -le 0 -or $_.Rounds -gt 32 -or $_.Nodes -lt 3 -or $_.Nodes -gt 7 -or $_.Versions -le 0 }).Count -ne 0) {
        throw 'Incomplete retained Simulation evidence'
    }
    $result = $null
    if ($Cohort -in @('all','baseline')) {
        $result = ($workers | Where-Object name -EQ fuzz).stdout.GetAwaiter().GetResult() | ConvertFrom-Json
        if ($result.target -ne 'FastCDC,PathRules,VersionVector,IgnoreMatcher,FrameCodec,MessageValidator,KeyText' -or $result.seconds -lt $FuzzSeconds -or $result.crashes -ne 0 -or $result.seeds -le 0) { throw 'Boundary fuzz result does not prove the requested duration and targets' }
    }
    foreach ($file in $hashes) {
        if ((Get-FileHash -LiteralPath $file.path -Algorithm SHA256).Hash -ne $file.sha256) { throw 'A worker build output changed during verification' }
    }
    Assert-Budget
    if ($null -ne $result) {
        @{status='passed';requestedSeconds=$FuzzSeconds;seconds=$result.seconds;target=$result.target;seeds=$result.seeds;gateP0Passed=$false} |
            ConvertTo-Json | Set-Content artifacts/fuzz/verification.json
    }
    $simulationHash=(Get-FileHash -LiteralPath (Join-Path $root 'tests/PeerSync.Simulation/bin/Release/net10.0-windows10.0.20348.0/PeerSync.Simulation.dll') -Algorithm SHA256).Hash
    if ($Cohort -ne 'all') {
        $package=Join-Path $root "artifacts/boundary-shards/$Cohort"
        if (Test-Path -LiteralPath $package) { throw "Boundary shard package already exists: $Cohort" }
        New-Item -ItemType Directory -Path $package | Out-Null
        Copy-Item -LiteralPath $trx[0] -Destination (Join-Path $package scope.trx)
        if ($Cohort -eq 'baseline') {
            foreach ($name in @('summary.json','hierarchical-summary.json','cases.jsonl','hierarchical-cases.jsonl','connected-maintenance.json')) {
                Copy-Item -LiteralPath (Join-Path $evidence $name) -Destination (Join-Path $package $name)
            }
            Copy-Item -LiteralPath artifacts/fuzz/verification.json -Destination (Join-Path $package fuzz-verification.json)
        }
        else {
            New-Item -ItemType Directory -Path (Join-Path $package regressions) | Out-Null
            foreach ($seed in $expectedRetained) {
                Copy-Item -LiteralPath (Join-Path $evidence "regressions/$seed/result.json") -Destination (Join-Path $package "regressions/$seed.json")
            }
        }
        @{schema='peersync-boundary-shard-v1';status='passed';sourceRevision=$source;
          ciRunId=$env:GITHUB_RUN_ID;ciRunAttempt=$env:GITHUB_RUN_ATTEMPT;cohort=$Cohort;
          simulationSha256=$simulationHash;executedMethods=$scope.executedMethods;
          retainedRegressionCases=$scope.retainedRegressionCases;maintenanceCases=$scope.maintenanceCases;
          retainedSeeds=@($expectedRetained);simulationCases=$Cases;hierarchicalCases=$HierarchicalCases;
          fuzzSeconds=$(if($result){$result.seconds}else{0});fuzzSeeds=$(if($result){$result.seeds}else{0});
          fuzzCrashes=$(if($result){$result.crashes}else{0});
          seconds=$clock.Elapsed.TotalSeconds;binaryOutputsUnchanged=$true} |
            ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $package manifest.json)
    }
    @{status='passed';sourceRevision=$source;ciRunId=$env:GITHUB_RUN_ID;ciRunAttempt=$env:GITHUB_RUN_ATTEMPT;cohort=$Cohort;runId=$runId;fuzzSeconds=$FuzzSeconds;simulationCases=$Cases;hierarchicalCases=$HierarchicalCases;retainedRegressionCases=$scope.retainedRegressionCases;maintenanceCases=$scope.maintenanceCases;executedMethods=$scope.executedMethods;actualFuzzSeconds=$(if($result){$result.seconds}else{0});parallel=$Cohort -eq 'all';simulationCohorts=$simulationCohorts.Count;verificationBudgetSeconds=$VerificationBudgetSeconds;buildSeconds=$buildSeconds;seconds=$clock.Elapsed.TotalSeconds;simulationSha256=$simulationHash;binaryHashes=$hashes;binaryOutputsUnchanged=$true;simulationEvidence=$evidence;trx=$trx;gateP1Passed=$false} |
        ConvertTo-Json -Depth 6 | Set-Content artifacts/fuzz/boundary-simulation.json
}
catch {
    @{status='failed';sourceRevision=$source;ciRunId=$env:GITHUB_RUN_ID;ciRunAttempt=$env:GITHUB_RUN_ATTEMPT;cohort=$Cohort;runId=$runId;fuzzSeconds=$FuzzSeconds;simulationCases=$Cases;hierarchicalCases=$HierarchicalCases;parallel=$Cohort -eq 'all';simulationCohorts=$(if($Cohort -eq 'all'){4}else{1});verificationBudgetSeconds=$VerificationBudgetSeconds;buildSeconds=$buildSeconds;seconds=$clock.Elapsed.TotalSeconds;error=$_.Exception.Message;errorType=$_.Exception.GetType().Name;hResult=$_.Exception.HResult;workers=@($workers | Select-Object name,pid,startedUtc);gateP1Passed=$false} |
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
