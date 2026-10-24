[CmdletBinding()]
param(
    [string]$InstallDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([string]::IsNullOrWhiteSpace($InstallDirectory)) {
    # PowerShell 5.1 can evaluate a parameter default before PSScriptRoot is
    # populated for -File. Resolve it in the script body for the packaged
    # no-argument command used by operators.
    $InstallDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
}

# This preflight does not change service configuration. The loopback health
# request emits one ordinary diagnostic event, and cannot establish that the
# provider admits outside traffic.
$issues = [System.Collections.Generic.List[string]]::new()
$result = [ordered]@{
    schema = 'peersync-rendezvous-deployment-v1'
    checkedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    scope = 'server-local; outside TCP/UDP 99 reachability remains unverified'
    configuration = $null
    service = $null
    certificate = $null
    listeners = $null
    firewall = $null
    dns = $null
    localHealth = $null
    issues = $null
}

$port = 99
$hostName = 'sync.taskscape.com'
$settingsPath = Join-Path $InstallDirectory 'appsettings.json'
try {
    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    $section = $settings.PSObject.Properties['Rendezvous']
    $rendezvous = if ($null -ne $section) { $section.Value } else { $null }
    if ($null -ne $rendezvous) {
        $configuredPort = $rendezvous.PSObject.Properties['Port']
        $configuredHost = $rendezvous.PSObject.Properties['CertificateSubject']
        if ($null -ne $configuredPort -and $null -ne $configuredPort.Value) { $port = [int]$configuredPort.Value }
        if ($null -ne $configuredHost -and $configuredHost.Value) { $hostName = [string]$configuredHost.Value }
    }
    if ($port -lt 1 -or $port -gt 65535 -or [string]::IsNullOrWhiteSpace($hostName)) {
        throw [System.IO.InvalidDataException]::new('Invalid rendezvous settings')
    }
    $configuredUrl = if ($null -ne $rendezvous) { $rendezvous.PSObject.Properties['PublicUrl'] } else { $null }
    $publicUrl = if ($null -ne $configuredUrl -and $configuredUrl.Value) { [Uri]$configuredUrl.Value } else { [Uri]'https://sync.taskscape.com:99' }
    $matches = $publicUrl.Scheme -eq 'https' -and $publicUrl.Host -eq $hostName -and $publicUrl.Port -eq $port
    $result.configuration = [ordered]@{ readable = $true; port = $port; host = $hostName; publicUrlMatchesListener = $matches; source = 'appsettings.json' }
    if (-not $matches) { $issues.Add('public-url-mismatch') }
} catch {
    $result.configuration = [ordered]@{ readable = $false; errorType = $_.Exception.GetType().Name; source = 'appsettings.json' }
    $issues.Add('settings-unavailable')
}

$service = $null
try {
    $service = Get-CimInstance Win32_Service -Filter "Name='PeerSyncRendezvous'"
    $running = $null -ne $service -and $service.State -eq 'Running' -and $service.ProcessId -gt 0
    $result.service = [ordered]@{ installed = $null -ne $service; running = $running; processId = if ($running) { [int]$service.ProcessId } else { $null } }
    if (-not $running) { $issues.Add('service-not-running') }
} catch {
    $result.service = [ordered]@{ checked = $false; errorType = $_.Exception.GetType().Name }
    $issues.Add('service-state-unavailable')
}

try {
    $now = [DateTime]::UtcNow
    $serverAuth = '1.3.6.1.5.5.7.3.1'
    $eligible = @(Get-ChildItem Cert:\LocalMachine\My | Where-Object {
        $_.HasPrivateKey -and $_.NotBefore.ToUniversalTime() -le $now -and $_.NotAfter.ToUniversalTime() -gt $now -and
        $_.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::DnsName, $false) -eq $hostName -and
        @($_.Extensions | Where-Object { $_ -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] } |
            ForEach-Object { @($_.EnhancedKeyUsages | Where-Object Value -eq $serverAuth) }).Count -gt 0
    })
    $keyUsable = $false
    $grantScript = Join-Path $InstallDirectory 'Grant-RendezvousCertificate.ps1'
    if (Test-Path -LiteralPath $grantScript -PathType Leaf) {
        try {
            $check = @(& $grantScript -DnsName $hostName -CheckOnly)
            $keyUsable = @($check | Where-Object { $_ -like 'CERTIFICATE *' }).Count -eq 1
        } catch {
            # The grant helper also checks that signing works, the public key
            # verifies it and the private-key file is machine-owned. Do not
            # include certificate or provider details in the public JSON.
        }
    }
    $result.certificate = [ordered]@{
        eligibleInLocalMachineMy = $eligible.Count -gt 0
        count = $eligible.Count
        usableMachinePrivateKeyForCaller = $keyUsable
        serviceKeyAccess = 'not directly inspected'
    }
    if ($eligible.Count -eq 0) { $issues.Add('server-certificate-unavailable') }
    elseif (-not $keyUsable) { $issues.Add('server-certificate-key-unusable') }
} catch {
    $result.certificate = [ordered]@{ checked = $false; errorType = $_.Exception.GetType().Name }
    $issues.Add('certificate-check-unavailable')
}

try {
    $tcp = @(Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue)
    $udp = @(Get-NetUDPEndpoint -LocalPort $port -ErrorAction SilentlyContinue)
    $servicePid = if ($null -ne $service -and $service.State -eq 'Running') { [int]$service.ProcessId } else { 0 }
    $tcpOwned = $servicePid -gt 0 -and @($tcp | Where-Object OwningProcess -eq $servicePid).Count -gt 0
    $udpOwned = $servicePid -gt 0 -and @($udp | Where-Object OwningProcess -eq $servicePid).Count -gt 0
    $result.listeners = [ordered]@{ tcpPresent = $tcp.Count -gt 0; udpPresent = $udp.Count -gt 0; tcpOwnedByService = $tcpOwned; udpOwnedByService = $udpOwned }
    if (-not $tcpOwned) { $issues.Add('tcp-listener-missing-or-foreign') }
    if (-not $udpOwned) { $issues.Add('udp-listener-missing-or-foreign') }
} catch {
    $result.listeners = [ordered]@{ checked = $false; errorType = $_.Exception.GetType().Name }
    $issues.Add('listener-check-unavailable')
}

try {
    $allow = [ordered]@{ tcp = $false; udp = $false }
    foreach ($protocol in @('TCP', 'UDP')) {
        $name = "PeerSync Rendezvous $protocol 99"
        $rules = @(Get-NetFirewallRule -PolicyStore ActiveStore -DisplayName $name -ErrorAction SilentlyContinue)
        foreach ($rule in $rules) {
            if ($rule.Enabled -ne 'True' -or $rule.Direction -ne 'Inbound' -or $rule.Action -ne 'Allow') { continue }
            $filters = @($rule | Get-NetFirewallPortFilter)
            if (@($filters | Where-Object {
                $_.Protocol -eq $protocol -and @($_.LocalPort) -contains [string]$port
            }).Count -gt 0) { $allow[$protocol.ToLowerInvariant()] = $true; break }
        }
    }
    $result.firewall = [ordered]@{ tcpAllowRule = $allow.tcp; udpAllowRule = $allow.udp; effectiveExternalFiltering = 'not measured' }
    if (-not $allow.tcp) { $issues.Add('tcp-allow-rule-missing') }
    if (-not $allow.udp) { $issues.Add('udp-allow-rule-missing') }
} catch {
    $result.firewall = [ordered]@{ checked = $false; errorType = $_.Exception.GetType().Name }
    $issues.Add('firewall-check-unavailable')
}

try {
    $records = @(Resolve-DnsName -Name $hostName -Type A -ErrorAction Stop | Where-Object Type -eq 'A')
    $result.dns = [ordered]@{ aRecordPresent = $records.Count -gt 0; addressesOmitted = $true }
    if ($records.Count -eq 0) { $issues.Add('dns-a-record-missing') }
} catch {
    $result.dns = [ordered]@{ checked = $false; errorType = $_.Exception.GetType().Name; addressesOmitted = $true }
    $issues.Add('dns-check-unavailable')
}

$curl = Join-Path $env:SystemRoot 'System32\curl.exe'
if (Test-Path -LiteralPath $curl) {
    try {
        # --resolve keeps the public hostname for SNI and certificate checking,
        # but sends the request only to this machine. Never disable TLS checks.
        # Windows PowerShell 5.1 wraps native stderr as an error record. A
        # refused connection is a measured health failure, not a script error.
        $previousPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $body = & $curl --silent --show-error --fail --noproxy '*' --max-time 5 `
                --resolve "${hostName}:${port}:127.0.0.1" "https://${hostName}:${port}/healthz" 2>&1
            $code = $LASTEXITCODE
        } finally { $ErrorActionPreference = $previousPreference }
        $healthy = $false
        if ($code -eq 0) {
            try { $healthy = ($body | Out-String | ConvertFrom-Json).status -eq 'ok' } catch { }
        }
        $result.localHealth = [ordered]@{ reachable = $healthy; curlExitCode = $code; tlsValidated = $code -eq 0 }
        if (-not $healthy) { $issues.Add('local-health-or-tls-failed') }
    } catch {
        $result.localHealth = [ordered]@{ checked = $false; errorType = $_.Exception.GetType().Name }
        $issues.Add('local-health-check-unavailable')
    }
} else {
    $result.localHealth = [ordered]@{ checked = $false; reason = 'curl-unavailable' }
    $issues.Add('local-health-check-unavailable')
}

$result.issues = @($issues.ToArray())
$result.localReady = $issues.Count -eq 0
$result | ConvertTo-Json -Depth 5 -Compress
if ($issues.Count -ne 0) { exit 1 }
