function Select-QuicInvocationEvents {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object[]]$Events,
        [Parameter(Mandatory)][int]$ExpectedProcessId,
        [Parameter(Mandatory)][DateTimeOffset]$StartedUtc,
        [Parameter(Mandatory)][DateTimeOffset]$ExitedUtc,
        [Parameter(Mandatory)][ValidateSet('listen','dial')][string]$Role
    )
    if ($ExitedUtc -lt $StartedUtc) { throw 'Probe lifetime is invalid' }
    $starts = @($Events | Where-Object {
        if ($_.component -ne 'QuicBasics' -or $_.processId -ne $ExpectedProcessId -or $_.eventName -ne 'transport.start' -or $_.operation -ne $Role) { return $false }
        $stamp = [DateTimeOffset]::MinValue
        $valid = $false
        if ($_.timestampUtc -is [DateTimeOffset]) { $stamp=$_.timestampUtc; $valid=$true }
        elseif ($_.timestampUtc -is [DateTime]) { $stamp=[DateTimeOffset]$_.timestampUtc; $valid=$true }
        else { $valid=[DateTimeOffset]::TryParse([string]$_.timestampUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$stamp) }
        $valid -and $stamp -ge $StartedUtc -and $stamp -le $ExitedUtc
    })
    $run = [Guid]::Empty
    if ($starts.Count -ne 1 -or -not [Guid]::TryParse([string]$starts[0].runId,[ref]$run) -or $run -eq [Guid]::Empty) {
        throw 'Expected exactly one probe start/run in this process lifetime'
    }
    @($Events | Where-Object { $_.processId -eq $ExpectedProcessId -and $_.component -eq 'QuicBasics' -and $_.runId -eq $starts[0].runId })
}
