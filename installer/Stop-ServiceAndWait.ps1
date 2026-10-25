[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('PeerSync', 'PeerSyncRendezvous')]
    [string]$ServiceName,
    [ValidateRange(1, 120)]
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.ServiceProcess
$serviceInfo = Get-CimInstance Win32_Service -Filter "Name = '$ServiceName'"
if ($null -eq $serviceInfo) {
    Write-Output "SERVICE_ABSENT name=$ServiceName"
    exit 0
}

$imagePath = [string]$serviceInfo.PathName
if ($imagePath -match '^\s*"([^"]+\.exe)"') {
    $imagePath = $Matches[1]
} elseif ($imagePath -match '^\s*(\S+\.exe)(?:\s|$)') {
    $imagePath = $Matches[1]
} else {
    throw "Cannot identify the $ServiceName service image. Refusing to replace files."
}
$imagePath = [IO.Path]::GetFullPath($imagePath)
$imageName = [IO.Path]::GetFileName($imagePath)
$capturedProcessId = [int]$serviceInfo.ProcessId
$deadline = [Diagnostics.Stopwatch]::StartNew()
$controller = [System.ServiceProcess.ServiceController]::new($ServiceName)
try {
    while ($true) {
        $controller.Refresh()
        if ($controller.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Stopped) { break }
        if ($deadline.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
            throw "The $ServiceName service did not report STOPPED within $TimeoutSeconds seconds."
        }
        if ($controller.Status -in @(
            [System.ServiceProcess.ServiceControllerStatus]::Running,
            [System.ServiceProcess.ServiceControllerStatus]::Paused)) {
            $controller.Stop()
            Write-Output "STOP_REQUESTED name=$ServiceName pid=$capturedProcessId"
        }
        Start-Sleep -Milliseconds 200
    }

    # SCM may report STOPPED before the old process releases its listening
    # sockets and image files. Include its captured PID and any process still
    # running the service's exact configured image (including a stale process
    # from an earlier attempt that was already STOPPED at entry).
    while ($true) {
        $matching = @(Get-CimInstance Win32_Process -Filter "Name = '$imageName'" |
            Where-Object {
                if ([int]$_.ProcessId -eq $capturedProcessId -and $capturedProcessId -gt 0) { return $true }
                if ([string]::IsNullOrWhiteSpace($_.ExecutablePath)) {
                    throw "Cannot inspect a $imageName process path. Refusing to replace files."
                }
                return [string]::Equals([IO.Path]::GetFullPath($_.ExecutablePath),
                    $imagePath, [StringComparison]::OrdinalIgnoreCase)
            })
        if ($matching.Count -eq 0) { break }
        if ($deadline.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
            throw "The $ServiceName service process did not exit within $TimeoutSeconds seconds."
        }
        Start-Sleep -Milliseconds 200
    }

    $controller.Refresh()
    if ($controller.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        throw "The $ServiceName service restarted while waiting for its process to exit."
    }
    Write-Output "STOPPED_AND_EXITED name=$ServiceName"
} finally {
    $controller.Dispose()
}
