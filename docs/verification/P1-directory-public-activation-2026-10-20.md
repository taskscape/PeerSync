# Public directory coordinator activation

Public Puller and TransportFolder now enable the tested directory snapshot
staging/publication path. Standalone Scanner retains legacy mode because it
does not own the publication worker. The connected case suite now constructs
the public TransportFolder directly, covering nested/file-directory cases,
four-node RW/RW and RW/RO gossip with reference coverage, offline source
forwarding and edited-copy forwarding with both original authors offline.

New public Puller tests verify nested snapshots on new and existing legacy
folders, causal legacy upgrade, unchanged repeated scan versions/native mtimes,
complete reference coverage and unsigned RO local edits. Three prior Puller
assertions reflected old timing. Tests now explicitly require post-apply
reconciliation scans and the private-publication barrier: an unknown child
survives a directory tombstone and is signed immediately, while its parent
remains a tombstone until the next publication pass proves the snapshot. Work
ordering, preserved native data, queue draining and zero failure checks remain.

Local Release focused validation: all 49 public coordinator/Puller/connected
cases pass, zero failures/skips, exit 0, 12.718 seconds. Engine and Engine.Tests
compiler/configured analyzer diagnostics return no warnings/errors, workspace
warnings or truncation. `git diff --check` passes.

Evidence: `artifacts/p1-directory-public-activation-focused.log` and
`artifacts/tests/directory-public-activation-focused/`. The first full activation
run recorded exactly the three old-timing failures above, with 938 passes;
see `artifacts/p1-directory-public-activation.log`.

Public activation is a local source change, not installed/deployed evidence.
Directory CI/main acceptance and complete phase/external gates remain open.
No checkbox is ticked before merged green CI. Canonical results follow below.

Final canonical local `build/Verify.ps1 -Runtime win-x64 -SkipFuzz` passes,
exit 0, 176.825 seconds. All 1478 canonical cases pass (944 Engine, 282 Storage),
zero failures/skips, plus eight real engine simulation cases. Diagnostics,
control readiness, native/transport probes, QUIC timeout evidence and win-x64
probe publishing pass. Boundary fuzz was explicitly skipped locally and remains
a required CI job. gateP1Passed=false is retained. Evidence:
`artifacts/p1-directory-public-activation-canonical.log`,
`artifacts/verification-win-x64.json`, canonical TRX files in `artifacts/tests/`,
and `artifacts/simulation/20261020T001219-42f1640875d34d5d9d62021ba6869a61/`.
