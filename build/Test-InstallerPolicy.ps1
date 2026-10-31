[CmdletBinding()]
param([Parameter(Mandatory)][string]$IsccPath)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path $root 'artifacts/installer-policy'
New-Item -ItemType Directory -Path $fixture -Force | Out-Null

# Compile and execute each production InitializeSetup, with only registry/file
# reads redirected to fixtures. The wrapper exits before installation or UAC.
foreach ($product in @('PeerSync', 'PeerSync.Rendezvous')) {
    $source = Get-Content -LiteralPath (Join-Path $root "installer/$product.iss") -Raw
    $match = [regex]::Match($source, '(?s)function InitializeSetup: Boolean;.*?(?=function PrepareToInstall)')
    if (-not $match.Success) { throw "Cannot extract $product InitializeSetup." }
    $policy = $match.Value.Replace('InitializeSetup', 'ProductInitializeSetup').
        Replace('RegQueryStringValue', 'FixtureInstallLocation').Replace('GetPackedVersion', 'FixtureVersion')
    $policy = [regex]::Replace($policy, '(?s)(?:Suppressible)?MsgBox\(.*?\);', "Log('Fixture message suppressed');")
    $harness = @'
#define NewVersionPacked 281474976710656
[Setup]
AppName=PeerSync installer policy test
AppVersion=1.0
DefaultDirName={tmp}\PeerSync-policy-unused
PrivilegesRequired=lowest
Uninstallable=no
CreateAppDir=no
OutputBaseFilename=policy
[Code]
function ServiceExists: Boolean;
begin
  Result := ExpandConstant('{param:STATE|absent}') <> 'absent';
end;
function FixtureInstallLocation(RootKey: Integer; const Subkey, Name: String; var Value: String): Boolean;
begin
  Value := 'X:\PeerSync-fixture';
  Result := True;
end;
function FixtureVersion(const Filename: String; var Version: Int64): Boolean;
var State: String;
begin
  State := ExpandConstant('{param:STATE|absent}');
  Result := (State <> 'missing') and (State <> 'unreadable');
  Version := StrToInt64(ExpandConstant('{param:OLDVERSION|0}'));
end;
'@
    $harness += "`n$policy`n" + @'
function InitializeSetup: Boolean;
var Value: String;
begin
  if ProductInitializeSetup then Value := 'allowed' else Value := 'rejected';
  if not SaveStringToFile(ExpandConstant('{param:RESULT}'), Value, False) then
    RaiseException('Cannot save policy result');
  Result := False;
end;
'@
    $script = Join-Path $fixture "$product.iss"
    [IO.File]::WriteAllText($script, $harness)
    & $IsccPath "/O$fixture" $script > (Join-Path $fixture "$product-compile.log")
    if ($LASTEXITCODE -ne 0) { throw "$product policy harness did not compile." }
    $cases = @(
        @{ State = 'absent'; Old = '0' },
        @{ State = 'missing'; Old = '0' },
        @{ State = 'unreadable'; Old = '0' },
        @{ State = 'older'; Old = '38654705664' },
        @{ State = 'same'; Old = '281474976710656' },
        @{ State = 'newer'; Old = '562949953421312' }
    )
    foreach ($case in $cases) {
        $resultPath = Join-Path $fixture "$product-$($case.State).txt"
        # Delete a previous result so a failed launch cannot reuse it.
        if (Test-Path -LiteralPath $resultPath) { Remove-Item -LiteralPath $resultPath }
        $process = Start-Process -FilePath (Join-Path $fixture 'policy.exe') -WindowStyle Hidden -PassThru `
            -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', "/STATE=$($case.State)",
                "/OLDVERSION=$($case.Old)", "/RESULT=`"$resultPath`"")
        if (-not $process.WaitForExit(30000)) { $process.Kill(); throw 'Policy fixture timed out.' }
        if ($process.ExitCode -ne 1 -or (Get-Content -LiteralPath $resultPath -Raw) -ne 'allowed') {
            throw "$product rejected $($case.State) installation."
        }
    }
    if ($product -eq 'PeerSync') {
        foreach ($account in @('LocalSystem', 'NT SERVICE\PeerSync', 'invalid')) {
            $resultPath = Join-Path $fixture 'account.txt'
            if (Test-Path -LiteralPath $resultPath) { Remove-Item -LiteralPath $resultPath }
            $process = Start-Process -FilePath (Join-Path $fixture 'policy.exe') -WindowStyle Hidden -PassThru `
                -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', "/SERVICEACCOUNT=`"$account`"", "/RESULT=`"$resultPath`"")
            if (-not $process.WaitForExit(30000)) { $process.Kill(); throw 'Account fixture timed out.' }
            $expected = if ($account -eq 'invalid') { 'rejected' } else { 'allowed' }
            if ($process.ExitCode -ne 1 -or (Get-Content -LiteralPath $resultPath -Raw) -ne $expected) {
                throw "Unexpected service-account policy result: $account"
            }
        }
    }
}
'Installer policy passed: fresh install, missing/unreadable image, upgrade, reinstall, downgrade, and service accounts.'
