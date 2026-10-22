# P3 installer smoke on clean Windows Server 2025

This is supplemental local validation, not the Gate P3 clean Windows 11 or
Server 2022 check and not the two-PC usability check. The VM was installed from
Windows Server 2025 Datacenter Evaluation media, build 26100, with no Visual C++
v14 Redistributable initially present. The x64 PeerSync setup was a signed
development build; its installer and bundled prerequisite signatures were
verified before running it. No production signing certificate was used.

The first setup installed and started the service, and the CLI reached its
local control API. Adding a folder exposed a missing native prerequisite:
NSec's libsodium dependency could not load because the guest lacked
`VCRUNTIME140.dll`. Installing Microsoft's signed x64 v14 Redistributable made
the same command succeed. This led to bundling the architecture-matched
redistributable in each setup, with a publisher/hash check in the build.

The corrected setup was tested from a state with PeerSync uninstalled and the
redistributable removed. The prior ProgramData state and synced fixture file
were retained. The setup exited 0, installed the runtime, and started the
service. The previously registered folder's share RPC and a new folder-add RPC
both succeeded through the installed CLI. Running the same setup over the
installation exited 0; the service was running afterward, the original folder
remained accessible, and the fixture file still had SHA-256
`775B39482DB51D8443311DC02EFCA29DDBE97E78B435375D484613F056BF0A0A`.
A silent uninstall had exited 0 and preserved ProgramData and the fixture.

A temporary standard Windows account on the same guest could not call the
installed control API (`psp status` exit 1). An elevated `PeerSync.exe
--authorize-user <SID>` saved the account authorization, restarted the service
through SCM, and exited 0. The same account and logon session then called the
API successfully (`psp status` exit 0), without signing out. The helper's
redacted journal recorded one transient SCM start error 1053 followed by a
successful retry and RUNNING state. The test restored the prior settings and
removed the temporary account; the service remained running. The same check
passed again after upgrading with the final locally signed x64 setup, whose
manifest hash and guest Authenticode signature were verified before install.

The VM's setup logs are under `C:\Temp\PeerSync\` in the local test guest.
This smoke did not exercise the rendered WPF authorization flow, interactive
remove-data prompt, Windows 11, Server 2022, Arm64 execution, or two-PC sync;
their plan gates stay open.
