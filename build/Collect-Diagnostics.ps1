[CmdletBinding()]
param(
    [string]$Logs = "$env:ProgramData/PeerSync/logs",
    [string]$Destination = "./PeerSync-diagnostics-$([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')).zip",
    [switch]$IncludeNetworkAddresses
)
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $Logs).Path
$target = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $target) { throw 'Destination already exists' }
$staging = Join-Path ([IO.Path]::GetTempPath()) ('PeerSync-diagnostics-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $staging | Out-Null
try {
    # Deliberately exclude settings, databases, keys, certificates, arbitrary text logs and crash dumps.
    $cutoff = [DateTimeOffset]::UtcNow.AddHours(-24)
    $index = 0
    foreach ($file in Get-ChildItem -LiteralPath $source -Filter 'debug*.jsonl' -File -Recurse) {
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
        $lines = foreach ($line in [IO.File]::ReadLines($file.FullName)) {
            try {
                $entry = $line | ConvertFrom-Json
                if ([DateTimeOffset]::Parse($entry.timestampUtc) -ge $cutoff) { $line }
            } catch { } # A torn final line must not prevent collection.
        }
        if ($lines) { $lines | Set-Content (Join-Path $staging "debug-$index.jsonl"); $index++ }
    }
    $os = Get-CimInstance Win32_OperatingSystem
    $metadata = [ordered]@{
        collectedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        os = $os.Caption
        osVersion = $os.Version
        architecture = $os.OSArchitecture
        dotnet = @(& dotnet --list-runtimes)
        adapters = @(Get-NetAdapter | Select-Object InterfaceDescription,Status,LinkSpeed)
        services = @(Get-Service -Name PeerSync,PeerSyncRendezvous -ErrorAction SilentlyContinue | Select-Object Name,Status)
    }
    if ($IncludeNetworkAddresses) {
        $metadata.addresses = @(Get-NetIPAddress | Select-Object InterfaceIndex,AddressFamily,IPAddress,PrefixLength)
        $metadata.routes = @(Get-NetRoute | Where-Object DestinationPrefix -in @('0.0.0.0/0','::/0') | Select-Object InterfaceIndex,NextHop,RouteMetric)
    }
    $metadata | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $staging 'environment.json')
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $target
    Write-Output $target
}
finally {
    $resolved = [IO.Path]::GetFullPath($staging)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or (Split-Path $resolved -Leaf) -notlike 'PeerSync-diagnostics-*') {
        throw 'Refusing to remove an unexpected staging path'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
