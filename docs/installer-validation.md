# PeerSync installer and two-PC validation

P3 installers are development builds until the clean-VM and two-PC gate in
`plan.md` passes. Build on Windows with .NET 10, the Windows SDK Signing Tools,
and Inno Setup 7.1. The build script publishes self-contained ReadyToRun
service, app, and CLI payloads into `publish/<arch>/`, then invokes ISCC.
It downloads the Microsoft-signed Visual C++ v14 Redistributable for the
selected architecture from Microsoft's documented permalink, verifies its
Authenticode publisher, and bundles it. Setup installs that prerequisite
before replacing service files; a required reboot stops Setup before the
upgrade and must be followed by a rerun. The manifest records its SHA-256.
The [Server 2025 clean-VM smoke](verification/P3-server2025-installer-smoke-2026-10-22.md)
records the missing-runtime reproduction and the successful bundled fix.

```powershell
$thumbprint = 'YOUR_LOCALMACHINE_CODE_SIGNING_CERTIFICATE_THUMBPRINT'
./build/Build-Installer.ps1 -Arch x64 -Version 1.0.0 -CertificateThumbprint $thumbprint -DevelopmentSigning
./build/Build-Installer.ps1 -Arch arm64 -Version 1.0.0 -CertificateThumbprint $thumbprint -DevelopmentSigning
```

The default build performs a locked solution restore. If a local development
machine already has restored assets but another process temporarily locks a
NuGet analyzer, `-UseExistingRestoredAssets` publishes with `--no-restore`.
It is accepted only with `-DevelopmentSigning` or `-UnsignedDevelopment`, and
the manifest records `restoreMode: existing-assets`. This local fallback
does not establish locked-restore or release-build parity; CI and release
builds must use the default `restoreMode: locked` path.

`-UnsignedDevelopment` permits local compiler validation without a signing
identity. It records `localDevelopmentOnly: true` in the adjacent manifest;
do not distribute that setup. A development certificate signs test builds but
does not give public publisher trust. The setup, its embedded uninstaller, and
the EXE/DLL payloads use the same configured signing identity. The certificate
private key stays in `Cert:\LocalMachine\My`; the script takes only its
thumbprint and does not export it.

For a test PC that has not trusted the development signer, copy the setup and
its adjacent `.exe.cer` file together. After checking that the certificate
SHA-256 matches `developmentCertificateSha256` in the manifest, import this
**public certificate only** from an elevated PowerShell on that test PC:

```powershell
$cer = '.\PeerSync-Setup-1.0.0.0-x64.exe.cer'
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null
Get-AuthenticodeSignature '.\PeerSync-Setup-1.0.0.0-x64.exe' | Select-Object Status, SignerCertificate
```

Trust this self-signed certificate only on isolated test machines. Remove it
from those stores after validation. A production deployment needs a publicly
trusted code-signing identity.

Use an elevated PowerShell on each test PC. For an unattended x64 install:

```powershell
New-Item -ItemType Directory -Path C:\Temp -Force | Out-Null
& .\PeerSync-Setup-1.0.0.0-x64.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS="autostart" /LOG="C:\Temp\PeerSync-setup.log"
sc.exe query PeerSync
& "$env:ProgramFiles\PeerSync\cli\psp.exe" status --json
& "$env:ProgramFiles\PeerSync\app\PeerSync.exe"
```

For scripted installs, use `Start-Process -Wait -PassThru -WindowStyle Hidden`
to capture Setup's exit code. A bare PowerShell invocation of a GUI setup can
return before installation finishes in remote sessions.

Before an attended upgrade, choose **Exit app** from the PeerSync tray menu;
closing the dashboard window alone leaves its tray process running. The
service continues independently until Setup stops it. Current Setup asks
Windows Restart Manager to force-close an app still using files it must
replace, including for silent upgrades, and does not automatically reopen
the dashboard afterward. Check the exit code and service state before
reopening the app. An earlier physical silent upgrade failed to replace the
open dashboard executable; the [installer repair record](verification/P3-open-dashboard-upgrade-repair-2026-10-24.md)
retains that failure and the pending field verification of the new setting.

The default service account is `NT SERVICE\PeerSync`. The installer switch
`/SERVICEACCOUNT=LocalSystem` is a fallback when a folder's ACL cannot be
granted to the virtual account. The app offers an inheritable Modify grant
when adding or joining a folder; a protected child ACL remains visible as a
warning. The installer creates a distinct, non-exportable device certificate
on each PC through `PeerSync.Service.exe --init`. No development certificate
creation is needed on the second PC for device identity.

For the two-PC check, open the app on PC A, add a test folder, then choose
Share. Copy the read/write key into Join on PC B and choose a different local
test folder. Place a file in either folder and confirm the bytes and contents
match on the other PC. Record the service status, app screenshot, test file
hashes, elapsed time, and both machines' OS versions. If the peers are on
different networks, configure reachable predefined hosts in the folder
Settings tab; internet traversal is a P4 gate and should be reported
separately from the P3 installer check.

After an upgrade, confirm that the service restarts, `--init` preserves the
existing thumbprint, and `psp status --json` still lists both folders. A
lower-version setup must refuse the downgrade before replacing files. For
uninstall, test both answers to the data prompt. The default No answer keeps
`%ProgramData%\PeerSync`; Yes invokes `--remove-data` before deleting the
service executable. Silent uninstall keeps data. Synced folder contents are
never removed by either path.

When a check fails, keep `C:\Temp\PeerSync-setup.log`, the app log under
`%LocalAppData%\PeerSync\logs\app`, and the service log under
`%ProgramData%\PeerSync\logs\service`. Run
`psp diagnostics --output C:\Temp\PeerSync-diagnostics.zip` on each machine
and include the matching time window. Check the Windows Application event log
for source `PeerSync`, `sc.exe query PeerSync`, and the inbound firewall rule
named `PeerSync`. Redact any folder keys before sharing logs.
