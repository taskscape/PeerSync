[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$folder = Join-Path ([IO.Path]::GetTempPath()) ('PeerSync-collector-test-' + [Guid]::NewGuid().ToString('N'))
$culture = [Globalization.CultureInfo]::CurrentCulture
New-Item -ItemType Directory -Force "$folder/source" | Out-Null
try {
    foreach($name in @('settings.json','peersync.db','raw.log')) { Set-Content "$folder/source/$name" 'DO_NOT_COLLECT' }
    $recent = @{timestampUtc=[DateTimeOffset]::UtcNow.ToString('O');eventName='test.recent';component='test'} | ConvertTo-Json -Compress
    $expired = @{timestampUtc=[DateTimeOffset]::UtcNow.AddDays(-2).ToString('O');eventName='test.expired';component='test'} | ConvertTo-Json -Compress
    Set-Content "$folder/source/debug.jsonl" @($recent,$expired,'partial-json')
    $writer = [IO.FileStream]::new("$folder/source/debug.jsonl", [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    foreach($locale in @('pl-PL','en-US')) {
        [Globalization.CultureInfo]::CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo($locale)
        & "$PSScriptRoot/Collect-Diagnostics.ps1" -Logs "$folder/source" -Destination "$folder/$locale.zip" | Out-Null
        $zip = [IO.Compression.ZipFile]::OpenRead("$folder/$locale.zip")
        try {
            if (@($zip.Entries | Where-Object FullName -notmatch '^(debug-\d+\.jsonl|environment\.json)$').Count -ne 0) { throw 'Unexpected bundle file' }
            $entry = @($zip.Entries | Where-Object FullName -like 'debug-*')
            if ($entry.Count -ne 1) { throw 'Expected one debug file' }
            $reader = [IO.StreamReader]::new($entry[0].Open())
            try {
                $body = $reader.ReadToEnd()
                if ($body -match 'DO_NOT_COLLECT|test.expired|partial-json') { throw 'Excluded data was collected' }
                if ($body -notmatch 'test.recent') { throw 'Recent event was lost' }
            } finally { $reader.Dispose() }
        } finally { $zip.Dispose() }
        Write-Host "Collector live-reader sharing, filtering and exclusion passed ($locale)"
    }
    foreach ($case in @('empty','expired')) {
        $source = Join-Path $folder $case
        New-Item -ItemType Directory $source | Out-Null
        if ($case -eq 'expired') { Set-Content (Join-Path $source 'debug.jsonl') @($expired,'partial-json') }
        $destination = Join-Path $folder "$case.zip"
        try {
            & "$PSScriptRoot/Collect-Diagnostics.ps1" -Logs $source -Destination $destination | Out-Null
            throw 'Collector unexpectedly accepted a metadata-only bundle'
        } catch {
            if ($_.Exception.Message -notmatch '^No (debug\*\.jsonl files|recent valid debug events) found') { throw }
        }
        if (Test-Path -LiteralPath $destination) { throw 'An empty diagnostic bundle was created' }
        Write-Host "Collector rejected $case debug logs with an actionable error"
    }
}
finally {
    if ($writer) { $writer.Dispose() }
    [Globalization.CultureInfo]::CurrentCulture = $culture
    $resolved = [IO.Path]::GetFullPath($folder)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase) -or (Split-Path $resolved -Leaf) -notmatch '^PeerSync-collector-test-[0-9a-f]{32}$') { throw 'Unexpected cleanup path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
