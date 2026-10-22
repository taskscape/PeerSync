[CmdletBinding()]
param([string]$DnsName = 'sync.taskscape.com')
$ErrorActionPreference = 'Stop'
$grant = Join-Path $PSScriptRoot 'Grant-RendezvousCertificate.ps1'
& $grant -DnsName $DnsName
Restart-Service PeerSyncRendezvous -Force -ErrorAction Stop
Write-Output 'RENDEZVOUS_RESTARTED'
