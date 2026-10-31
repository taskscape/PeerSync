# P5.2 WER exclusion policy, local validation, 2026-10-23

The user approved the [scoped crash-dump boundary](../proposals/P5-crash-dump-boundary.md).
Both Inno installers now package `Set-WerPolicy.ps1` and call it before
starting the service. The client covers `PeerSync.Service.exe`, `PeerSync.exe`
and `psp.exe`; the server covers `PeerSync.Rendezvous.exe`. The script uses
Microsoft's `WerAddExcludedApplication` / `WerRemoveExcludedApplication` APIs
with the all-users machine scope. A separate registry marker records only
entries PeerSync created. Reinstall maintains owned entries, uninstall removes
only those entries, and a pre-existing administrator exclusion is preserved.
An administrator value that does not actually exclude reporting makes setup
fail rather than silently claiming protection.
If an administrator changes an owned value, uninstall leaves that value alone
and relinquishes its marker. Upgrade refuses to overwrite that change.
The installer fails rather than silently starting
the service if exclusion registration fails.

`build/Test-WerPolicy.ps1` ran under Windows PowerShell 5.1 using the current
user registry scope and disposable unique executable names. It passed:

- two-name setup-style `powershell.exe -File` registration and removal;
- idempotent re-registration and cleanup of owned entries;
- preservation of a pre-existing administrator exclusion; and
- rejection of an administrator value that leaves reporting enabled; and
- refusal to overwrite an externally changed exclusion at upgrade, followed
  by preservation of that value at uninstall.

The same Windows PowerShell entry point is now a required step in the
existing installer CI job. The current GitHub Actions budget prevents that
hosted job from starting, so its CI result remains open.

Both client and rendezvous Inno scripts compiled as unsigned development
packages with Inno Setup 7.1. The check used existing published payloads and
does not claim a signed/current-source package or installed behavior. The
attempt to run a machine-wide registry test in the disposable Server 2025 VM
was rejected by automatic approval review with the reason `blocked by policy`;
no guest mutation or machine-scope result is claimed. Installed setup,
upgrade and uninstall checks remain open.

Microsoft documents that automatic WER reports can contain minidump/heap
data, that the exclusion API accepts an all-users scope, and that `LocalDumps`
is controlled independently of WER exclusion. Therefore the installer policy
does not prove key absence from administrator-configured or live/full dumps.
The administrator guides include a predeployment LocalDumps audit; independent
review must verify that policy and the threat-model boundary on an installed
release candidate.

References: [Using WER](https://learn.microsoft.com/en-us/windows/win32/wer/using-wer),
[WerAddExcludedApplication](https://learn.microsoft.com/en-us/windows/win32/api/werapi/nf-werapi-weraddexcludedapplication),
[WerRemoveExcludedApplication](https://learn.microsoft.com/en-us/windows/win32/api/werapi/nf-werapi-werremoveexcludedapplication),
[Collecting user-mode dumps](https://learn.microsoft.com/en-us/windows/win32/wer/collecting-user-mode-dumps).
