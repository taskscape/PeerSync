# P5.2 crash-dump boundary proposal

## Why the current wording needs a decision

`plan.md` says that keys must be absent from crash dumps. A running sync
process necessarily holds folder keys in memory. A privileged Windows operator
can capture that memory with a live or full process dump; PeerSync cannot make
an arbitrary administrator-created dump safe while still using those keys.
This is a scope question for the security review, not evidence that the
existing support-bundle filter is sufficient.

On the installed Server 2025 test VM, a read-only check found no global or
PeerSync-specific `LocalDumps` key and no policy disabling Windows Error
Reporting (WER). Microsoft documents that WER crash reports are enabled by
default and may include minidump or heap data. `LocalDumps` is not enabled by
default, but an administrator can enable it globally or for an executable.
Microsoft also says `LocalDumps` operates independently of WER reporting,
including when WER is disabled. Therefore a WER exclusion alone cannot prove
that no local dump will be written.

Primary references:

- [Using WER](https://learn.microsoft.com/en-us/windows/win32/wer/using-wer)
- [Collecting user-mode dumps](https://learn.microsoft.com/en-us/windows/win32/wer/collecting-user-mode-dumps)
- [WerAddExcludedApplication](https://learn.microsoft.com/en-us/windows/win32/api/werapi/nf-werapi-weraddexcludedapplication)

## Proposed acceptance boundary

Treat automatic application-controlled reporting and arbitrary privileged
memory capture separately:

1. PeerSync does not create crash dumps or include them in diagnostics bundles,
   telemetry or update reports. Its structured logs never contain folder keys.
2. Installed PeerSync processes opt out of automatic WER reporting, with an
   installed-host check of the resulting exclusion. The setting is maintained
   on upgrade and removed on uninstall without changing unrelated global WER
   policy or discarding a pre-existing administrator exclusion.
3. The deployment guide checks global and per-executable `LocalDumps` settings
   before production use. A configured dump is treated as secret material:
   prevent automatic collection where the operator's policy permits, or
   explicitly accept and protect the dump under administrator-only controls.
4. Administrator-initiated live/full dumps and kernel memory acquisition are
   outside the application's confidentiality guarantee. The reviewer records
   this threat-model boundary and tests the automatic-reporting path on an
   installed candidate. No claim is made that a mini dump can never contain a
   key.

This proposal changes the literal P5.2 acceptance sentence. Until approved,
P5.2 stays open. The independent reviewer, physical attack checks and release
gate remain mandatory. The installed VM registry observation is only a
configuration snapshot, not a proof of absent dumps.
