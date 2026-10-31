// Shared by the client and rendezvous installers. The bundled redistributable
// is still installed on clean or older systems; an already current runtime
// must not be reinstalled on every PeerSync upgrade.
function VcRuntimeAtLeastPackagedAtRoot(const Root: HKEY; const RegPath: String;
  const RequiredMajor, RequiredMinor, RequiredBuild, RequiredRevision: Word): Boolean;
var
  Installed, Major, Minor, Build, Revision: Cardinal;
begin
  Result := False;
  if not RegQueryDWordValue(Root, RegPath, 'Installed', Installed) or
     not RegQueryDWordValue(Root, RegPath, 'Major', Major) or
     not RegQueryDWordValue(Root, RegPath, 'Minor', Minor) or
     not RegQueryDWordValue(Root, RegPath, 'Bld', Build) or
     not RegQueryDWordValue(Root, RegPath, 'Rbld', Revision) or
     (Installed <> 1) then
    Exit;

  if Major <> RequiredMajor then
    Result := Major > RequiredMajor
  else if Minor <> RequiredMinor then
    Result := Minor > RequiredMinor
  else if Build <> RequiredBuild then
    Result := Build > RequiredBuild
  else
    Result := Revision >= RequiredRevision;
end;

function VcRuntimeAtLeastPackaged: Boolean;
var
  Major, Minor, Build, Revision: Word;
  RegPath: String;
  DllVersion: Int64;
begin
  Result := False;
  UnpackVersionComponents({#VcRedistVersionPacked}, Major, Minor, Build, Revision);
  RegPath := 'Software\Microsoft\VisualStudio\14.0\VC\Runtimes\{#VcRegistryArch}';
  // Microsoft documents the 32-bit registry view for this package. Check the
  // native view as well because Windows installations can expose both.
  if not GetPackedVersion(ExpandConstant('{sys}\VCRUNTIME140.dll'), DllVersion) or
     (ComparePackedVersion(DllVersion, {#VcRedistVersionPacked}) < 0) then
  begin
    Log('Visual C++ runtime DLL missing or older; running bundled redistributable.');
    Exit;
  end;
  Result := VcRuntimeAtLeastPackagedAtRoot(HKLM32, RegPath,
      Major, Minor, Build, Revision) or
    VcRuntimeAtLeastPackagedAtRoot(HKLM64, RegPath,
      Major, Minor, Build, Revision);
  if Result then
    Log('Visual C++ runtime already meets bundled version ' +
      VersionToStr({#VcRedistVersionPacked}) + '; skipping redistributable.')
  else
    Log('Visual C++ runtime missing or older than bundled version ' +
      VersionToStr({#VcRedistVersionPacked}) + '; running redistributable.');
end;
