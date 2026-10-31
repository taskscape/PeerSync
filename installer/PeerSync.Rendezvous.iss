#define StageRoot AddBackslash(SourcePath) + "..\publish\rendezvous\x64"
#define ServiceExe StageRoot + "\PeerSync.Rendezvous.exe"
#define AppVer GetFileVersion(ServiceExe)
#if AppVer == ""
  #error Publish PeerSync.Rendezvous.exe before compiling the installer
#endif
#define VcRedistExe StageRoot + "\redist\vc_redist.exe"
#define VcRedistVersionPacked 0
#define VcRedistVersion GetPackedVersion(VcRedistExe, VcRedistVersionPacked)
#if VcRedistVersion == ""
  #error Bundle a versioned vc_redist.exe before compiling the installer
#endif
#define VcRegistryArch "x64"

[Setup]
AppId={{347EEC9C-2B64-4B93-889A-6F57142E1F76}
AppName=PeerSync Rendezvous
AppVersion={#AppVer}
AppPublisher=taskscape
DefaultDirName={autopf}\PeerSync Rendezvous
DefaultGroupName=PeerSync Rendezvous
PrivilegesRequired=admin
MinVersion=10.0.20348
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; An earlier service build may report STOPPED while its process remains in
; Windows Error Reporting. Restart Manager must release those old image files.
CloseApplications=force
RestartApplications=no
OutputDir=..\artifacts\installer
OutputBaseFilename=PeerSync-Rendezvous-Setup-{#AppVer}-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\PeerSync.Rendezvous.exe
#ifndef UnsignedDevelopment
SignTool=peersync
SignedUninstaller=yes
#else
SignedUninstaller=no
#endif

[Dirs]
Name: "{commonappdata}\PeerSync.Rendezvous"; Flags: uninsneveruninstall
Name: "{commonappdata}\PeerSync.Rendezvous\logs"; Flags: uninsneveruninstall

[Files]
Source: "{#StageRoot}\redist\vc_redist.exe"; Flags: dontcopy noencryption
Source: "Stop-ServiceAndWait.ps1"; Flags: dontcopy noencryption
Source: "Stop-ServiceAndWait.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "Grant-RendezvousCertificate.ps1"; Flags: dontcopy noencryption
Source: "Grant-RendezvousCertificate.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "Import-RendezvousWebHostingCertificate.ps1"; Flags: dontcopy noencryption
Source: "Import-RendezvousWebHostingCertificate.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "Set-WerPolicy.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "Audit-CrashPolicy.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "Test-RendezvousDeployment.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "Collect-DeploymentEvidence.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "Grant-RendezvousCertificate.ps1"; DestDir: "{commonappdata}\PeerSync.Rendezvous"; Flags: ignoreversion uninsneveruninstall
Source: "Import-RendezvousWebHostingCertificate.ps1"; DestDir: "{commonappdata}\PeerSync.Rendezvous"; Flags: ignoreversion uninsneveruninstall
Source: "After-RendezvousRenewal.ps1"; DestDir: "{commonappdata}\PeerSync.Rendezvous"; Flags: ignoreversion uninsneveruninstall
#ifndef UnsignedDevelopment
Source: "{#StageRoot}\*.exe"; DestDir: "{app}"; Flags: ignoreversion signonce
Source: "{#StageRoot}\*.dll"; DestDir: "{app}"; Flags: ignoreversion signonce
#endif
Source: "{#StageRoot}\appsettings.json"; DestDir: "{app}"; Flags: onlyifdoesntexist
Source: "{#StageRoot}\*"; DestDir: "{app}"; Excludes: "*.exe,*.dll,*.pdb,appsettings.json,redist\*"; Flags: recursesubdirs ignoreversion
#ifdef UnsignedDevelopment
Source: "{#StageRoot}\*.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StageRoot}\*.dll"; DestDir: "{app}"; Flags: ignoreversion
#endif

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""PeerSync Rendezvous TCP 99"""; Flags: runhidden; RunOnceId: "DeleteTcpRule"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""PeerSync Rendezvous UDP 99"""; Flags: runhidden; RunOnceId: "DeleteUdpRule"

[Registry]
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Services\EventLog\Application\PeerSyncRendezvous"; ValueType: expandsz; ValueName: "EventMessageFile"; ValueData: "{win}\Microsoft.NET\Framework64\v4.0.30319\EventLogMessages.dll"; Flags: uninsdeletekey
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Services\EventLog\Application\PeerSyncRendezvous"; ValueType: dword; ValueName: "TypesSupported"; ValueData: "7"; Flags: uninsdeletekey

[Code]
#include "VcRuntime.iss"
function ServiceExists: Boolean;
begin
  Result := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\PeerSyncRendezvous');
end;

function RunCommand(const FileName, Parameters: String): Integer;
var Code: Integer;
begin
  Log('Rendezvous installer: ' + ExtractFileName(FileName) + ' ' + Parameters);
  if not ExecAndLogOutput(FileName, Parameters, '', SW_HIDE, ewWaitUntilTerminated, Code, nil) then
    RaiseException('Could not start ' + ExtractFileName(FileName) + ': ' + SysErrorMessage(Code));
  Result := Code;
  Log('Rendezvous installer command exit: ' + IntToStr(Code));
end;

procedure RequireSuccess(const FileName, Parameters: String);
var Code: Integer;
begin
  Code := RunCommand(FileName, Parameters);
  if Code <> 0 then RaiseException(ExtractFileName(FileName) + ' failed with exit code ' + IntToStr(Code) + '. See setup log.');
end;

function InitializeSetup: Boolean;
begin
  { Manual setup always permits the selected package version. Certificate and
    service/process checks still run before replacing the installed files. }
  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var Code, I: Integer;
    Output: TExecOutput;
begin
  Result := '';
  if not VcRuntimeAtLeastPackaged then
  begin
    ExtractTemporaryFile('vc_redist.exe');
    Code := RunCommand(ExpandConstant('{tmp}\vc_redist.exe'), '/install /quiet /norestart');
    if Code = 3010 then
    begin
      NeedsRestart := True;
      Result := 'The Visual C++ runtime requires a restart. Restart Windows and run setup again.';
      Exit;
    end;
    if (Code <> 0) and (Code <> 1638) then
    begin
      Result := 'The Visual C++ runtime failed (exit code ' + IntToStr(Code) + ').';
      Exit;
    end;
  end;
  ExtractTemporaryFile('Grant-RendezvousCertificate.ps1');
  ExtractTemporaryFile('Import-RendezvousWebHostingCertificate.ps1');
  if RunCommand(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
    ExpandConstant('{tmp}\Import-RendezvousWebHostingCertificate.ps1') + '"') <> 0 then
  begin
    Result := 'The win-acme WebHosting certificate import failed. See setup log.';
    Exit;
  end;
  if not ExecAndCaptureOutput(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
    ExpandConstant('{tmp}\Grant-RendezvousCertificate.ps1') + '" -CheckOnly',
    '', SW_HIDE, ewWaitUntilTerminated, Code, Output) then
  begin
    Result := 'Could not run the Rendezvous certificate check. See setup log.';
    Exit;
  end;
  if Code <> 0 then
  begin
    for I := 0 to GetArrayLength(Output.StdErr) - 1 do
      Log('Rendezvous certificate check: ' + Output.StdErr[I]);
    Result := 'The sync.taskscape.com certificate check failed.';
    if GetArrayLength(Output.StdErr) > 0 then
      Result := Result + #13#10 + Output.StdErr[0]
    else
      Result := Result + ' See setup log.';
    Exit;
  end;
  if not ServiceExists then Exit;
  ExtractTemporaryFile('Stop-ServiceAndWait.ps1');
  if RunCommand(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
    ExpandConstant('{tmp}\Stop-ServiceAndWait.ps1') + '" -ServiceName PeerSyncRendezvous') <> 0 then
    Result := 'The PeerSyncRendezvous service or its old process did not exit. See setup log.';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep <> ssPostInstall then Exit;
  if not ServiceExists then
    RequireSuccess(ExpandConstant('{sys}\sc.exe'),
      'create PeerSyncRendezvous binPath= "\"' + ExpandConstant('{app}\PeerSync.Rendezvous.exe') +
      '\"" start= auto obj= "NT SERVICE\PeerSyncRendezvous"')
  else
    RequireSuccess(ExpandConstant('{sys}\sc.exe'),
      'config PeerSyncRendezvous binPath= "\"' + ExpandConstant('{app}\PeerSync.Rendezvous.exe') +
      '\"" start= auto obj= "NT SERVICE\PeerSyncRendezvous"');
  RequireSuccess(ExpandConstant('{sys}\sc.exe'),
    'failure PeerSyncRendezvous reset= 86400 actions= restart/60000/restart/60000/restart/300000');
  { Remove the broad inheritable grant from earlier development builds before
    granting write access only to the journal directory. Renewal scripts in
    the parent are executed as administrator and must stay service-read-only. }
  RequireSuccess(ExpandConstant('{sys}\icacls.exe'),
    '"' + ExpandConstant('{commonappdata}\PeerSync.Rendezvous') + '" /remove:g "NT SERVICE\PeerSyncRendezvous"');
  RequireSuccess(ExpandConstant('{sys}\icacls.exe'),
    '"' + ExpandConstant('{commonappdata}\PeerSync.Rendezvous\logs') + '" /grant "NT SERVICE\PeerSyncRendezvous:(OI)(CI)M"');
  RequireSuccess(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\Grant-RendezvousCertificate.ps1') + '"');
  RequireSuccess(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
    ExpandConstant('{app}\Set-WerPolicy.ps1') + '" -ExecutableNames PeerSync.Rendezvous.exe');
  RunCommand(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall delete rule name="PeerSync Rendezvous TCP 99"');
  RunCommand(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall delete rule name="PeerSync Rendezvous UDP 99"');
  RequireSuccess(ExpandConstant('{sys}\netsh.exe'),
    'advfirewall firewall add rule name="PeerSync Rendezvous TCP 99" dir=in action=allow protocol=TCP localport=99');
  RequireSuccess(ExpandConstant('{sys}\netsh.exe'),
    'advfirewall firewall add rule name="PeerSync Rendezvous UDP 99" dir=in action=allow protocol=UDP localport=99');
  RequireSuccess(ExpandConstant('{sys}\sc.exe'), 'start PeerSyncRendezvous');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep <> usUninstall then Exit;
  if ServiceExists then
    RequireSuccess(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
      ExpandConstant('{app}\Stop-ServiceAndWait.ps1') + '" -ServiceName PeerSyncRendezvous');
  RequireSuccess(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
    ExpandConstant('{app}\Set-WerPolicy.ps1') + '" -ExecutableNames PeerSync.Rendezvous.exe -Remove');
  { Delete before files are removed on every uninstall cycle. A conditional
    service entry in UninstallRun can be skipped after a reinstall. }
  if ServiceExists then
    RequireSuccess(ExpandConstant('{sys}\sc.exe'), 'delete PeerSyncRendezvous');
end;
