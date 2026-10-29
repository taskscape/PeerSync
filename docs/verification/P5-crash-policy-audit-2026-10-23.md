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

On 2026-10-24, the packaged audit ran after the signed 1.0.23.0 upgrades on
both physical Windows clients. The first PC used its installed script; the
second used a one-time SYSTEM task running its own installed script, removed
immediately afterward. Both outputs report all three client executables
excluded from automatic WER reports, no configured global LocalDumps values
and no configured per-executable LocalDumps values. The two read-only JSON
results are retained under
`artifacts/field/client-1.0.23-crash-policy-20261024/`. This is deployed
policy evidence for those two clients at that time. The third PC and physical
Rendezvous server still require their own installed audits, and an independent
reviewer must still verify the approved crash-dump boundary.

On 2026-10-25, the same read-only installed audit ran again on both physical
Windows clients after the signed 1.0.26.0 upgrade. The first PC ran its
packaged script directly; the second PC ran its packaged script through the
existing remote management session. Both results report all three client
executables excluded from automatic WER reports and owned by the installer,
with no configured global or per-executable LocalDumps values. The two JSON
results are retained under
`artifacts/field/client-1.0.26-crash-policy-20261025/`. The audit does not
inspect privileged full/live dumps, OS process telemetry, the third PC or the
physical Rendezvous server. Independent review remains open.

On 2026-10-26, the installed audit was repeated on both physical LAN clients
after their 1.0.31.0 upgrade. Each machine's packaged script read the 64-bit
machine registry and reported `allAutomaticReportsExcluded=true`, all three
client executables owned by the PeerSync installer, and no configured global
or per-executable LocalDumps values. Both services were Running. The two
read-only JSON results are retained under
`artifacts/field/client-1.0.31-crash-policy-20261026/`; the second-PC query
used a temporary WinRM session, after which the first PC's WinRM service and
TrustedHosts setting were restored. On 2026-10-27 the same installed audit ran
through the temporary restricted management session on physical third PC
`LARKBOX-X-1` after its 1.0.31.0 upgrade. Its three executable exclusions and
installer ownership markers were present; global and per-executable LocalDumps
had no configured values. The read-only JSON is under
`artifacts/field/client-1.0.31.0-third-20261027/crash-policy.json`.
These are point-in-time policy observations; physical Rendezvous server,
privileged dump handling and independent review remain open.

On 2026-10-28 UTC, the installed **1.0.41.0** audit ran read-only on the first,
second and third physical LAN clients while their services were Running. Each
installed `Audit-CrashPolicy.ps1` matched source SHA-256
`EAE210F9D4F5D152A2ECAC3771A3B255C99E7D842B847D05DA7522CB5A71AB56`.
Each 64-bit machine-policy result reports automatic WER exclusion and PeerSync
installer ownership for all three client executables, with no configured global
or per-executable LocalDumps values. The source-verified JSON files are retained
under ignored `artifacts/field/physical-50k-20261027/client-1.0.41-crash-policy/`:
first SHA-256 `6DFCDC081CFBC02FF17EB36ED256F639C1723EFE5B398E221E8296F52ED5DE67`,
second `AD7458A8DCDC6342622EFD84C4A5D51DC76B4C321C23E62F5663ADF053CC6A12`,
third `46A7AA5AB8EB01CFBDB93E660F736CE4F24B59F8FAA9BD789F97077AA11C357C`.
The first client was checked locally and the other two through authenticated
temporary WinRM sessions; the first PC's WinRM and TrustedHosts settings were
restored afterward. This covers current installed client policies, not the
physical Rendezvous server, privileged administrator-created dumps or the
required independent threat review.
