[CmdletBinding()]
param([string]$DnsName = 'sync.taskscape.com')
$ErrorActionPreference = 'Stop'
$import = Join-Path $PSScriptRoot 'Import-RendezvousWebHostingCertificate.ps1'
$grant = Join-Path $PSScriptRoot 'Grant-RendezvousCertificate.ps1'
& $import -DnsName $DnsName
& $grant -DnsName $DnsName
Restart-Service PeerSyncRendezvous -Force -ErrorAction Stop
Write-Output 'RENDEZVOUS_RESTARTED'
