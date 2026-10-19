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
    $collectedEvents = 0
    $expiredEvents = 0
    $invalidEvents = 0
    $debugFiles = @(Get-ChildItem -LiteralPath $source -Filter 'debug*.jsonl' -File -Recurse)
    if ($debugFiles.Count -eq 0) {
        throw "No debug*.jsonl files found under '$source'. Pass -Logs with the exact LOGS directory printed by the probe after reproducing the failure."
    }
    foreach ($file in $debugFiles) {
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
        # Collection must coexist with the live writer and with file rotation.
        try {
            $stream = [IO.FileStream]::new($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read,
                ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        } catch [IO.FileNotFoundException] { continue } # Rotation may win the race after enumeration.
        $reader = [IO.StreamReader]::new($stream)
        try {
            $lines = while (-not $reader.EndOfStream) {
                $line = $reader.ReadLine()
                try {
                    $entry = $line | ConvertFrom-Json
                # PowerShell 7.5+ converts ISO timestamps into DateTime automatically.
                # Parsing that object via its culture-dependent string can silently discard every event.
                    $timestamp = if ($entry.timestampUtc -is [DateTime]) {
                        [DateTimeOffset]::new($entry.timestampUtc)
                    } else {
                        [DateTimeOffset]::Parse([string]$entry.timestampUtc, [Globalization.CultureInfo]::InvariantCulture)
                    }
                    if ($timestamp -ge $cutoff) { $line } else { $expiredEvents++ }
                } catch { $invalidEvents++ } # A torn final line must not prevent collection.
            }
        } finally { $reader.Dispose() }
        if ($lines) {
            $lines | Set-Content (Join-Path $staging "debug-$index.jsonl")
            $collectedEvents += @($lines).Count
            $index++
        }
    }
    if ($collectedEvents -eq 0) {
        throw "No recent valid debug events found under '$source' (files=$($debugFiles.Count), expired=$expiredEvents, invalid=$invalidEvents). Reproduce the failure, check both PCs' clocks, and collect the LOGS directory printed by that run."
    }
    $os = Get-CimInstance Win32_OperatingSystem
    $metadata = [ordered]@{
        collectedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        collection = [ordered]@{
            debugFilesFound = $debugFiles.Count
            debugFilesIncluded = $index
            eventsIncluded = $collectedEvents
            expiredEvents = $expiredEvents
            invalidEvents = $invalidEvents
        }
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
    Write-Host "Collected $collectedEvents events from $index debug files under '$source'."
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
