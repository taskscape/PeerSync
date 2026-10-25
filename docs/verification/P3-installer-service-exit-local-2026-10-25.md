# Installer service-exit guard — local source check, 2026-10-25

The physical second-PC restart race showed that SCM can report `Stopped`
before the old PeerSync process releases its listeners and mapped image.
Both client and rendezvous installers previously polled only `sc query`.
That could let an upgrade replace files, or an uninstall delete them, while
the previous service process was still draining.

`installer/Stop-ServiceAndWait.ps1` is now packaged into each installer for
upgrade and retained in the installation for uninstall. It captures the
service PID and configured image path, requests a normal stop, waits for SCM
`Stopped`, then waits for that PID and any remaining process running the
exact configured image to exit. The entire operation has a 30-second
deadline. An uninspectable matching process path, a timeout, or a service
restart fails the operation before files or data are removed. It does not
kill the process or change service recovery settings.

The Windows PowerShell 5.1 parser accepted the helper. Its absent-service
path returned `SERVICE_ABSENT` and exit 0 without touching the installed
client. Read-only WMI inspection confirmed that the running local
`PeerSync` service exposes both its PID and quoted configured image path,
and the corresponding process exposes its executable path. Both Inno Setup
7.1 unsigned-development scripts compiled from the staged x64 payloads.

The helper then ran against the first physical PC's installed 1.0.23.0
service. It captured old PID 24640, requested stop, printed
`STOPPED_AND_EXITED`, and returned 0. An independent process query confirmed
the old PID was gone. The service restarted as PID 20680, and the installed
CLI returned `productionSyncAvailable: true` with connected peers. This
checks the actual stop/process-exit guard and recovery on one PC. It does
not prove that a newly signed installer uses the guard during upgrade or
interactive uninstall. The installed three-PC 1.0.23.0 deployment still
lacks the source listener-bind retry and this packaged installer guard.
Repeat signed upgrade, SCM restart and uninstall checks on target Windows
builds before closing P2.5.1 or P3.3.3.

## Signed physical-PC upgrade follow-up

Development-signed client 1.0.24.0 was built from `caeada2` with a valid
timestamped signature. The first physical PC upgraded from 1.0.23.0 with
setup exit 0. Its setup log records `STOP_REQUESTED` for the old PID and
`STOPPED_AND_EXITED` before file replacement, no Windows restart, and a
running 1.0.24.0 service afterward. The installed service, app and CLI all
have Valid signatures; the helper matches the source SHA-256. Settings and
three existing native test files kept their SHA-256 values, and live status
reported production sync available. Retained before/after records and the
setup log are under `artifacts/field/first-pc-1.0.24-upgrade-20261025/`.

The same signed installer was copied to the second physical PC with an exact
SHA-256 match. Its SYSTEM task initially rejected the signature because the
public PeerSync Development certificate was absent from LocalMachine Root.
The exact public certificate, containing no private key and matching the
installer signer, was imported there. SYSTEM then reported a Valid signature.
The setup itself stopped safely before replacing files, exit 7: the new guard
could not parse that PC's **unquoted** existing service image path under
`C:\Program Files`. The service remained Running at 1.0.23.0 with the same
settings and sampled native-file hashes. Its log is under
`C:\Temp\PeerSync-Field-1.0.24\setup.log` on the second PC.

The guard now accepts an unquoted path only through the exact expected service
executable name, resolves and verifies the file, and still refuses any
ambiguous/missing image before requesting a stop. Both installers now pass
escaped quotes through `sc.exe` so SCM stores a quoted image path; the client
also repairs an existing path during upgrade. A temporary local stopped-service
probe confirmed that the new `sc.exe` argument form stores
`"C:\Program Files\PeerSync\service\PeerSync.Service.exe"`. A new signed
package and physical upgrade are required to verify this source repair; the
failed 1.0.24.0 attempt does not count as a second-PC upgrade.

The helper's read-only `-InspectOnly` mode validated the first PC's quoted
image through Windows PowerShell 5.1 and the second PC's legacy unquoted image
under a SYSTEM scheduled task (task exit 0); both services remained Running.
A separate temporary stopped-service probe confirmed `sc.exe config` changes
an unquoted `ImagePath` to the intended quoted value. These checks validate
the parser and command form without claiming a completed second-PC upgrade.
