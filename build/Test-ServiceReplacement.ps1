[CmdletBinding()]
param([string]$Scenario)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Scenario) {
    foreach ($case in @('missing-stopped', 'missing-running', 'existing-stopped', 'legacy-unquoted',
            'wrong-image', 'process-running', 'process-uninspectable')) {
        & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $PSCommandPath -Scenario $case
        if ($LASTEXITCODE -ne 0) { throw "Service replacement regression failed: $case" }
    }
    'Service replacement checks passed.'
    exit 0
}

# Run the production helper with fake SCM/CIM boundaries. Never touch a real
# service or kill a real process, including on developer machines.
$fixture = Join-Path $root 'artifacts/service replacement'
Add-Type -AssemblyName System.ServiceProcess
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$image = Join-Path $fixture 'PeerSync.Service.exe'
if (Test-Path -LiteralPath $image) { Remove-Item -LiteralPath $image }
if ($Scenario -in @('existing-stopped', 'legacy-unquoted')) { 'fixture' | Set-Content -LiteralPath $image }
$controller = [pscustomobject]@{ Status = [System.ServiceProcess.ServiceControllerStatus]::Stopped; Stops = 0; Refreshes = 0 }
if ($Scenario -eq 'missing-running') { $controller.Status = [System.ServiceProcess.ServiceControllerStatus]::Running }
$controller | Add-Member ScriptMethod Refresh { $this.Refreshes++ }
$controller | Add-Member ScriptMethod Stop { $this.Stops++; $this.Status = [System.ServiceProcess.ServiceControllerStatus]::Stopped }
$controller | Add-Member ScriptMethod Dispose { }
function Get-CimInstance($ClassName, $Filter) {
    if ($ClassName -eq 'Win32_Service') {
        $path = if ($Scenario -eq 'wrong-image') { Join-Path $fixture 'foreign.exe' } else { $image }
        $path = if ($Scenario -eq 'legacy-unquoted') { $path } else { '"' + $path + '"' }
        return [pscustomobject]@{ PathName = $path; ProcessId = 12345 }
    }
    if ($ClassName -ne 'Win32_Process') { throw "Unexpected CIM class $ClassName" }
    if ($Scenario -eq 'process-running') { return [pscustomobject]@{ ExecutablePath = $image; ProcessId = 12345 } }
    if ($Scenario -eq 'process-uninspectable') { return [pscustomobject]@{ ExecutablePath = $null; ProcessId = 67890 } }
}
$source = Get-Content -LiteralPath (Join-Path $root 'installer/Stop-ServiceAndWait.ps1') -Raw
$constructor = '[System.ServiceProcess.ServiceController]::new($ServiceName)'
if (($source.Split(@($constructor), [StringSplitOptions]::None)).Count -ne 2) { throw 'SCM fixture boundary changed.' }
$source = $source.Replace($constructor, '$controller')
$expectedFailure = $Scenario -in @('wrong-image', 'process-running', 'process-uninspectable')
try {
    & ([scriptblock]::Create($source)) -ServiceName PeerSync -TimeoutSeconds 1
    if ($expectedFailure) { throw "Unsafe service state accepted: $Scenario" }
    if ($controller.Refreshes -lt 2) { throw 'SCM stop/restart checks were skipped.' }
    if ($Scenario -eq 'missing-running' -and $controller.Stops -ne 1) { throw 'Missing-image running service was not stopped.' }
} catch {
    $expected = switch ($Scenario) {
        'wrong-image' { '*service image is not the expected installed executable*' }
        'process-running' { '*process did not exit*' }
        'process-uninspectable' { '*Cannot inspect*process path*' }
        default { $null }
    }
    if (-not $expected -or $_.Exception.Message -notlike $expected) { throw }
}
"Passed: $Scenario"
