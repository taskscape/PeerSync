# P5.2 packaged crash-policy audit, 2026-10-23

`installer/Audit-CrashPolicy.ps1` reads the 64-bit machine WER registry view
without changing it. The client and Rendezvous installers now package it,
and the deployment guides show the commands for each component. It reports
whether each installed executable is excluded from automatic WER, whether a
global or per-executable LocalDumps key contains values, and whether a
PeerSync installer ownership marker exists. It does not print dump-folder
paths or claim that an administrator-created memory dump is safe.

Windows PowerShell 5.1 ran `build/Test-CrashPolicyAudit.ps1` successfully with
a unique current-user dummy executable. The control observed an absent WER
exclusion, the setup-created exclusion and ownership marker, a configured
per-executable LocalDumps value, and exclusion removal. It cleaned up only
its disposable registry entries. The current-user LocalDumps case tests the
audit's registry parsing; Windows' real LocalDumps policy is machine-wide.

The same script read the currently installed local client machine policy:
`allAutomaticReportsExcluded=true` for the service, App and CLI;
`globalLocalDumpsKeyPresent=true`, `globalLocalDumpsConfigured=false`, and
all three per-executable `localDumpsConfigured=false`. The global key has no
values but has subkeys for unrelated applications. The audit does not treat
that empty global key as a configured PeerSync dump policy. This is a
point-in-time host observation, not a guarantee about future administrator
policy or privileged live/full dump capture.

Both unsigned-development Inno scripts were compiled from retained published
payloads with the new audit script included. This verifies packaging syntax,
not a signed/current-source installer, installed Rendezvous host, uninstall
cleanup or independent security review. Hosted CI remains denied before job
start by the GitHub Actions budget. P5.2 stays open.

Microsoft documents that LocalDumps settings operate independently of normal
WER reporting and can be set globally or per application:
[Collecting user-mode dumps](https://learn.microsoft.com/en-us/windows/win32/wer/collecting-user-mode-dumps).
