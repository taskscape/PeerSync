# P5.5 signed installed-client Archive verification, 2026-10-23

The local Windows test host was upgraded with the signed, self-contained x64
`PeerSync-Setup-1.0.13.0-x64.exe` built from commit `419ab72c8e3da2a35550fdaa933e3e716ff2f368`.
The installer SHA-256 is
`7D1A0A8C027B40A51D5DCBB6E82BD752FF976DC594E67DCFFE09295D43D97A7F`.
Its Authenticode signature validates with the **development-only**
`CN=PeerSync Development` certificate, thumbprint
`6AE1CBFC31F1DB189AF03A82D60C535AE8E0A7BD`; the manifest states
`localDevelopmentOnly=true`, `publicReleaseSigning=false`, and
`workingTreeDirty=true` (the pre-existing unrelated `Sync.zip.tmp`). The
previous package and ProgramData were copied to
`C:\PeerSync-Test\pre-1.0.13-20261023` before upgrade. The silent installer
returned exit 0; its log is `C:\PeerSync-Test\setup-1.0.13-20261023.log`.
The `PeerSync` SCM service is Running and `PeerSyncUpdateInstall` is Ready.
Installed App and service executables both report version `1.0.13` plus the
source revision, and both have valid development signatures. The installed
App SHA-256 is
`DC83A00AEA084BEE63825A30973E8539A81EA08373AE7F7D02CC3B959FF42A53`.

An initial signed 1.0.12 installation exposed a real App startup failure:
`DiagnosticRunJournal.Open` threw Win32 error 4395 while opening an ordinary
`%LocalAppData%\PeerSync\logs\app` child. The diagnostic directory walker
already pins and validates each parent, opens one child at a time with
`FILE_OPEN_REPARSE_POINT`, and rejects a reparse final object. Its extra
`OBJ_DONT_REPARSE` attribute rejected this ordinary profile path on the host.
Removing that redundant attribute fixed direct journal opening and App
startup; a profile-root regression was added. Core passed 151/151, App 21/21,
Service 56/56, and the installed probe project built with zero warnings.

The opt-in probe launched the **installed App executable as a separate
process** on the active Windows desktop, waited for the connected service
banner through Windows UI Automation, selected its real Archive tab and row,
then invoked Restore and Restore as copy, including the real modal copy-name
prompt. It used a disposable service-registered folder and verified the
resulting native file bytes (`before`) for both actions. It removed the service
folder registration afterward and retained the native result at
`C:\PeerSync-Test\archive-client-dab8f799f5384432b4dcc8dfceeeae86`.
The run returned exit 0 with `uiAutomation=true`,
`originalRestored=true`, `copyRestored=true`, and the installed App hash above:

```powershell
& 'C:\Projects\Sync\spikes\InstalledServiceProbe\bin\Release\net10.0-windows10.0.20348.0\PeerSync.InstalledProbe.exe' --client-archive 'C:\Program Files\PeerSync\app\PeerSync.exe'
```

The earlier [reviewed screenshot](archive-tab-installed-service-2026-10-23.png)
shows path, date, size and both Archive actions against the local SCM service.
That capture used the current App assembly in the probe process. This new
check adds signed installed-client packaging and separate-process behavior.
Physical two-PC validation, release signing, and the independent security
review remain separate open gates.

The installed machine-wide WER exclusion values and PeerSync ownership
markers for `PeerSync.Service.exe`, `PeerSync.exe`, and `psp.exe` were each `1`
after upgrade. The machine-wide `LocalDumps` key has no global DumpFolder,
DumpCount, DumpType or CustomDumpFlags value and no per-executable subkey for
these names. This is a local installed-policy audit, not proof that an
administrator cannot create a privileged dump or that other deployment hosts
have the same policy. Uninstall cleanup and independent security review remain
open.
