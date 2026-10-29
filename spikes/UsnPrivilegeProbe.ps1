[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z]:$')][string]$Volume = 'C:',
    [ValidateSet('Read', 'Data', 'None')][string]$HandleAccess = 'Read',
    [Parameter(Mandatory)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$resultPath = [IO.Path]::GetFullPath($OutputPath)
$identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
[IO.File]::WriteAllText($resultPath,
    (@{ identity = $identity; stage = 'started' } | ConvertTo-Json -Compress),
    [Text.UTF8Encoding]::new($false))
trap {
    [IO.File]::WriteAllText($resultPath,
        (@{ identity = $identity; stage = 'failed'; error = $_.Exception.GetType().Name } |
            ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
    exit 1
}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class UsnProbeNative {
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateFile(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(IntPtr handle, uint control,
        byte[] input, uint inputLength, byte[] output, uint outputLength,
        out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);
}
'@

$result = [ordered]@{
    identity = $identity
    volume = $Volume.ToUpperInvariant()
    handleAccess = $HandleAccess
    openError = $null
    queryError = $null
    readError = $null
    querySucceeded = $false
    readSucceeded = $false
    bytesReturned = 0
}
$path = '\\.\' + $Volume.ToUpperInvariant()
$access = switch ($HandleAccess) {
    'Read' { [Convert]::ToUInt32('80000000', 16) }
    'Data' { [uint32]1 }
    default { [uint32]0 }
}
$handle = [UsnProbeNative]::CreateFile($path, $access, 7,
    [IntPtr]::Zero, 3, 0, [IntPtr]::Zero)
if ($handle -eq [IntPtr](-1)) {
    $result.openError = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
} else {
    try {
        $query = [byte[]]::new(80)
        $returned = [uint32]0
        $result.querySucceeded = [UsnProbeNative]::DeviceIoControl(
            $handle, [uint32]0x000900F4, [byte[]]::new(0), 0,
            $query, [uint32]$query.Length, [ref]$returned, [IntPtr]::Zero)
        if (-not $result.querySucceeded) {
            $result.queryError = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        } elseif ($returned -lt 56) {
            $result.queryError = -1
            $result.querySucceeded = $false
        } else {
            # USN_JOURNAL_DATA_V0 starts with JournalID, FirstUsn and NextUsn.
            $journalId = [BitConverter]::ToInt64($query, 0)
            $firstUsn = [BitConverter]::ToInt64($query, 8)
            $read = [byte[]]::new(40)
            [Array]::Copy([BitConverter]::GetBytes($firstUsn), 0, $read, 0, 8)
            [Array]::Copy([BitConverter]::GetBytes([uint32]::MaxValue), 0, $read, 8, 4)
            [Array]::Copy([BitConverter]::GetBytes($journalId), 0, $read, 32, 8)
            $buffer = [byte[]]::new(64KB)
            $returned = [uint32]0
            $result.readSucceeded = [UsnProbeNative]::DeviceIoControl(
                $handle, [uint32]0x000900BB, $read, [uint32]$read.Length,
                $buffer, [uint32]$buffer.Length, [ref]$returned, [IntPtr]::Zero)
            $result.bytesReturned = $returned
            if (-not $result.readSucceeded) {
                $result.readError = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
            }
        }
    }
    finally { [void][UsnProbeNative]::CloseHandle($handle) }
}
[IO.File]::WriteAllText($resultPath,
    ($result | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
