[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ServiceExecutable,

    [Parameter(Mandatory)]
    [ValidateRange(1, 65535)]
    [int] $ListenPort,

    [Parameter(Mandatory)]
    [string] $PeerAddress,

    [switch] $EnableLanDiscovery
)

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell session.'
}

$resolvedExecutable = (Resolve-Path -LiteralPath $ServiceExecutable).Path
if (-not (Test-Path -LiteralPath $resolvedExecutable -PathType Leaf)) {
    throw "Service executable was not found: $ServiceExecutable"
}

$parsedAddress = $null
if (-not [System.Net.IPAddress]::TryParse($PeerAddress, [ref] $parsedAddress)) {
    throw "PeerAddress must be a literal IPv4 or IPv6 address: $PeerAddress"
}
$peer = $parsedAddress.ToString()
$safePeer = $peer -replace '[^A-Za-z0-9_.-]', '_'
$rulePrefix = "PeerSync test $ListenPort $safePeer"
$ruleGroup = 'PeerSync Test Setup'

function Add-PeerSyncFirewallRule {
    param(
        [Parameter(Mandatory)] [string] $DisplayName,
        [Parameter(Mandatory)] [ValidateSet('Inbound', 'Outbound')] [string] $Direction,
        [Parameter(Mandatory)] [ValidateSet('TCP', 'UDP')] [string] $Protocol,
        [Parameter(Mandatory)] [string] $Port,
        [Parameter(Mandatory)] [string[]] $RemoteAddress
    )

    # Replace only this helper's rules so rerunning it applies the current
    # executable, peer, and port without accumulating stale entries.
    Get-NetFirewallRule -DisplayName $DisplayName -ErrorAction SilentlyContinue |
        Where-Object Group -eq $ruleGroup | Remove-NetFirewallRule

    $portParameters = if ($Direction -eq 'Inbound') {
        @{ LocalPort = $Port }
    }
    else {
        @{ RemotePort = $Port }
    }

    New-NetFirewallRule -DisplayName $DisplayName -Direction $Direction `
        -Group $ruleGroup -Action Allow -Enabled True -Profile Any -Protocol $Protocol `
        -Program $resolvedExecutable -RemoteAddress $RemoteAddress @portParameters |
        Out-Null
}

foreach ($protocol in 'TCP', 'UDP') {
    Add-PeerSyncFirewallRule -DisplayName "$rulePrefix $protocol inbound" `
        -Direction Inbound -Protocol $protocol -Port "$ListenPort" -RemoteAddress $peer
    Add-PeerSyncFirewallRule -DisplayName "$rulePrefix $protocol outbound" `
        -Direction Outbound -Protocol $protocol -Port "$ListenPort" -RemoteAddress $peer
}

if ($EnableLanDiscovery) {
    Add-PeerSyncFirewallRule -DisplayName 'PeerSync discovery inbound' `
        -Direction Inbound -Protocol UDP -Port '47820' -RemoteAddress @('LocalSubnet')
    Add-PeerSyncFirewallRule -DisplayName 'PeerSync discovery outbound' `
        -Direction Outbound -Protocol UDP -Port '47820' `
        -RemoteAddress @('LocalSubnet', '239.255.80.83', 'ff12::5053')
}

Write-Output "Configured PeerSync firewall rules for $peer on TCP/UDP port $ListenPort."
if ($EnableLanDiscovery) {
    Write-Output 'Configured LocalSubnet-scoped UDP discovery rules on port 47820.'
}
