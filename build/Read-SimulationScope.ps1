[CmdletBinding()]
param([Parameter(Mandatory=$true)][string[]]$Path)
$ErrorActionPreference='Stop'
$registry=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'simulation-scope.json') -Raw | ConvertFrom-Json
if ($registry.schema -ne 'peersync-simulation-scope-v1') { throw 'Unknown simulation scope registry' }
$expected=@($registry.baselineMethods | ForEach-Object { $registry.prefix + $_ })
$expected+=@($registry.retainedSeeds | ForEach-Object { $registry.prefix + "RetainedHierarchicalRegressionsConvergeAndPreservePublishedPayloads(seed: $_)" })
$results=@()
if ($Path.Count -lt 1 -or $Path.Count -gt 4) { throw 'Simulation requires one full report or up to four cohort reports' }
foreach ($report in $Path) {
    $file=Get-Item -LiteralPath $report
    if ($file.Length -gt 10MB) { throw 'Simulation TRX exceeds the evidence limit' }
    $settings=[System.Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing=[System.Xml.DtdProcessing]::Prohibit
    $settings.MaxCharactersInDocument=10MB
    $reader=[System.Xml.XmlReader]::Create($file.FullName,$settings)
    try { $document=[System.Xml.XmlDocument]::new(); $document.Load($reader) }
    finally { $reader.Dispose() }
    $cohort=@($document.SelectNodes("//*[local-name()='UnitTestResult']"))
    $counterNodes=@($document.SelectNodes("//*[local-name()='Counters']"))
    if ($counterNodes.Count -ne 1 -or $cohort.Count -eq 0 -or @($cohort | Where-Object outcome -NE 'Passed').Count -ne 0) { throw 'Simulation did not execute a complete successful cohort' }
    $counters=$counterNodes[0]
    foreach ($field in @('total','executed','passed')) { if ($counters.GetAttribute($field) -ne [string]$cohort.Count) { throw 'Simulation test counters do not match executed scope' } }
    foreach ($field in @('failed','error','timeout','aborted','notExecuted','inconclusive')) { if ($counters.HasAttribute($field) -and $counters.GetAttribute($field) -ne '0') { throw 'Simulation has failed, skipped or incomplete counters' } }
    $results+=$cohort
}
if ($results.Count -ne $expected.Count -or @($results.testName | Sort-Object -Unique).Count -ne $expected.Count) { throw 'Simulation did not execute the complete unique method scope' }
$actual=@($results | ForEach-Object testName | Sort-Object)
if (@(Compare-Object -ReferenceObject @($expected | Sort-Object) -DifferenceObject $actual).Count -ne 0) { throw 'Simulation executed a different method scope' }
[pscustomobject]@{executedMethods=$results.Count;retainedRegressionCases=@($results | Where-Object testName -Like '*RetainedHierarchicalRegressionsConvergeAndPreservePublishedPayloads*').Count;
 maintenanceCases=@($results | Where-Object testName -Like '*ConnectedMaintenanceExpiresArchivesAndBlocksWithoutLosingDelayedPeerVersions').Count}
