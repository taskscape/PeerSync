Set-StrictMode -Version Latest

function Get-PeerSyncTestTreeContentBytes {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Root,
        [ValidateRange(0, 10737418240)][long]$MaximumBytes = 10GB
    )

    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    if ($rootPath.Length -le 3 -or -not [IO.Directory]::Exists($rootPath)) {
        throw 'Test-tree root must be an existing folder below a volume root.'
    }
    if (([IO.File]::GetAttributes($rootPath) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Test-tree root cannot be a reparse point.'
    }
    # The field kit runs in Windows PowerShell 5.1. Traverse through the
    # extended-length form so a deep synchronized path is still counted.
    if ($rootPath -match '^[A-Za-z]:\\') {
        $scanRoot = '\\?\' + $rootPath
    } elseif ($rootPath.StartsWith('\\', [StringComparison]::Ordinal)) {
        $scanRoot = '\\?\UNC\' + $rootPath.Substring(2)
    } else {
        throw 'Test-tree root requires an absolute Windows path.'
    }

    $queue = [System.Collections.Generic.Queue[string]]::new()
    $queue.Enqueue($scanRoot)
    $bytes = [long]0
    while ($queue.Count -gt 0) {
        $directory = $queue.Dequeue()
        foreach ($path in [IO.Directory]::EnumerateFileSystemEntries($directory)) {
            if ($directory.Equals($scanRoot, [StringComparison]::OrdinalIgnoreCase) -and
                [IO.Path]::GetFileName($path).Equals('.psp', [StringComparison]::OrdinalIgnoreCase)) {
                continue
            }
            $attributes = [IO.File]::GetAttributes($path)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw [IO.IOException]::new('The test tree contains a reparse point.')
            }
            if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                $queue.Enqueue($path)
                continue
            }
            $size = ([IO.FileInfo]::new($path)).Length
            if ($size -lt 0 -or $size -gt $MaximumBytes - $bytes) {
                throw [IO.IOException]::new(
                    "Test tree content exceeds the $MaximumBytes-byte preflight budget.")
            }
            $bytes += $size
        }
    }
    return $bytes
}
