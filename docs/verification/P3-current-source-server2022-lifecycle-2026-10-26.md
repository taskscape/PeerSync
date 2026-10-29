# Current-source Server 2022 client lifecycle, 2026-10-26

The disposable `PeerSync-P3-Server2022-Clean` Hyper-V guest (Windows Server
2022 Datacenter Evaluation, build 20348) exercised the development-signed
1.0.28.0 client on 2026-10-26. This guest was originally created as a clean
Server 2022 VM but had a running 1.0.16.0 PeerSync installation at the start
of this run. The test therefore verifies the current package's upgrade and
uninstall behavior from that installed baseline; it is not a fresh-OS first
install or an interactive-prompt check.

The setup was copied by PowerShell Direct into the guest and verified there:
166,905,112 bytes, SHA-256
`9B763AB40D19B7602EB6EEDA1E1C012AFC92806CF282CC264F14D09ADB3AB853`,
valid Authenticode signature from the development signer with thumbprint
`6AE1CBFC31F1DB189AF03A82D60C535AE8E0A7BD`. The package manifest
identifies tracked source `e3be5dca83e07a821ab75fe567b3aa3de915c9fb`
and explicitly marks the package local-development-only. Its size and the
under-two-minute active test fit the user-approved 10 GiB / three-hour test
limits.

A checkpoint captured the guest's running 1.0.16.0 service and settings before
the cycle. The first attempt upgraded and completed the keep-data uninstall,
but its fixture incorrectly assumed that a folder left on disk by an older
test was still registered with the service. `psp share` returned `NotFound`
after reinstall; the settings hash had stayed identical. The failed report and
setup logs were retained, then the checkpoint was restored. The corrected
control created a **fresh** folder, granted service access, registered it
through the installed CLI and confirmed `share` before recording the baseline
settings hash. No product-source change was made for this fixture error.

The corrected run passed all of these stages:

- Upgrade 1.0.16.0 to 1.0.28.0: setup exit 0, service Running, installed
  service version 1.0.28.0, CLI status exit 0, unchanged settings hash and
  unchanged eight-byte native sentinel SHA-256.
- Silent keep-data uninstall: uninstaller exit 0; service, local group and
  installed files removed; settings and native sentinel preserved.
- Reinstall 1.0.28.0: setup exit 0; service Running, settings unchanged,
  registered folder still shareable and sentinel unchanged.
- Device-data removal with the installed service executable followed by silent
  uninstall: both processes exited 0; ProgramData, device certificate,
  service, local group, update task and installed files were absent; the
  synced-file sentinel retained its original hash.

The structured run report is
`artifacts/installer-validation-1.0.28/report-passed.json`; the guest setup,
reinstall and both uninstall logs, test control, initial failed report and
independent final-state read are retained beside it. Installer log command
exits 2/1 are expected idempotency checks: the Administrator was already in
the group during upgrade, the group had to be created on reinstall, and no
firewall rule existed to delete before its recreation. The setup and both
uninstallers themselves exited 0 and needed no Windows restart.

After copying evidence to the host, the checkpoint was restored and removed.
The guest again reported the original 1.0.16.0 running service and exact
baseline settings SHA-256; the new test folder was absent. Its original
checkpoint-disabled setting was reinstated, no snapshots remain, and the VM
was shut down cleanly. The interactive remove-data prompt, a clean Windows 11
VM, a non-developer app-only installation, and public release signing remain
open Gate P3/P5 checks.
