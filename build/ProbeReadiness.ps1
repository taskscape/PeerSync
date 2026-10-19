function Wait-ProbeReadyFile($Probe, [string]$Path, [int]$Seconds = 20) {
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    do {
        if ($Probe.Process.HasExited) { throw "Server exited before readiness: $($Probe.Label)" }
        if (Test-Path -LiteralPath $Path) {
            # File creation can become visible before its small JSON write completes.
            try { $ready = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -ErrorAction Stop }
            catch { $ready = $null }
            if ($ready -and $ready.processId -eq $Probe.Process.Id) { return }
        }
        Start-Sleep -Milliseconds 50
    } while ($deadline.Elapsed.TotalSeconds -lt $Seconds)
    throw "Server readiness timed out: $($Probe.Label)"
}
