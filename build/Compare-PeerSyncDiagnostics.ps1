param(
    [Parameter(Mandatory = $true)]
    [ValidateCount(2, 3)]
    [string[]] $Bundle,
    [ValidatePattern('^[0-9A-Fa-f]{16}$')]
    [string] $FolderFingerprint,
    [datetime] $FromUtc,
    [datetime] $ToUtc
)

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$events = [System.Collections.Generic.List[object]]::new()

foreach ($path in $Bundle) {
    $resolved = (Resolve-Path -LiteralPath $path -ErrorAction Stop).ProviderPath
    $archive = [System.IO.Compression.ZipFile]::OpenRead($resolved)
    try {
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName -notmatch '^debug-[0-9]{3}\.jsonl$') { continue }
            $reader = [System.IO.StreamReader]::new($entry.Open())
            try {
                while ($null -ne ($line = $reader.ReadLine())) {
                    try { $item = $line | ConvertFrom-Json -ErrorAction Stop }
                    catch { continue }
                    if ($item.eventName -notmatch '^(tracker\.|connection\.|service\.(network_|peer_candidate_|relay_|folder_peer_ready|folder_sync_failed|folder_sync_pass)|rendezvous\.(relay_|session_|announced|folder_announced)|transport\.(folder_auth_|handshake_failed))') { continue }
                    if ($item.eventName -eq 'service.folder_sync_pass' -and
                        $item.itemsChanged -eq 0 -and $item.itemsDeferred -eq 0) { continue }
                    if ($item.timestampUtc -is [datetime]) {
                        $time = [datetimeoffset]($item.timestampUtc.ToUniversalTime())
                    }
                    else {
                        $time = [datetimeoffset]::Parse([string]$item.timestampUtc,
                            [System.Globalization.CultureInfo]::InvariantCulture,
                            [System.Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
                    }
                    if ($PSBoundParameters.ContainsKey('FromUtc') -and $time -lt $FromUtc.ToUniversalTime()) { continue }
                    if ($PSBoundParameters.ContainsKey('ToUtc') -and $time -gt $ToUtc.ToUniversalTime()) { continue }
                    $events.Add([pscustomobject]@{
                        TimestampUtc = $time.ToString('o')
                        Source = [System.IO.Path]::GetFileName($resolved)
                        Event = $item.eventName
                        Folder = $item.folderFingerprint
                        Peer = $item.peerFingerprint
                        Session = $item.sessionId
                        Operation = $item.operation
                        Transport = $item.transport
                        Seen = $item.itemsVisited
                        Changed = $item.itemsChanged
                        Deferred = $item.itemsDeferred
                        Error = $item.errorCode
                        RetryMs = $item.retryInMs
                    })
                }
            }
            finally { $reader.Dispose() }
        }
    }
    finally { $archive.Dispose() }
}

if ($FolderFingerprint) {
    $relatedPeers = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($event in $events) {
        if ($event.Folder -eq $FolderFingerprint -and $event.Peer) {
            [void] $relatedPeers.Add($event.Peer)
        }
    }
    $events | Where-Object { $_.Folder -eq $FolderFingerprint -or
        ($_.Peer -and $relatedPeers.Contains($_.Peer)) } | Sort-Object TimestampUtc, Source
}
else { $events | Sort-Object TimestampUtc, Source }
