[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/QuicDiagnosticEvidence.ps1"
$began = [DateTimeOffset]'2026-10-19T15:09:22Z'
$ended = $began.AddSeconds(14)
$oldRun = 'a04420336255462cb3dfcb1c2f78e0e4'
$currentRun = '4496be4e62714fb09b33627c7c7bfb45'
function Event([string]$Run,[string]$Name,[string]$Operation,[DateTimeOffset]$Stamp,[string]$ErrorCode='') {
    [pscustomobject]@{component='QuicBasics';processId=52312;runId=$Run;eventName=$Name;operation=$Operation;timestampUtc=$Stamp.ToString('O');errorCode=$ErrorCode;bytes=42;hResult=-2146233029}
}
$events = @(
    (Event $oldRun transport.start dial $began.AddHours(-2)),
    (Event $oldRun transfer.failed send $began.AddHours(-2).AddSeconds(6) 'QuicException.StreamAborted'),
    (Event $currentRun transport.start listen $began.AddMilliseconds(30)),
    (Event $currentRun transfer.progress receive $began.AddSeconds(8)),
    (Event $currentRun transfer.failed receive $began.AddSeconds(13) 'ProbeTimeout'),
    (Event ([Guid]::NewGuid().ToString('N')) transport.start listen $ended.AddSeconds(1))
)
if (@($events | Where-Object eventName -eq transfer.failed | Where-Object errorCode -ne ProbeTimeout).Count -ne 1) { throw 'Fixture does not reproduce the unscoped failure' }
$selected = @(Select-QuicInvocationEvents -Events $events -ExpectedProcessId 52312 -StartedUtc $began -ExitedUtc $ended -Role listen)
if ($selected.Count -ne 3 -or @($selected | Where-Object runId -ne $currentRun).Count -ne 0) { throw 'PID reuse selected another invocation' }
$typed = @($events | ConvertTo-Json -Depth 4 | ConvertFrom-Json)
if (@(Select-QuicInvocationEvents -Events $typed -ExpectedProcessId 52312 -StartedUtc $began -ExitedUtc $ended -Role listen).Count -ne 3) { throw 'JSON DateTime conversion changed invocation selection' }
$failed = @($selected | Where-Object eventName -eq transfer.failed)
if ($failed.Count -ne 1 -or $failed[0].errorCode -ne 'ProbeTimeout') { throw 'Current failure evidence was lost' }
$missing = @($events | Where-Object { $_.runId -ne $currentRun -or $_.eventName -ne 'transfer.failed' })
if (@(Select-QuicInvocationEvents -Events $missing -ExpectedProcessId 52312 -StartedUtc $began -ExitedUtc $ended -Role listen | Where-Object eventName -eq transfer.failed).Count -ne 0) { throw 'Historical failure satisfied missing current evidence' }
$validHistory = @(
    (Event $oldRun transport.start listen $began.AddHours(-1)),
    (Event $oldRun transfer.failed receive $began.AddHours(-1).AddSeconds(6) 'ProbeTimeout'),
    (Event $currentRun transport.start listen $began.AddMilliseconds(30)),
    (Event $currentRun transfer.progress receive $began.AddSeconds(8))
)
if (@($validHistory | Where-Object eventName -eq transfer.failed | Where-Object errorCode -eq ProbeTimeout).Count -ne 1) { throw 'Fixture has no stale valid-looking timeout' }
if (@(Select-QuicInvocationEvents -Events $validHistory -ExpectedProcessId 52312 -StartedUtc $began -ExitedUtc $ended -Role listen | Where-Object eventName -eq transfer.failed).Count -ne 0) { throw 'Stale valid timeout satisfied missing current evidence' }
$rejected = $false
try { Select-QuicInvocationEvents -Events $events -ExpectedProcessId 52312 -StartedUtc $began -ExitedUtc $ended -Role dial | Out-Null }
catch { $rejected = $true }
if (-not $rejected) { throw 'Wrong-role evidence was accepted' }
Write-Output 'QUIC evidence excludes reused-PID history and later invocations; missing current failures remain missing'
