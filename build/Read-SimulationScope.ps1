[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Path)
$ErrorActionPreference='Stop'
$file=Get-Item -LiteralPath $Path
if ($file.Length -gt 10MB) { throw 'Simulation TRX exceeds the evidence limit' }
$settings=[System.Xml.XmlReaderSettings]::new()
$settings.DtdProcessing=[System.Xml.DtdProcessing]::Prohibit
$settings.MaxCharactersInDocument=10MB
$reader=[System.Xml.XmlReader]::Create($file.FullName,$settings)
try { $document=[System.Xml.XmlDocument]::new(); $document.Load($reader) }
finally { $reader.Dispose() }
$registry=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'simulation-scope.json') -Raw | ConvertFrom-Json
if ($registry.schema -ne 'peersync-simulation-scope-v1') { throw 'Unknown simulation scope registry' }
$expected=@($registry.baselineMethods | ForEach-Object { $registry.prefix + $_ })
$expected+=@($registry.retainedSeeds | ForEach-Object { $registry.prefix + "RetainedHierarchicalRegressionsConvergeAndPreservePublishedPayloads(seed: $_)" })
$results=@($document.SelectNodes("//*[local-name()='UnitTestResult']"))
$counters=$document.SelectSingleNode("//*[local-name()='Counters']")
if ($null -eq $counters -or $results.Count -ne $expected.Count -or @($results | Where-Object outcome -NE 'Passed').Count -ne 0) { throw 'Simulation did not execute the complete successful method scope' }
foreach ($field in @('total','executed','passed')) { if ($counters.GetAttribute($field) -ne [string]$expected.Count) { throw 'Simulation test counters do not match executed scope' } }
foreach ($field in @('failed','error','timeout','aborted','notExecuted','inconclusive')) { if ($counters.HasAttribute($field) -and $counters.GetAttribute($field) -ne '0') { throw 'Simulation has failed, skipped or incomplete counters' } }
$actual=@($results | ForEach-Object testName | Sort-Object)
if (@(Compare-Object -ReferenceObject @($expected | Sort-Object) -DifferenceObject $actual).Count -ne 0) { throw 'Simulation executed a different method scope' }
[pscustomobject]@{executedMethods=$results.Count;retainedRegressionCases=@($results | Where-Object testName -Like '*RetainedHierarchicalRegressionsConvergeAndPreservePublishedPayloads*').Count;
 maintenanceCases=@($results | Where-Object testName -Like '*ConnectedMaintenanceExpiresArchivesAndBlocksWithoutLosingDelayedPeerVersions').Count}
