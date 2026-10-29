[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceRoot,
    [Parameter(Mandatory)][string]$DestinationRoot,
    [Parameter(Mandatory)][string]$RemoteComputer,
    [Parameter(Mandatory)][System.Net.IPAddress]$RemoteLanAddress,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [guid]$RunId = [guid]::NewGuid(),
    [ValidateRange(104857600, 10737418240)][long]$FileBytes = 4GB,
    [ValidateRange(60, 10800)][int]$TimeoutSeconds = 10800,
    [switch]$PreflightOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Get-FolderFingerprint([string]$folderId) {
    if ($folderId -notmatch '^[0-9A-Fa-f]{64}$') { throw 'Service returned an invalid FolderId.' }
    $bytes = New-Object byte[] 32
    for ($i = 0; $i -lt 32; $i++) {
        $bytes[$i] = [Convert]::ToByte($folderId.Substring(2 * $i, 2), 16)
    }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '')).Substring(0, 16) }
    finally { $sha.Dispose() }
}
if ($RemoteLanAddress.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
    throw 'The measured physical LAN endpoint must be IPv4.'
}
$source = [IO.Path]::GetFullPath($SourceRoot).TrimEnd('\')
$destination = [IO.Path]::GetFullPath($DestinationRoot).TrimEnd('\')
$output = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
if ($source.Length -le 3 -or $destination.Length -le 3 -or $output.Length -le 3) {
    throw 'Use dedicated directories below volume roots.'
}
foreach ($path in @($source, $destination, $output)) {
    if ($path -notmatch '^[A-Za-z]:\\') { throw 'Use local drive-letter paths on each host.' }
}
if ($output.Equals($source, [StringComparison]::OrdinalIgnoreCase) -or
    $output.StartsWith($source + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The evidence and staging directory must be outside the synchronized source.'
}
if (-not [IO.Directory]::Exists($source) -or -not [IO.Directory]::Exists($output)) {
    throw 'The source and evidence directories must already exist.'
}
if ([IO.Path]::GetPathRoot($source) -ne [IO.Path]::GetPathRoot($output)) {
    throw 'The source and staging directories must be on the same volume for an atomic test-file move.'
}
foreach ($path in @($source, $output)) {
    if (([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Test directories cannot be reparse points.'
    }
}
if (@(Get-ChildItem -LiteralPath $source -Force | Where-Object Name -ne '.psp').Count -ne 0) {
    throw 'Use a new, empty, already joined source folder for this isolated throughput run.'
}
$cli = 'C:\Program Files\PeerSync\cli\psp.exe'
if (-not [IO.File]::Exists($cli)) { throw 'The installed PeerSync CLI is missing.' }
$localService = Get-Service PeerSync
if ($localService.Status -ne 'Running') { throw 'The local PeerSync service is not running.' }
$localVersion = (Get-Item 'C:\Program Files\PeerSync\service\PeerSync.Service.exe').VersionInfo.FileVersion
$localStatus = (& $cli status --json | Out-String | ConvertFrom-Json)
if ($LASTEXITCODE -ne 0 -or -not $localStatus.productionSyncAvailable) {
    throw 'The local installed service cannot perform production sync.'
}
$localPeers = (& $cli peers $source --json | Out-String | ConvertFrom-Json)
if ($LASTEXITCODE -ne 0 -or @($localPeers.peers).Count -ne 1 -or
    -not $localPeers.peers[0].pinned -or -not $localPeers.peers[0].transport) {
    throw 'The isolated source folder must have one authenticated, connected peer.'
}
$localFingerprint = Get-FolderFingerprint ([string]$localPeers.folderId)
if (@($localStatus.peers | Where-Object {
    $_.folderFingerprint -eq $localFingerprint -and
    $_.deviceId -eq $localPeers.peers[0].deviceId -and
    $_.connected -and $_.clockState -eq 'Accepted'
}).Count -eq 0) {
    throw 'The source folder has no live clock-accepted session to its pinned peer.'
}
$routeParts = @(Find-NetRoute -RemoteIPAddress $RemoteLanAddress.ToString())
$route = $routeParts | Where-Object { $_.CimClass.CimClassName -eq 'MSFT_NetIPAddress' } |
    Select-Object -First 1
$networkRoute = $routeParts | Where-Object { $_.CimClass.CimClassName -eq 'MSFT_NetRoute' } |
    Select-Object -First 1
if (-not $route -or -not $route.IPAddress -or -not $networkRoute -or
    $networkRoute.InterfaceIndex -ne $route.InterfaceIndex -or
    $networkRoute.NextHop -ne '0.0.0.0') {
    throw 'The remote LAN endpoint is not on an on-link route.'
}
$sourceLanAddress = [string]$route.IPAddress
$adapter = Get-NetAdapter -Name $route.InterfaceAlias -ErrorAction Stop
if ($adapter.Status -ne 'Up' -or -not $adapter.HardwareInterface -or
    $adapter.TransmitLinkSpeed -lt 1000000000 -or $adapter.ReceiveLinkSpeed -lt 1000000000) {
    throw 'The source route must use an active physical adapter at 1 Gbit/s or faster.'
}
$remote = Invoke-Command -ComputerName $RemoteComputer -Authentication Negotiate `
    -ArgumentList $destination, $sourceLanAddress, $RemoteLanAddress.ToString(), $localVersion, $FileBytes -ScriptBlock {
    param($root, $firstLan, $expectedRemoteLan, $expectedVersion, $bytes)
    $ErrorActionPreference = 'Stop'
    if (-not [IO.Directory]::Exists($root)) { throw 'The destination folder is missing.' }
    if (([IO.File]::GetAttributes($root) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The destination folder is a reparse point.'
    }
    if (@(Get-ChildItem -LiteralPath $root -Force | Where-Object Name -ne '.psp').Count -ne 0) {
        throw 'Use a new, empty, already joined destination folder.'
    }
    if ((Get-Service PeerSync).Status -ne 'Running') { throw 'The remote PeerSync service is not running.' }
    $version = (Get-Item 'C:\Program Files\PeerSync\service\PeerSync.Service.exe').VersionInfo.FileVersion
    if ($version -ne $expectedVersion) { throw 'Both installed services must use the same build.' }
    $cli = 'C:\Program Files\PeerSync\cli\psp.exe'
    $status = (& $cli status --json | Out-String | ConvertFrom-Json)
    if ($LASTEXITCODE -ne 0 -or -not $status.productionSyncAvailable) {
        throw 'The remote installed service cannot perform production sync.'
    }
    $peers = (& $cli peers $root --json | Out-String | ConvertFrom-Json)
    if ($LASTEXITCODE -ne 0 -or @($peers.peers).Count -ne 1 -or
        -not $peers.peers[0].pinned -or -not $peers.peers[0].transport) {
        throw 'The isolated destination folder must have one authenticated, connected peer.'
    }
    $folderId = [string]$peers.folderId
    if ($folderId -notmatch '^[0-9A-Fa-f]{64}$') { throw 'Service returned an invalid FolderId.' }
    $folderBytes = New-Object byte[] 32
    for ($i = 0; $i -lt 32; $i++) {
        $folderBytes[$i] = [Convert]::ToByte($folderId.Substring(2 * $i, 2), 16)
    }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $fingerprint = ([BitConverter]::ToString($sha.ComputeHash($folderBytes)).Replace('-', '')).Substring(0, 16) }
    finally { $sha.Dispose() }
    if (@($status.peers | Where-Object {
        $_.folderFingerprint -eq $fingerprint -and
        $_.deviceId -eq $peers.peers[0].deviceId -and
        $_.connected -and $_.clockState -eq 'Accepted'
    }).Count -eq 0) {
        throw 'The destination folder has no live clock-accepted session to its pinned peer.'
    }
    $routeParts = @(Find-NetRoute -RemoteIPAddress $firstLan)
    $route = $routeParts | Where-Object { $_.CimClass.CimClassName -eq 'MSFT_NetIPAddress' } |
        Select-Object -First 1
    $networkRoute = $routeParts | Where-Object { $_.CimClass.CimClassName -eq 'MSFT_NetRoute' } |
        Select-Object -First 1
    if (-not $route -or [string]$route.IPAddress -ne $expectedRemoteLan -or
        -not $networkRoute -or $networkRoute.InterfaceIndex -ne $route.InterfaceIndex -or
        $networkRoute.NextHop -ne '0.0.0.0') {
        throw 'The destination LAN source does not match the on-link route to the first PC.'
    }
    $adapter = Get-NetAdapter -Name $route.InterfaceAlias -ErrorAction Stop
    if ($adapter.Status -ne 'Up' -or -not $adapter.HardwareInterface -or
        $adapter.TransmitLinkSpeed -lt 1000000000 -or $adapter.ReceiveLinkSpeed -lt 1000000000) {
        throw 'The destination route must use an active physical adapter at 1 Gbit/s or faster.'
    }
    $drive = Get-PSDrive -Name ([IO.Path]::GetPathRoot($root).Substring(0,1))
    if ($drive.Free -lt 2 * $bytes + 5GB) { throw 'The destination lacks space for native bytes, blocks and headroom.' }
    [pscustomobject]@{FolderId=$peers.folderId;Address=[string]$route.IPAddress;
        Interface=$route.InterfaceAlias;LinkBitsPerSecond=[long]$adapter.ReceiveLinkSpeed;
        Version=$version;FreeBytes=[long]$drive.Free}
}
if ($localPeers.folderId -ne $remote.FolderId) {
    throw 'The two prepared folders do not have the same FolderId.'
}
$drive = Get-PSDrive -Name ([IO.Path]::GetPathRoot($source).Substring(0,1))
if ($drive.Free -lt 2 * $FileBytes + 5GB) {
    throw 'The source lacks space for native bytes, blocks and headroom.'
}
$preflight = [ordered]@{
    schema = 'peersync-installed-lan-throughput-v1'; runId = $RunId.ToString('N')
    checkedUtc = [DateTime]::UtcNow.ToString('o'); serviceVersion = $localVersion
    folderFingerprint = $localFingerprint
    fileBytes = $FileBytes; byteBudget = 10GB; timeoutSeconds = $TimeoutSeconds
    sourceLanAddress = $sourceLanAddress; destinationLanAddress = $RemoteLanAddress.ToString()
    sourceInterface = $route.InterfaceAlias; destinationInterface = $remote.Interface
    sourceLinkBitsPerSecond = [long]$adapter.TransmitLinkSpeed
    destinationLinkBitsPerSecond = $remote.LinkBitsPerSecond
    sourceFreeBytes = [long]$drive.Free; destinationFreeBytes = $remote.FreeBytes
}
if ($PreflightOnly) { $preflight | ConvertTo-Json -Depth 4; return }

$runStarted = [DateTime]::UtcNow
$name = 'lan-throughput-' + $RunId.ToString('N') + '.bin'
$staged = Join-Path $output ($name + '.staging')
$sourceFile = Join-Path $source $name
$destinationFile = Join-Path $destination $name
if ([IO.File]::Exists($staged) -or [IO.File]::Exists($sourceFile)) {
    throw 'The run-specific test file already exists.'
}
$reportPath = Join-Path $output ('lan-throughput-' + $RunId.ToString('N') + '.json')
if ([IO.File]::Exists($reportPath)) { throw 'The run-specific report already exists.' }
$report = [ordered]@{ preflight=$preflight; status='running'; startedUtc=$runStarted.ToString('o');
    testFile=$name; sourceHashSha256=$null; moveStartedUtc=$null; verifiedUtc=$null;
    transferSeconds=$null; effectiveBytesPerSecond=$null; targetBytesPerSecond=100000000;
    directLanBlockBytes=$null; directLanBlockRequests=$null; directLanSessions=$null;
    directLanTransports=$null; stagedFileRemoved=$false; passed=$false; failure=$null }
try {
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    $buffer = New-Object byte[] (1024 * 1024)
    try {
        $stream = [IO.FileStream]::new($staged, [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write, [IO.FileShare]::None, $buffer.Length,
            [IO.FileOptions]::SequentialScan)
        try {
            [long]$remaining = $FileBytes
            while ($remaining -gt 0) {
                $rng.GetBytes($buffer)
                $length = [int][Math]::Min($remaining, [long]$buffer.Length)
                $stream.Write($buffer, 0, $length)
                $remaining -= $length
                if (([DateTime]::UtcNow - $runStarted).TotalSeconds -ge $TimeoutSeconds) {
                    throw 'The active three-hour test deadline expired during fixture generation.'
                }
            }
            $stream.Flush($true)
        } finally { $stream.Dispose() }
    } finally { $rng.Dispose() }
    $hash = (Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash
    $report.sourceHashSha256 = $hash
    $moveStarted = [DateTime]::UtcNow
    $report.moveStartedUtc = $moveStarted.ToString('o')
    [IO.File]::Move($staged, $sourceFile)
    while ($true) {
        if (([DateTime]::UtcNow - $runStarted).TotalSeconds -ge $TimeoutSeconds) {
            throw 'The active three-hour test deadline expired before verified convergence.'
        }
        $observation = Invoke-Command -ComputerName $RemoteComputer -Authentication Negotiate `
            -ArgumentList $destinationFile, $FileBytes -ScriptBlock {
            param($path, $expectedBytes)
            if (-not [IO.File]::Exists($path)) { return [pscustomobject]@{Length=-1;Hash=$null;Utc=$null} }
            $length = ([IO.FileInfo]::new($path)).Length
            if ($length -ne $expectedBytes) { return [pscustomobject]@{Length=$length;Hash=$null;Utc=$null} }
            $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
            [pscustomobject]@{Length=$length;Hash=$hash;Utc=[DateTime]::UtcNow.ToString('o')}
        }
        if ($observation.Hash) {
            if ($observation.Hash -ne $hash) { throw 'The destination native SHA-256 does not match the source.' }
            $verified = [DateTime]::Parse($observation.Utc).ToUniversalTime()
            $seconds = ($verified - $moveStarted).TotalSeconds
            $report.verifiedUtc = $verified.ToString('o')
            $report.transferSeconds = $seconds
            $report.effectiveBytesPerSecond = [long][Math]::Floor($FileBytes / $seconds)
            # A route preflight alone does not prove which endpoint the service
            # used. Attribute at least the new file's bytes to native LAN block
            # responses in the receiver's active service journal.
            $lanBlocks = Invoke-Command -ComputerName $RemoteComputer -Authentication Negotiate `
                -ArgumentList $sourceLanAddress, $moveStarted.ToString('o'), $verified.ToString('o') -ScriptBlock {
                param($firstLan, $fromText, $untilText)
                $from = [DateTimeOffset]::Parse($fromText).UtcDateTime
                $until = [DateTimeOffset]::Parse($untilText).UtcDateTime
                $service = Get-CimInstance Win32_Service -Filter "Name='PeerSync'"
                if (-not $service -or $service.State -ne 'Running') { throw 'The receiver service stopped during transfer.' }
                $pidOfService = [int]$service.ProcessId
                $latest = Get-ChildItem 'C:\ProgramData\PeerSync\logs\service' -Recurse -File -Filter 'debug.jsonl' |
                    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
                if (-not $latest) { throw 'The receiver service journal is unavailable.' }
                [long]$total = 0
                [long]$count = 0
                $sessions = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
                $transports = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
                foreach ($file in Get-ChildItem -LiteralPath $latest.DirectoryName -File -Filter 'debug*.jsonl') {
                    foreach ($line in [IO.File]::ReadLines($file.FullName)) {
                        if ($line.IndexOf('"eventName":"transport.request_complete"',
                                [StringComparison]::Ordinal) -lt 0 -or
                            $line.IndexOf('"remoteAddress":"' + $firstLan + '"',
                                [StringComparison]::Ordinal) -lt 0) { continue }
                        $event = $line | ConvertFrom-Json
                        if ($event.processId -ne $pidOfService -or
                            $event.operation -notin @('quic_block','tcp_block') -or
                            $event.bytes -le 0) { continue }
                        $at = if ($event.timestampUtc -is [DateTime]) {
                            $event.timestampUtc.ToUniversalTime()
                        } else {
                            [DateTimeOffset]::Parse([string]$event.timestampUtc).UtcDateTime
                        }
                        if ($at -lt $from -or $at -gt $until) { continue }
                        $total += [long]$event.bytes
                        $count++
                        if ($event.sessionId) { [void]$sessions.Add([string]$event.sessionId) }
                        if ($event.transport) { [void]$transports.Add([string]$event.transport) }
                    }
                }
                [pscustomobject]@{Bytes=$total;Requests=$count;Sessions=@($sessions);
                    Transports=@($transports)}
            }
            $report.directLanBlockBytes = [long]$lanBlocks.Bytes
            $report.directLanBlockRequests = [long]$lanBlocks.Requests
            $report.directLanSessions = @($lanBlocks.Sessions)
            $report.directLanTransports = @($lanBlocks.Transports)
            $report.status = 'verified'
            $report.passed = ($seconds -gt 0 -and $report.effectiveBytesPerSecond -ge 100000000 -and
                $report.directLanBlockBytes -ge $FileBytes -and
                ($verified - $runStarted).TotalSeconds -le $TimeoutSeconds)
            break
        }
        Start-Sleep -Seconds 2
    }
} catch {
    $report.status = 'failed'
    $report.failure = $_.Exception.GetType().Name + ': ' + $_.Exception.Message
} finally {
    if ([IO.File]::Exists($staged)) {
        try { [IO.File]::Delete($staged); $report.stagedFileRemoved = $true }
        catch { $report.failure = ($report.failure + '; staging cleanup failed: ' + $_.Exception.GetType().Name) }
    }
    $report.endedUtc = [DateTime]::UtcNow.ToString('o')
    $report.activeSeconds = ([DateTime]::UtcNow - $runStarted).TotalSeconds
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding UTF8
    Write-Output "REPORT $reportPath"
    Write-Output "RESULT status=$($report.status) passed=$($report.passed) bytesPerSecond=$($report.effectiveBytesPerSecond)"
}
if ($report.status -eq 'failed') { throw $report.failure }
if (-not $report.passed) { throw 'Verified transfer did not meet the 80%-of-1-Gbit/s end-to-end threshold.' }
