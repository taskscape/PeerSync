[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Root,
    [ValidateRange(1024, 65535)][int]$Port = 9443
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($Root)
$manifest = Join-Path $root 'manifest.json'
$setup = Join-Path $root 'setup.exe'
if (-not (Test-Path -LiteralPath $manifest -PathType Leaf) -or
    -not (Test-Path -LiteralPath $setup -PathType Leaf)) {
    throw 'The local test feed needs manifest.json and setup.exe.'
}
$journal = Join-Path $root 'feed.jsonl'
$listener = [Net.HttpListener]::new()
$listener.Prefixes.Add("https://localhost:$Port/")
$listener.Start()
try {
    while ($listener.IsListening) {
        $context = $listener.GetContext()
        $name = $context.Request.Url.AbsolutePath
        $path = switch ($name) {
            '/manifest.json' { $manifest }
            '/setup.exe' { $setup }
            default { $null }
        }
        if ($context.Request.HttpMethod -ne 'GET' -or $null -eq $path) {
            $context.Response.StatusCode = 404
            $context.Response.Close()
            continue
        }
        $file = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read,
            [IO.FileShare]::Read)
        try {
            $context.Response.StatusCode = 200
            $context.Response.ContentType = if ($name -eq '/manifest.json') {
                'application/json'
            } else { 'application/octet-stream' }
            $context.Response.ContentLength64 = $file.Length
            $file.CopyTo($context.Response.OutputStream)
            $context.Response.OutputStream.Flush()
            $context.Response.Close()
            @{ utc = [DateTime]::UtcNow.ToString('O'); path = $name; bytes = $file.Length } |
                ConvertTo-Json -Compress | Add-Content -LiteralPath $journal
        }
        finally { $file.Dispose() }
    }
}
finally { $listener.Stop(); $listener.Close() }
