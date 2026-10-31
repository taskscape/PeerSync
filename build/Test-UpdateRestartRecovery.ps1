[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$helper = Join-Path (Split-Path $PSScriptRoot -Parent) 'installer\Update-Helper.ps1'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($helper, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Update helper did not parse.' }
foreach ($name in @('Write-UpdateAttempt', 'Find-RestartedAttempt',
        'Remove-ReadySlot', 'Remove-TrustedAttempt')) {
    $function = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq $name }, $true))
    if ($function.Count -ne 1) { throw "Update restart function $name is missing or ambiguous." }
    . ([scriptblock]::Create($function[0].Extent.Text))
}

$root = Join-Path $env:TEMP ('PeerSync-update-attempt-' + [guid]::NewGuid().ToString('N'))
$ready = [guid]::NewGuid().ToString('N') + '.ready'
$attempt = Join-Path $root ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $attempt -Force | Out-Null
# Compare the filesystem's canonical spelling, as returned by the helper's
# directory enumeration. Runner TEMP can contain a short-name or dot alias.
$attempt = (Get-ChildItem -LiteralPath $root -Directory).FullName
try {
    $bootTicks = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().Ticks
    Write-UpdateAttempt $attempt $ready ($bootTicks + 1) 'restartRequired'
    try { Find-RestartedAttempt $ready $root | Out-Null; throw 'Pre-reboot attempt was accepted.' }
    catch { if ($_.Exception.Message -ne 'Restart is still required before update cleanup.') { throw } }

    Write-UpdateAttempt $attempt $ready ($bootTicks - 1) 'started'
    if ($null -ne (Find-RestartedAttempt $ready $root)) { throw 'Unfinished attempt was accepted.' }

    Write-UpdateAttempt $attempt $ready ($bootTicks - 1) 'restartRequired'
    $found = Find-RestartedAttempt $ready $root
    if ($found.FullName -ne $attempt) { throw 'Restarted attempt returned a different directory.' }

    $other = Join-Path $root ([guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $other | Out-Null
    '{invalid-json' | Set-Content -LiteralPath (Join-Path $other 'attempt.json')
    if ((Find-RestartedAttempt $ready $root).FullName -ne $attempt) {
        throw 'An unrelated damaged attempt blocked the verified attempt.'
    }

    $pending = Join-Path $root 'pending'
    New-Item -ItemType Directory -Path $pending | Out-Null
    $blocked = Join-Path $pending ([guid]::NewGuid().ToString('N') + '.ready')
    $foreign = Join-Path $blocked 'unexpected'
    New-Item -ItemType Directory -Path $foreign -Force | Out-Null
    'preserve' | Set-Content -LiteralPath (Join-Path $foreign 'marker.txt')
    try { Remove-ReadySlot (Get-Item -LiteralPath $blocked) $pending; throw 'Nested child was removed.' }
    catch { if ($_.Exception.Message -ne 'Pending update slot contains an unexpected child.') { throw } }
    if ((Get-Content -LiteralPath (Join-Path $foreign 'marker.txt')) -ne 'preserve') {
        throw 'Unexpected child was modified.'
    }

    $clean = Join-Path $pending ([guid]::NewGuid().ToString('N') + '.ready')
    New-Item -ItemType Directory -Path $clean | Out-Null
    foreach ($name in @('manifest.json', 'setup.exe', 'receipt.json')) {
        '' | Set-Content -LiteralPath (Join-Path $clean $name)
    }
    Remove-ReadySlot (Get-Item -LiteralPath $clean) $pending
    if (Test-Path -LiteralPath $clean) { throw 'Verified ready slot was not removed.' }
    Remove-TrustedAttempt (Get-Item -LiteralPath $attempt) $root
    if (Test-Path -LiteralPath $attempt) { throw 'Verified trusted attempt was not removed.' }
    'Update restart recovery control passed.'
}
finally {
    $full = [IO.Path]::GetFullPath($root)
    if ([IO.Path]::GetDirectoryName($full) -ne [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') -or
        [IO.Path]::GetFileName($full) -cnotmatch '^PeerSync-update-attempt-[0-9a-f]{32}$') {
        throw 'Unexpected update restart test fixture path.'
    }
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
}
