[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ExecutablePath,
    [ValidateRange(1, 120)]
    [int]$TimeoutSeconds = 20
)

$ErrorActionPreference = 'Stop'
$expected = [IO.Path]::GetFullPath($ExecutablePath)
if ([IO.Path]::GetFileName($expected) -ne 'PeerSync.exe') {
    throw 'The requested dashboard image is not PeerSync.exe.'
}

function Get-InstalledDashboard {
    @(Get-CimInstance Win32_Process -Filter "Name = 'PeerSync.exe'" |
        Where-Object {
            if ([string]::IsNullOrWhiteSpace($_.ExecutablePath)) {
                throw 'Cannot identify a PeerSync.exe process image. Refusing to replace files.'
            }
            [string]::Equals([IO.Path]::GetFullPath($_.ExecutablePath),
                $expected, [StringComparison]::OrdinalIgnoreCase)
        })
}

$deadline = [Diagnostics.Stopwatch]::StartNew()
$requested = [Collections.Generic.HashSet[int]]::new()
$forced = [Collections.Generic.HashSet[int]]::new()
while ($true) {
    $matching = @(Get-InstalledDashboard)
    if ($matching.Count -eq 0) {
        Write-Output 'DASHBOARD_STOPPED_AND_EXITED name=PeerSync.exe'
        exit 0
    }
    if ($deadline.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
        throw "The installed PeerSync dashboard did not exit within $TimeoutSeconds seconds."
    }
    foreach ($item in $matching) {
        $processId = [int]$item.ProcessId
        if ($requested.Add($processId)) {
            $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
            if ($process) {
                [void]$process.CloseMainWindow()
                Write-Output "DASHBOARD_CLOSE_REQUESTED pid=$processId"
            }
        }
        if ($deadline.Elapsed.TotalSeconds -lt 4 -or -not $forced.Add($processId)) {
            continue
        }
        # Recheck the image immediately before force-stop; a PID could have
        # exited and been reused while the graceful close was pending.
        $stillMatching = @(Get-InstalledDashboard | Where-Object ProcessId -eq $processId)
        if ($stillMatching.Count -eq 0) { continue }
        Stop-Process -Id $processId -Force -ErrorAction Stop
        Write-Output "DASHBOARD_FORCE_STOPPED pid=$processId"
    }
    Start-Sleep -Milliseconds 200
}
