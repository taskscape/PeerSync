# Server 2022 upgrade and repeated-uninstall cycle, 2026-10-23

The disposable `PeerSync-P3-Server2022-Clean` Hyper-V guest is Windows Server
2022 Datacenter Evaluation (build 20348). Its earlier first 1.0.0 setup had
copied the service but failed during `--init` with the runtime message
`Your Windows doesn't fully support CET. Please install all available Windows
updates.` The guest then installed available Windows updates, reported a
required reboot and lost its heartbeat while left running. A guest-only
restart restored the heartbeat. At UBR 5622, the same installed 1.0.0
`--init` exited 0, the service started, and the verified `psp status --json`
client exited 0. This is evidence for that VM, not a claim that every Server
2022 image has the same prerequisite state.

A current-source 1.0.12 development-signed setup was verified by matching
SHA-256 and valid Authenticode signatures on host and guest. Its silent
upgrade exited 0, left the service Running and the verified CLI working,
kept `config/settings.json` byte-for-byte unchanged, retained the SYSTEM
updater task, and installed WER exclusions without a LocalDumps policy. A
real test folder was granted to `NT SERVICE\PeerSync`, registered through
the installed CLI and given an eight-byte sentinel with SHA-256
`FCF4E8D7D9F50F5618DFE6796E0DE44ECAAC06B2A60CEE1B1CCF097B6BE9E87D`.

The 1.0.12 silent keep-data uninstall removed the service, files, task,
firewall rule and owned WER entries while preserving settings and the
sentinel. Reinstalling 1.0.12 kept settings and the registered folder; the
installed share RPC succeeded. A direct installed `--remove-data` then exited
0, removed ProgramData and the device certificate, and preserved the
sentinel. The following silent uninstall exited 0 but **left the stopped
`PeerSync` service and `PeerSync Users` local group registered**, pointing
the service at a now-deleted executable. Its log omitted the conditional
`[UninstallRun]` `sc.exe` and `net.exe` entries during that second cycle.
This is a real uninstall defect, not a failed test assertion. The exact
orphaned service and group were removed in the disposable VM after verifying
the service was stopped and its image path matched the deleted test package.

Both installers now delete their service from the checked
`CurUninstallStepChanged(usUninstall)` path before files disappear; the client
also deletes its local group there. The fragile conditional service/group
`[UninstallRun]` entries were removed. A new 1.0.13 development-signed x64
client package and x64 rendezvous package compiled and passed host signature
checks. The client package's exact bytes and signer were verified again in
the Server 2022 guest. The rendezvous package was tested on the separate
Server 2025 guest below.

The 1.0.13 guest cycle used a fresh folder so the old `.psp` marker could not
be mistaken for a new identity. It covered initial install, grant and
registration, silent keep-data uninstall, reinstall, successful share RPC,
direct `--remove-data`, and silent uninstall. After the first uninstall the
service and group were absent, settings retained their SHA-256, and the
sentinel was unchanged. After reinstall the service was Running and the
registered folder still answered. After remove-data and the second uninstall,
the service, group, update task, firewall rule, installer-owned WER values,
ProgramData, device certificate and install directory were absent; the
synced-file sentinel still had the exact original hash. Inno logged a
transient directory-delete retry, and its self-delete completed just after
the uninstaller process exited; the final check confirmed the directory was
gone. Guest setup/uninstall logs are under `C:\Temp\PeerSync`; the
one-off cycle control is retained under `artifacts/installer` on the host.

The disposable Server 2025 guest's `PeerSyncRendezvous` service was upgraded
from 1.0.6.0 to the exact 1.0.13 package after matching host/guest SHA-256
and Authenticode signer. Setup exited 0, left the service Running, bound TCP
and UDP port 99, and installed its WER exclusion without a LocalDumps policy.
A silent uninstall removed the service and firewall rules but retained its
ProgramData directory. Reinstalling the same package restored the running
service. A **second** silent uninstall exited 0 and removed the service,
install directory, both firewall rules and owned WER entry; ProgramData stayed
for reuse. The package was installed once more, restoring the Running 1.0.13
service and both port-99 listeners for subsequent tests. Guest logs under
`C:\Temp\PeerSync` retain the upgrade, both uninstalls and final reinstall.

The same guest was then upgraded to the current-source 1.0.14.0 development
package containing the Windows PowerShell 5.1 preflight correction. It kept
the service Running and both listeners; the packaged no-argument deployment
preflight exited 0 with zero issues and validated local TLS. See
[packaged preflight evidence](P4-central-deployment-preflight-2026-10-23.md).

This closes the observed repeated-uninstall defect on this VM. It does not
exercise the interactive data-removal prompt, a clean Windows 11 VM, Arm64
execution, or physical two-PC usability, so Gate P3 remains open. The client
installer uses a trusted development certificate rather than a release
signer, and the VM's test folder remains solely as retained evidence.
