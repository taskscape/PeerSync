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
