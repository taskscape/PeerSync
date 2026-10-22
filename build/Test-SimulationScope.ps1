[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$fixture=Join-Path $root ('artifacts/simulation-scope-controls/'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $fixture | Out-Null
$registry=Get-Content (Join-Path $PSScriptRoot simulation-scope.json) -Raw | ConvertFrom-Json
$baseline=@($registry.baselineMethods | ForEach-Object { $registry.prefix+$_ })
$retained=@($registry.retainedSeeds | ForEach-Object { $registry.prefix+"RetainedHierarchicalRegressionsConvergeAndPreservePublishedPayloads(seed: $_)" })
$passed=0
function Write-Trx([string]$Name,[string[]]$Methods,[string]$Outcome='Passed',[int]$Count=-1,[int]$NotExecuted=0) {
    if ($Count -lt 0) { $Count=$Methods.Count }
    $nodes=@($Methods | ForEach-Object { '<UnitTestResult testName="'+[Security.SecurityElement]::Escape($_)+'" outcome="'+$Outcome+'" />' }) -join ''
    $xml='<TestRun><Results>'+$nodes+'</Results><ResultSummary><Counters total="'+$Count+'" executed="'+$Count+'" passed="'+$Count+'" failed="0" notExecuted="'+$NotExecuted+'" /></ResultSummary></TestRun>'
    $path=Join-Path $fixture "$Name.trx"
    Set-Content -LiteralPath $path -Value $xml
    return $path
}
function Expect-Rejected([string]$Name,[string[]]$Reports) {
    $rejected=$false
    try { & "$PSScriptRoot/Read-SimulationScope.ps1" -Path $Reports | Out-Null }
    catch { $rejected=$true }
    if (-not $rejected) { throw "Invalid Simulation scope accepted: $Name" }
    $script:passed++
}
$full=Write-Trx full ($baseline+$retained)
$a=Write-Trx baseline $baseline
$b=Write-Trx retained $retained
$split=@(
    (Write-Trx retained-1 $retained[0,3,6]),
    (Write-Trx retained-2 $retained[1,4,7]),
    (Write-Trx retained-3 $retained[2,5])
)
foreach ($reports in @(@{paths=@($full)},@{paths=@($a,$b)},@{paths=@($a)+$split})) {
    $scope=& "$PSScriptRoot/Read-SimulationScope.ps1" -Path $reports.paths
    if ($scope.executedMethods -ne 15 -or $scope.retainedRegressionCases -ne 8 -or $scope.maintenanceCases -ne 1) { throw 'Complete Simulation scope rejected' }
    $passed++
}
foreach ($item in @(@('baseline',$a),@('retained-1',$split[0]),@('retained-2',$split[1]),@('retained-3',$split[2]))) {
    $scope=& "$PSScriptRoot/Read-SimulationScope.ps1" -Path $item[1] -Cohort $item[0]
    if ($scope.executedMethods -ne $(if($item[0] -eq 'baseline'){7}elseif($item[0] -eq 'retained-3'){2}else{3})) { throw "Named Simulation cohort rejected: $($item[0])" }
    $passed++
}
foreach ($item in @(@('baseline',$split[0]),@('retained-1',$split[1]),@('retained-2',$split[2]),@('retained-3',$a))) {
    $rejected=$false
    try { & "$PSScriptRoot/Read-SimulationScope.ps1" -Path $item[1] -Cohort $item[0] | Out-Null }
    catch { $rejected=$true }
    if (-not $rejected) { throw "Wrong named Simulation cohort accepted: $($item[0])" }
    $passed++
}
Expect-Rejected missing @($a)
Expect-Rejected duplicate @($a,$a)
Expect-Rejected omitted-seed @($a,(Write-Trx omitted $retained[0..6]))
Expect-Rejected partial-counters @($a,(Write-Trx partial $retained -Count 7))
Expect-Rejected failed-result @($a,(Write-Trx failed $retained -Outcome Failed))
Expect-Rejected skipped-counter @($a,(Write-Trx skipped $retained -NotExecuted 1))
Expect-Rejected wrong-seed @($a,(Write-Trx wrong ($retained[0..6]+@('unexpected method'))))
Expect-Rejected extra-cohort @($a,$b,$b)
Expect-Rejected missing-split @($a,$split[0],$split[1])
Expect-Rejected duplicated-split @($a,$split[0],$split[1],$split[1])
$empty=Write-Trx empty @()
Expect-Rejected empty @($full,$empty)
$dtd=Join-Path $fixture dtd.trx
Set-Content -LiteralPath $dtd -Value '<!DOCTYPE TestRun [<!ENTITY x "test">]><TestRun>&x;</TestRun>'
Expect-Rejected dtd @($dtd)
@{status='passed';controls=$passed;fixture=$fixture} | ConvertTo-Json
