[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Client', 'Rendezvous')][string]$Role,
    [string]$OutputDirectory,
    [ValidatePattern('^[A-Za-z0-9.-]+:[0-9]{1,5}$')][string]$ProbeHost,
    [ValidateRange(10, 180)][int]$CommandTimeoutSeconds = 45
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($Role -eq 'Rendezvous' -and $ProbeHost) {
    throw 'ProbeHost applies only to a client on another machine.'
}

# Keep this command read-only on the deployment host. Diagnostic collectors
# write only new files in the requested output directory, not service state.
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $env:TEMP ('PeerSync-field-' +
        [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' +
        [Guid]::NewGuid().ToString('N').Substring(0, 8))
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) {
    throw 'The evidence output directory must be new.'
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$issues = [System.Collections.Generic.List[string]]::new()
$base = if ($Role -eq 'Client') {
    Join-Path $env:ProgramFiles 'PeerSync'
} else {
    Join-Path $env:ProgramFiles 'PeerSync Rendezvous'
}
$serviceName = if ($Role -eq 'Client') { 'PeerSync' } else { 'PeerSyncRendezvous' }
$expectedServiceAccount = 'NT SERVICE\' + $serviceName
$exe = if ($Role -eq 'Client') {
    Join-Path $base 'service\PeerSync.Service.exe'
} else {
    Join-Path $base 'PeerSync.Rendezvous.exe'
}

function Quote-Argument([string]$value) {
    if ($value.Contains('"')) { throw 'Invalid quote in a command path.' }
    return '"' + $value + '"'
}

function Invoke-BoundedTool([string]$file, [string]$arguments,
    [bool]$jsonOutput = $true) {
    $process = [Diagnostics.Process]::new()
    try {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
            return [pscustomobject]@{ exitCode = $null; timedOut = $false; errorType = 'ExecutableMissing'; data = $null }
        }
        $start = [Diagnostics.ProcessStartInfo]::new()
        $start.FileName = $file
        $start.Arguments = $arguments
        $start.UseShellExecute = $false
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $start.CreateNoWindow = $true
        $process.StartInfo = $start
        if (-not $process.Start()) { throw 'Could not start installed tool.' }
        # Drain both pipes while waiting: synchronous reads can deadlock when
        # either child stream fills its OS buffer.
        $outputTask = $process.StandardOutput.ReadToEndAsync()
        $errorTask = $process.StandardError.ReadToEndAsync()
        try {
            if (-not $process.WaitForExit($CommandTimeoutSeconds * 1000)) {
                $process.Kill()
                $process.WaitForExit()
                $null = $outputTask.GetAwaiter().GetResult()
                $null = $errorTask.GetAwaiter().GetResult()
                return [pscustomobject]@{ exitCode = $null; timedOut = $true; errorType = $null; data = $null }
            }
            $output = $outputTask.GetAwaiter().GetResult()
            $null = $errorTask.GetAwaiter().GetResult()
            $exit = $process.ExitCode
            if ($output.Length -gt 4MB) {
                return [pscustomobject]@{ exitCode = $exit; timedOut = $false; errorType = 'OutputTooLarge'; data = $null }
            }
            $data = $null
            if ($jsonOutput) {
                try { $data = $output | ConvertFrom-Json }
                catch { return [pscustomobject]@{ exitCode = $exit; timedOut = $false; errorType = 'InvalidJson'; data = $null } }
            }
            return [pscustomobject]@{ exitCode = $exit; timedOut = $false; errorType = $null; data = $data }
        } finally {
            $outputTask.Dispose()
            $errorTask.Dispose()
        }
    } catch {
        return [pscustomobject]@{ exitCode = $null; timedOut = $false; errorType = $_.Exception.GetType().Name; data = $null }
    } finally {
        $process.Dispose()
    }
}

$os = $null
try {
    $win = Get-CimInstance Win32_OperatingSystem
    $os = [ordered]@{ caption = $win.Caption; build = $win.BuildNumber; architecture = $win.OSArchitecture }
} catch { $issues.Add('os-audit-unavailable') }

$service = [ordered]@{ installed = $false; state = $null; startName = $null }
$servicePid = $null
try {
    $installed = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    if ($null -ne $installed) {
        $service = [ordered]@{ installed = $true; state = $installed.State; startName = $installed.StartName }
        $servicePid = $installed.ProcessId
    }
    if (-not $service.installed -or $service.state -ne 'Running') { $issues.Add('service-not-running') }
    if ($service.installed -and $service.startName -ne $expectedServiceAccount) { $issues.Add('service-account-unexpected') }
} catch { $issues.Add('service-audit-unavailable') }

$clientListener = $null
if ($Role -eq 'Client') {
    try {
        $settings = Get-Content -LiteralPath (Join-Path $env:ProgramData 'PeerSync\config\settings.json') -Raw | ConvertFrom-Json
        $port = [int]$settings.listenPort
        if ($port -lt 1 -or $port -gt 65535) { throw 'Invalid listener port.' }
        $tcp = @(Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue |
            Where-Object { $_.OwningProcess -eq $servicePid })
        $udp = @(Get-NetUDPEndpoint -LocalPort $port -ErrorAction SilentlyContinue |
            Where-Object { $_.OwningProcess -eq $servicePid })
        $clientListener = [ordered]@{ configuredPort = $port; tcpOwned = $tcp.Count -gt 0;
            udpOwned = $udp.Count -gt 0; idleBeforeFolderRegistrationIsAllowed = $true }
    } catch { $issues.Add('listener-audit-unavailable') }
}

$binary = [ordered]@{ present = $false; version = $null; sha256 = $null; signature = $null; signer = $null }
try {
    if (Test-Path -LiteralPath $exe -PathType Leaf) {
        $signature = Get-AuthenticodeSignature -LiteralPath $exe
        $binary = [ordered]@{
            present = $true
            version = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
            sha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
            signature = $signature.Status.ToString()
            signer = if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { $null }
        }
        if ($signature.Status -ne 'Valid') { $issues.Add('binary-signature-invalid') }
    } else { $issues.Add('binary-missing') }
} catch { $issues.Add('binary-audit-unavailable') }

$powershell51 = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$audit = if ($Role -eq 'Client') {
    Join-Path $base 'tools\Audit-CrashPolicy.ps1'
} else { Join-Path $base 'Audit-CrashPolicy.ps1' }
$crash = Invoke-BoundedTool $powershell51 (
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' + (Quote-Argument $audit) +
    ' -Component ' + $Role)
if ($crash.exitCode -ne 0 -or $null -eq $crash.data) { $issues.Add('crash-policy-audit-failed') }

$preflight = $null
$status = $null
$probe = $null
$bundleName = if ($Role -eq 'Client') { 'client-diagnostics.zip' } else { 'rendezvous-diagnostics.zip' }
$bundlePath = Join-Path $OutputDirectory $bundleName
if ($Role -eq 'Rendezvous') {
    $scriptFile = Join-Path $base 'Test-RendezvousDeployment.ps1'
    $preflight = Invoke-BoundedTool $powershell51 (
        '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' + (Quote-Argument $scriptFile))
    if ($preflight.exitCode -ne 0 -or $null -eq $preflight.data -or -not $preflight.data.localReady) {
        $issues.Add('central-preflight-failed')
    }
    $bundle = Invoke-BoundedTool $exe ('diagnostics --output ' + (Quote-Argument $bundlePath)) $false
} else {
    $cli = Join-Path $base 'cli\psp.exe'
    $status = Invoke-BoundedTool $cli 'status --json'
    if ($status.exitCode -ne 0 -or $null -eq $status.data) { $issues.Add('client-status-failed') }
    if ($ProbeHost) {
        $probe = Invoke-BoundedTool $cli ('probe ' + $ProbeHost)
        if ($probe.exitCode -ne 0 -or $null -eq $probe.data) { $issues.Add('network-probe-failed') }
    }
    $logPath = Join-Path $env:ProgramData 'PeerSync\logs\service'
    $bundle = Invoke-BoundedTool $cli (
        'diagnostics --logs ' + (Quote-Argument $logPath) + ' --output ' +
        (Quote-Argument $bundlePath)) $false
}
$bundleSummary = [ordered]@{ created = $false; sha256 = $null; bytes = $null; name = $bundleName }
if ($bundle.exitCode -eq 0 -and (Test-Path -LiteralPath $bundlePath -PathType Leaf)) {
    $item = Get-Item -LiteralPath $bundlePath
    $bundleSummary.created = $true
    $bundleSummary.bytes = $item.Length
    $bundleSummary.sha256 = (Get-FileHash -LiteralPath $bundlePath -Algorithm SHA256).Hash
} else { $issues.Add('diagnostic-bundle-failed') }

$result = [ordered]@{
    schema = 'peersync-field-evidence-v1'
    checkedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    role = $Role
    scope = 'local host and optional port-99 probe; physical sync and provider admission require separate checks'
    os = $os
    service = $service
    clientListener = $clientListener
    binary = $binary
    crashPolicy = if ($crash.data) { $crash.data } else { [ordered]@{ exitCode = $crash.exitCode; errorType = $crash.errorType; timedOut = $crash.timedOut } }
    centralPreflight = if ($preflight -and $preflight.data) { $preflight.data } else { $null }
    clientStatus = if ($status -and $status.data) {
        [ordered]@{ productionSyncAvailable = $status.data.productionSyncAvailable; peerCount = @($status.data.peers).Count }
    } else { $null }
    networkProbe = if ($probe -and $probe.data) {
        [ordered]@{
            sessionId = $probe.data.sessionId
            tcp = [ordered]@{ reachable = $probe.data.tcp.reachable; version = $probe.data.tcp.httpVersion; correlated = $probe.data.tcp.correlated; category = $probe.data.tcp.category; error = $probe.data.tcp.error }
            udp = [ordered]@{ reachable = $probe.data.udp.reachable; version = $probe.data.udp.httpVersion; correlated = $probe.data.udp.correlated; category = $probe.data.udp.category; error = $probe.data.udp.error }
            observedAddressOmitted = $true
        }
    } else { $null }
    diagnosticBundle = $bundleSummary
    issues = @($issues.ToArray())
    localChecksPassed = $issues.Count -eq 0
}
$summaryPath = Join-Path $OutputDirectory 'deployment-evidence.json'
$result | ConvertTo-Json -Depth 9 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
Write-Output $summaryPath
if ($issues.Count -ne 0) { exit 1 }
