[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceRoot,
    [Parameter(Mandatory)][string]$DestinationRoot,
    [Parameter(Mandatory)][string]$RemoteComputer,
    [Parameter(Mandatory)][System.Net.IPAddress]$RemoteLanAddress,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [guid]$RunId = [guid]::NewGuid(),
    [ValidateRange(104857600, 10737418240)][long]$FileBytes = 4GB,
    [switch]$PreflightOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Resolve-NewFolder([string]$value, [bool]$checkHere) {
    $path = [IO.Path]::GetFullPath($value).TrimEnd('\')
    if ($path -notmatch '^[A-Za-z]:\\' -or $path.Length -le 3) {
        throw 'Each throughput folder must be a new path below a local drive root.'
    }
    if ($checkHere) {
        if ([IO.Directory]::Exists($path) -or [IO.File]::Exists($path)) {
            throw 'The source throughput folder path already exists.'
        }
        $parent = [IO.Path]::GetDirectoryName($path)
        if (-not [IO.Directory]::Exists($parent) -or
            ([IO.File]::GetAttributes($parent) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'The source throughput folder needs an existing, non-redirected parent.'
        }
    }
    return $path
}

$source = Resolve-NewFolder $SourceRoot $true
$destination = Resolve-NewFolder $DestinationRoot $false
$output = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
if (-not [IO.Directory]::Exists($output) -or
    ([IO.File]::GetAttributes($output) -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
    $output.Equals($source, [StringComparison]::OrdinalIgnoreCase) -or
    $output.StartsWith($source + '\', [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetPathRoot($output) -ne [IO.Path]::GetPathRoot($source)) {
    throw 'Use an existing evidence directory outside the new source folder on the same volume.'
}
if ($RemoteLanAddress.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
    throw 'The destination physical LAN address must be IPv4.'
}

# Adding a folder changes the live service's membership and tracker state.
# Wait until the separate physical soak and its evidence collector finish.
$activeLocal = @(Get-ScheduledTask -TaskName 'PeerSync-PhysicalSoak-*' -ErrorAction SilentlyContinue |
    Where-Object State -eq 'Running')
if ($activeLocal.Count -gt 0) { throw 'A physical soak is still running on this PC.' }
$cli = 'C:\Program Files\PeerSync\cli\psp.exe'
if (-not [IO.File]::Exists($cli) -or (Get-Service PeerSync).Status -ne 'Running') {
    throw 'The installed PeerSync CLI and a running service are required.'
}
$version = (Get-Item 'C:\Program Files\PeerSync\service\PeerSync.Service.exe').VersionInfo.FileVersion
$route = Find-NetRoute -RemoteIPAddress $RemoteLanAddress.ToString() | Select-Object -First 1
if (-not $route -or -not $route.IPAddress -or
    ($route.NextHop -and $route.NextHop -ne '0.0.0.0')) {
    throw 'The destination LAN address is not on-link from this PC.'
}
$sourceLanAddress = [string]$route.IPAddress
$adapter = Get-NetAdapter -Name $route.InterfaceAlias -ErrorAction Stop
if ($adapter.Status -ne 'Up' -or -not $adapter.HardwareInterface -or
    $adapter.TransmitLinkSpeed -lt 1000000000 -or $adapter.ReceiveLinkSpeed -lt 1000000000) {
    throw 'The source must use an active physical link of at least 1 Gbit/s.'
}
$remote = Invoke-Command -ComputerName $RemoteComputer -Authentication Negotiate `
    -ArgumentList $destination, $sourceLanAddress, $RemoteLanAddress.ToString(), $version -ScriptBlock {
    param($root, $firstLan, $expectedLan, $expectedVersion)
    $ErrorActionPreference = 'Stop'
    if ([IO.Directory]::Exists($root) -or [IO.File]::Exists($root)) {
        throw 'The remote throughput folder path already exists.'
    }
    $parent = [IO.Path]::GetDirectoryName($root)
    if (-not [IO.Directory]::Exists($parent) -or
        ([IO.File]::GetAttributes($parent) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The remote throughput parent is missing or redirected.'
    }
    if (@(Get-ScheduledTask -TaskName 'PeerSync-PhysicalSoak-*' -ErrorAction SilentlyContinue |
        Where-Object State -eq 'Running').Count -gt 0) {
        throw 'A physical soak is still running on the remote PC.'
    }
    if ((Get-Service PeerSync).Status -ne 'Running' -or
        (Get-Item 'C:\Program Files\PeerSync\service\PeerSync.Service.exe').VersionInfo.FileVersion -ne $expectedVersion) {
        throw 'The remote service is stopped or has a different version.'
    }
    $route = Find-NetRoute -RemoteIPAddress $firstLan | Select-Object -First 1
    if (-not $route -or [string]$route.IPAddress -ne $expectedLan -or
        ($route.NextHop -and $route.NextHop -ne '0.0.0.0')) {
        throw 'The remote physical LAN route does not point back to the source.'
    }
    $adapter = Get-NetAdapter -Name $route.InterfaceAlias -ErrorAction Stop
    if ($adapter.Status -ne 'Up' -or -not $adapter.HardwareInterface -or
        $adapter.TransmitLinkSpeed -lt 1000000000 -or $adapter.ReceiveLinkSpeed -lt 1000000000) {
        throw 'The remote link is not an active physical 1 Gbit/s adapter.'
    }
    [pscustomobject]@{ Interface=$route.InterfaceAlias; FreeBytes=[long](Get-PSDrive -Name $root.Substring(0,1)).Free }
}
$preflight = [ordered]@{
    schema='peersync-lan-throughput-preparation-v1'; runId=$RunId.ToString('N')
    checkedUtc=[DateTime]::UtcNow.ToString('o'); sourceRoot=$source
    destinationRoot=$destination; remoteComputer=$RemoteComputer
    sourceLanAddress=$sourceLanAddress; destinationLanAddress=$RemoteLanAddress.ToString()
    sourceInterface=$route.InterfaceAlias; destinationInterface=$remote.Interface
    sourceFreeBytes=[long](Get-PSDrive -Name $source.Substring(0,1)).Free
    destinationFreeBytes=[long]$remote.FreeBytes; fileBytes=$FileBytes; serviceVersion=$version
}
if ($preflight.sourceFreeBytes -lt (2 * $FileBytes + 5GB) -or
    $preflight.destinationFreeBytes -lt (2 * $FileBytes + 5GB)) {
    throw 'The two PCs need space for the native test file, block store and headroom.'
}
if ($PreflightOnly) { [pscustomobject]$preflight | ConvertTo-Json -Depth 4; return }

$reportPath = Join-Path $output ('lan-throughput-preparation-' + $RunId.ToString('N') + '.json')
if ([IO.File]::Exists($reportPath)) { throw 'The run-specific preparation report already exists.' }
$stage = 'create_source'
$key = $null
try {
    [IO.Directory]::CreateDirectory($source) | Out-Null
    $stage = 'add_source'
    $added = (& $cli add $source --json 2>$null | Out-String | ConvertFrom-Json)
    if ($LASTEXITCODE -ne 0 -or $added.folderId -notmatch '^[0-9A-Fa-f]{64}$' -or
        [string]::IsNullOrWhiteSpace($added.shareKey)) {
        throw 'The installed source AddFolder command did not return a valid result.'
    }
    $key = [string]$added.shareKey
    $stage = 'join_destination'
    $joined = Invoke-Command -ComputerName $RemoteComputer -Authentication Negotiate `
        -ArgumentList $destination, $key, $added.folderId -ScriptBlock {
        param($root, $shareKey, $expectedFolderId)
        $ErrorActionPreference = 'Stop'
        [IO.Directory]::CreateDirectory($root) | Out-Null
        $cli = 'C:\Program Files\PeerSync\cli\psp.exe'
        $result = (& $cli join $shareKey $root --json 2>$null | Out-String | ConvertFrom-Json)
        if ($LASTEXITCODE -ne 0 -or $result.folderId -ne $expectedFolderId) {
            throw 'The remote JoinFolder command did not join the new folder.'
        }
        return $true
    }
    if ($joined -ne $true) { throw 'The remote join was not confirmed.' }
    $stage = 'wait_for_authenticated_session'
    $measure = Join-Path $PSScriptRoot 'Measure-InstalledLanThroughput.ps1'
    $measurement = @{SourceRoot=$source;DestinationRoot=$destination;
        RemoteComputer=$RemoteComputer;RemoteLanAddress=$RemoteLanAddress;
        OutputDirectory=$output;RunId=$RunId;FileBytes=$FileBytes}
    $ready = $null
    $deadline = [DateTime]::UtcNow.AddMinutes(3)
    do {
        try { $ready = & $measure @measurement -PreflightOnly | ConvertFrom-Json; break }
        catch { Start-Sleep -Seconds 3 }
    } while ([DateTime]::UtcNow -lt $deadline)
    if (-not $ready) { throw 'The new folder did not reach an authenticated ready state in three minutes.' }
    $safe = [ordered]@{ preflight=$preflight; status='ready'; readyUtc=[DateTime]::UtcNow.ToString('o');
        folderFingerprint=$ready.folderFingerprint; measurementRunId=$RunId.ToString('N') }
    $safe | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $reportPath -Encoding UTF8
    Write-Output "READY report=$reportPath"
} catch {
    [ordered]@{ preflight=$preflight; status='failed'; stage=$stage;
        errorType=$_.Exception.GetType().Name; failedUtc=[DateTime]::UtcNow.ToString('o') } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $reportPath -Encoding UTF8
    throw "Throughput folder preparation failed at $stage. Inspect $reportPath and service diagnostics."
} finally { $key = $null }
