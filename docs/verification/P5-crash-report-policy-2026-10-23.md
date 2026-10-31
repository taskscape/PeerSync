# P5.2 installed crash-report policy observation, 2026-10-23

The disposable Server 2025 VM `PeerSync-P3-Server2025-Clean` was reached
through PowerShell Direct. It reported Windows build 26100 and a running
`PeerSync` service. A read-only registry inspection found no global
`HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps`
configuration, no per-executable LocalDumps keys for PeerSync Service,
Rendezvous, App or CLI, and no machine policy disabling WER.

A reversible API probe used a dummy executable name, never a PeerSync binary.
`WerAddExcludedApplication(dummy, allUsers: true)` returned `S_OK`, created a
value under `HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\ExcludedApplications`,
and `WerRemoveExcludedApplication` returned `S_OK`. The dummy value was removed.
This establishes an installed-host mechanism for an automatic-reporting opt-out;
it does not establish that PeerSync is currently excluded.

Microsoft documents that default WER crash reports may include minidump and
heap information, and that administrator-configured LocalDumps operate
independently of WER exclusions. Thus absence of a LocalDumps registry key is
only a point-in-time configuration observation, not proof that keys cannot
appear in an OS or administrator-created dump. The [P5.2 boundary proposal](../proposals/P5-crash-dump-boundary.md)
records the necessary acceptance decision. P5.2 and independent review stay
open.

References: [Using WER](https://learn.microsoft.com/en-us/windows/win32/wer/using-wer),
[Collecting user-mode dumps](https://learn.microsoft.com/en-us/windows/win32/wer/collecting-user-mode-dumps),
and [WerAddExcludedApplication](https://learn.microsoft.com/en-us/windows/win32/api/werapi/nf-werapi-weraddexcludedapplication).
