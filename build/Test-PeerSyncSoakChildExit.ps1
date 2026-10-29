[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$output = [IO.Path]::GetFullPath($OutputDirectory)
if ([IO.Directory]::Exists($output) -or [IO.File]::Exists($output)) {
    throw 'Use a new output directory for the child exit-code control.'
}
if (-not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($output))) {
    throw 'The output parent must already exist.'
}

$launcher = Join-Path $PSScriptRoot 'Invoke-PeerSyncPhysicalSoak.ps1'
$operations = Join-Path $PSScriptRoot 'Invoke-PeerSyncSoakOperations.ps1'
$powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
foreach ($path in @($launcher, $operations, $powershell)) {
    if (-not [IO.File]::Exists($path)) { throw "Missing soak control dependency: $path" }
}

# Execute the actual child launcher functions without starting a three-hour
# capture. This catches the Windows PowerShell 5.1 Process.ExitCode behavior
# observed in the first physical soak.
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($launcher, [ref]$tokens, [ref]$errors)
if (@($errors).Count -ne 0) { throw 'Physical soak launcher has parser errors.' }
$names = @('Quote', 'Encode-Command', 'Start-RedirectedPowerShell')
$definitions = @($ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -in $names
}, $true))
if ($definitions.Count -ne 3) { throw 'Expected child launcher functions were not found.' }
foreach ($definition in $definitions) { . ([scriptblock]::Create($definition.Extent.Text)) }

$root = Join-Path $output 'root'
$logs = Join-Path $output 'logs'
[IO.Directory]::CreateDirectory($root) | Out-Null
[IO.Directory]::CreateDirectory($logs) | Out-Null
$runId = [Guid]::NewGuid()
$run = $runId.ToString('N')
$command = '& ' + (Quote $operations) + ' -Root ' + (Quote $root) +
    ' -RunId ' + (Quote $runId.ToString()) +
    ' -MachineLabel probe -LogDirectory ' + (Quote $logs) + ' -Count 5'
$successOut = Join-Path $logs 'success-stdout.txt'
$successErr = Join-Path $logs 'success-stderr.txt'
$child = Start-RedirectedPowerShell $command $successOut $successErr
try { $child.WaitForExit(); $successCode = $child.ExitCode }
finally { $child.Dispose() }
$journal = @(Get-ChildItem -LiteralPath $logs -Filter "operations-$run-probe-*.jsonl" -File)
if ($journal.Count -ne 1) { throw 'Successful operation batch journal is missing or ambiguous.' }
$last = Get-Content -LiteralPath $journal[0].FullName -Tail 1 | ConvertFrom-Json

$failureOut = Join-Path $logs 'failure-stdout.txt'
$failureErr = Join-Path $logs 'failure-stderr.txt'
$child = Start-RedirectedPowerShell "throw 'expected child-process control failure'" $failureOut $failureErr
try { $child.WaitForExit(); $failureCode = $child.ExitCode }
finally { $child.Dispose() }
$failureStderrBytes = (Get-Item -LiteralPath $failureErr).Length
if ($successCode -ne 0 -or $last.completed -ne 5 -or $last.failed -ne 0 -or
    $failureCode -ne 1 -or $failureStderrBytes -le 0) {
    throw 'Physical soak child exit-code control failed.'
}

$result = [ordered]@{
    schema = 'peersync-soak-child-exit-control-v1'
    utc = [DateTimeOffset]::UtcNow.ToString('O')
    launcherSha256 = (Get-FileHash -LiteralPath $launcher -Algorithm SHA256).Hash
    runId = $run
    successfulOperations = $last.completed
    failedOperations = $last.failed
    successExitCode = $successCode
    failureExitCode = $failureCode
    failureStderrBytes = $failureStderrBytes
    pass = $true
}
$resultPath = Join-Path $output 'result.json'
$result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $resultPath -Encoding UTF8
"PASS $resultPath"
