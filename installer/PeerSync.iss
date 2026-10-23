#ifndef Arch
  #define Arch "x64"
#endif
#if Arch != "x64" && Arch != "arm64"
  #error Arch must be x64 or arm64
#endif
#define StageRoot AddBackslash(SourcePath) + "..\publish\" + Arch
#define ServiceExe StageRoot + "\service\PeerSync.Service.exe"
#define NewVersionPacked 0
#define AppVer GetPackedVersion(ServiceExe, NewVersionPacked)
#if AppVer == ""
  #error Publish PeerSync.Service.exe before compiling the installer
#endif

[Setup]
AppId={{F6E23D72-1DFD-49D6-933B-A9B1FF63E6B5}
AppName=PeerSync
AppVersion={#AppVer}
AppPublisher=taskscape
DefaultDirName={autopf}\PeerSync
DefaultGroupName=PeerSync
PrivilegesRequired=admin
MinVersion=10.0.20348
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible and not arm64
ArchitecturesInstallIn64BitMode=x64compatible and not arm64
#endif
CloseApplications=yes
RestartApplications=no
UsePreviousTasks=yes
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=PeerSync-Setup-{#AppVer}-{#Arch}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\app\PeerSync.exe
#ifndef UnsignedDevelopment
SignTool=peersync
SignedUninstaller=yes
#else
SignedUninstaller=no
#endif

[Tasks]
Name: autostart; Description: "Start PeerSync when I sign in"

[Dirs]
Name: "{commonappdata}\PeerSync"; Flags: uninsneveruninstall

[Files]
; NSec's libsodium DLL needs the Microsoft Visual C++ v14 runtime on clean PCs.
; Extract before installation so a reboot requirement leaves the old service intact.
Source: "{#StageRoot}\redist\vc_redist.exe"; Flags: dontcopy noencryption
Source: "Update-Helper.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "Assert-UpdateSetupSignature.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "Set-UpdatePolicy.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "Set-WerPolicy.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "Audit-CrashPolicy.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "Collect-DeploymentEvidence.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion
#ifndef UnsignedDevelopment
Source: "{#StageRoot}\service\*.exe"; DestDir: "{app}\service"; Flags: recursesubdirs ignoreversion signonce
Source: "{#StageRoot}\service\*.dll"; DestDir: "{app}\service"; Flags: recursesubdirs ignoreversion signonce
Source: "{#StageRoot}\app\*.exe"; DestDir: "{app}\app"; Flags: recursesubdirs ignoreversion signonce
Source: "{#StageRoot}\app\*.dll"; DestDir: "{app}\app"; Flags: recursesubdirs ignoreversion signonce
Source: "{#StageRoot}\cli\*.exe"; DestDir: "{app}\cli"; Flags: recursesubdirs ignoreversion signonce
Source: "{#StageRoot}\cli\*.dll"; DestDir: "{app}\cli"; Flags: recursesubdirs ignoreversion signonce
#else
#endif
Source: "{#StageRoot}\service\*"; DestDir: "{app}\service"; Excludes: "*.exe,*.dll,*.pdb"; Flags: recursesubdirs ignoreversion
Source: "{#StageRoot}\app\*"; DestDir: "{app}\app"; Excludes: "*.exe,*.dll,*.pdb"; Flags: recursesubdirs ignoreversion
Source: "{#StageRoot}\cli\*"; DestDir: "{app}\cli"; Excludes: "*.exe,*.dll,*.pdb"; Flags: recursesubdirs ignoreversion
#ifdef UnsignedDevelopment
Source: "{#StageRoot}\service\*.exe"; DestDir: "{app}\service"; Flags: recursesubdirs ignoreversion
Source: "{#StageRoot}\service\*.dll"; DestDir: "{app}\service"; Flags: recursesubdirs ignoreversion
Source: "{#StageRoot}\app\*.exe"; DestDir: "{app}\app"; Flags: recursesubdirs ignoreversion
Source: "{#StageRoot}\app\*.dll"; DestDir: "{app}\app"; Flags: recursesubdirs ignoreversion
Source: "{#StageRoot}\cli\*.exe"; DestDir: "{app}\cli"; Flags: recursesubdirs ignoreversion
Source: "{#StageRoot}\cli\*.dll"; DestDir: "{app}\cli"; Flags: recursesubdirs ignoreversion
#endif

[Icons]
Name: "{group}\PeerSync"; Filename: "{app}\app\PeerSync.exe"
Name: "{group}\PeerSync command line"; Filename: "{cmd}"; Parameters: "/k ""{app}\cli\psp.exe"" --help"

[Registry]
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PeerSync"; ValueData: """{app}\app\PeerSync.exe"" --tray"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\app\PeerSync.exe"; Description: "Open PeerSync"; Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\tools\Update-Helper.ps1"" -Unregister"; Flags: runhidden; RunOnceId: "DeleteUpdateTask"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""PeerSync"""; Flags: runhidden; RunOnceId: "DeleteFirewallRule"

[Code]
var PostInstallSucceeded: Boolean;

function GetCustomSetupExitCode: Integer;
begin
  { Inno can return zero after a suppressed ssPostInstall script exception. }
  if PostInstallSucceeded then Result := 0 else Result := 9;
end;

function ServiceExists: Boolean;
begin
  Result := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\PeerSync');
end;

function RunCommand(const FileName, Parameters: String): Integer;
var
  Code: Integer;
begin
  Log('PeerSync installer: ' + ExtractFileName(FileName) + ' ' + Parameters);
  if not ExecAndLogOutput(FileName, Parameters, '', SW_HIDE, ewWaitUntilTerminated, Code, nil) then
    RaiseException('Could not start ' + ExtractFileName(FileName) + ': ' + SysErrorMessage(Code));
  Result := Code;
  Log('PeerSync installer command exit: ' + IntToStr(Code));
end;

procedure RequireSuccess(const FileName, Parameters: String);
var
  Code: Integer;
begin
  Code := RunCommand(FileName, Parameters);
  if Code <> 0 then
    RaiseException(ExtractFileName(FileName) + ' failed with exit code ' + IntToStr(Code) +
      '. See the setup log for command output.');
end;

function GroupExists: Boolean;
begin
  Result := RunCommand(ExpandConstant('{sys}\net.exe'), 'localgroup "PeerSync Users"') = 0;
end;

function IsServiceStopped: Boolean;
var
  Code: Integer;
begin
  Code := RunCommand(ExpandConstant('{cmd}'), '/c sc query PeerSync | find "STOPPED"');
  Result := Code = 0;
end;

function InitializeSetup: Boolean;
var
  InstalledVersion, NewVersion: Int64;
  Account, PreviousDir: String;
begin
  Result := False;
  Account := ExpandConstant('{param:SERVICEACCOUNT|NT SERVICE\PeerSync}');
  if (CompareText(Account, 'LocalSystem') <> 0) and
     (CompareText(Account, 'NT SERVICE\PeerSync') <> 0) then
  begin
    MsgBox('SERVICEACCOUNT must be LocalSystem or NT SERVICE\PeerSync.', mbError, MB_OK);
    Exit;
  end;
  if ServiceExists then
  begin
    if not RegQueryStringValue(HKLM64,
      'Software\Microsoft\Windows\CurrentVersion\Uninstall\{F6E23D72-1DFD-49D6-933B-A9B1FF63E6B5}_is1',
      'InstallLocation', PreviousDir) then
      PreviousDir := ExpandConstant('{autopf}\PeerSync');
    if not GetPackedVersion(AddBackslash(PreviousDir) + 'service\PeerSync.Service.exe', InstalledVersion) then
    begin
      MsgBox('The installed PeerSync service has no readable version. Repair or remove it before installing.', mbError, MB_OK);
      Exit;
    end;
    NewVersion := {#NewVersionPacked};
    if ComparePackedVersion(NewVersion, InstalledVersion) < 0 then
    begin
      MsgBox('PeerSync cannot be downgraded. Install the same or a newer version.', mbError, MB_OK);
      Exit;
    end;
  end;
  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Tries, RedistCode: Integer;
begin
  Result := '';
  ExtractTemporaryFile('vc_redist.exe');
  RedistCode := RunCommand(ExpandConstant('{tmp}\vc_redist.exe'), '/install /quiet /norestart');
  if RedistCode = 3010 then
  begin
    NeedsRestart := True;
    Result := 'The Microsoft Visual C++ runtime requires a restart. Restart Windows and run PeerSync Setup again.';
    Exit;
  end;
  if (RedistCode <> 0) and (RedistCode <> 1638) then
  begin
    Result := 'The Microsoft Visual C++ runtime could not be installed (exit code ' +
      IntToStr(RedistCode) + '). See the setup log.';
    Exit;
  end;
  if not ServiceExists then Exit;
  if IsServiceStopped then Exit;
  RunCommand(ExpandConstant('{sys}\sc.exe'), 'stop PeerSync');
  for Tries := 1 to 30 do
  begin
    if IsServiceStopped then Exit;
    Sleep(1000);
  end;
  Result := 'The PeerSync service did not stop within 30 seconds. Stop it in Services and run Setup again.';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Account: String;
begin
  if CurStep <> ssPostInstall then Exit;
  if not GroupExists then
    RequireSuccess(ExpandConstant('{sys}\net.exe'), 'localgroup "PeerSync Users" /add');
  { A pre-existing group may already contain this user. --init also grants the
    setup account by SID, so group membership can take effect at next sign-in. }
  RunCommand(ExpandConstant('{sys}\net.exe'),
    'localgroup "PeerSync Users" "' + ExpandConstant('{username}') + '" /add');
  if not ServiceExists then
  begin
    Account := ExpandConstant('{param:SERVICEACCOUNT|NT SERVICE\PeerSync}');
    RequireSuccess(ExpandConstant('{sys}\sc.exe'),
      'create PeerSync binPath= "' + ExpandConstant('{app}\service\PeerSync.Service.exe') +
      '" start= auto obj= "' + Account + '"');
  end;
  RequireSuccess(ExpandConstant('{sys}\sc.exe'),
    'failure PeerSync reset= 86400 actions= restart/60000/restart/60000/restart/300000');
  RequireSuccess(ExpandConstant('{app}\service\PeerSync.Service.exe'), '--init');
  RequireSuccess(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
    ExpandConstant('{app}\tools\Set-WerPolicy.ps1') +
    '" -ExecutableNames PeerSync.Service.exe,PeerSync.exe,psp.exe');
  RequireSuccess(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
    ExpandConstant('{app}\tools\Update-Helper.ps1') + '" -Register');
  RunCommand(ExpandConstant('{sys}\netsh.exe'),
    'advfirewall firewall delete rule name="PeerSync"');
  RequireSuccess(ExpandConstant('{sys}\netsh.exe'),
    'advfirewall firewall add rule name="PeerSync" dir=in action=allow program="' +
    ExpandConstant('{app}\service\PeerSync.Service.exe') + '" enable=yes');
  RequireSuccess(ExpandConstant('{sys}\sc.exe'), 'start PeerSync');
  PostInstallSucceeded := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Tries: Integer;
begin
  if CurUninstallStep <> usUninstall then Exit;
  if ServiceExists and not IsServiceStopped then
  begin
    RunCommand(ExpandConstant('{sys}\sc.exe'), 'stop PeerSync');
    for Tries := 1 to 30 do
    begin
      if IsServiceStopped then Break;
      Sleep(1000);
    end;
    if not IsServiceStopped then
      RaiseException('The PeerSync service did not stop. Stop it in Services and run Uninstall again.');
  end;
  if not UninstallSilent then
    if MsgBox('Also remove PeerSync settings, databases, logs and the device certificate?' + #13#10 +
      'Your synced folders and their files are not touched.',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      RequireSuccess(ExpandConstant('{app}\service\PeerSync.Service.exe'), '--remove-data');
  RequireSuccess(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
    ExpandConstant('{app}\tools\Set-WerPolicy.ps1') +
    '" -ExecutableNames PeerSync.Service.exe,PeerSync.exe,psp.exe -Remove');
  { Perform these once on every uninstall, even after an install/uninstall/
    reinstall cycle. Conditional entries in UninstallRun were skipped on the
    second cycle, leaving a service pointing at deleted files and an orphaned
    local group. Keep failures visible before Setup removes the payload. }
  if ServiceExists then
    RequireSuccess(ExpandConstant('{sys}\sc.exe'), 'delete PeerSync');
  if GroupExists then
    RequireSuccess(ExpandConstant('{sys}\net.exe'), 'localgroup "PeerSync Users" /delete');
end;
