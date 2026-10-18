[CmdletBinding()]
param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference = 'Stop'
$os = Get-CimInstance Win32_OperatingSystem
$parent = Split-Path ([IO.Path]::GetFullPath($Path)) -Parent
New-Item -ItemType Directory -Force $parent | Out-Null
@{
    recordedUtc=[DateTimeOffset]::UtcNow.ToString('O')
    os=$os.Caption
    osVersion=$os.Version
    build=$os.BuildNumber
    productType=$os.ProductType
    architecture=$os.OSArchitecture
    cpu=@(Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors)
    disks=@(Get-PhysicalDisk | Select-Object FriendlyName,MediaType,BusType,Size)
    adapters=@(Get-NetAdapter | Select-Object InterfaceDescription,Status,LinkSpeed)
    sdk=(& dotnet --version)
    runtimes=@(& dotnet --list-runtimes)
    powershell=$PSVersionTable.PSVersion.ToString()
    sourceRevision=(& git rev-parse HEAD)
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $Path
