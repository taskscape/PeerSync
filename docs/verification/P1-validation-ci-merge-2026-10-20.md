# Bounded P1 validation PR #39 merge audit

[PR #39](https://github.com/taskscape/PeerSync/pull/39) merged at
`2026-10-20T06:37:55Z`. Submitted head
`aada02d095b4b13c165850c59f433e96aa33607a`, synthetic merge
`7fc25a95ca580f67c40bd00fd818697574561b93` and actual main squash
`bf68d97743167730722913672b2562e1e86cf183` have the exact same tree:
`484dba8a2ef768b2dc7733961694709499210beb`. Expected base was
`7b689d0f583835424f632983203e58049704dea9`; all three repaired campaign
review threads were resolved after green final-head CI. No textual replies
were posted and no unchanged CI rerun was requested.

All five jobs in [run 39378499583](https://github.com/taskscape/PeerSync/actions/runs/39378499583)
pass on the submitted head: x64 10m03s, ARM64 4m01s, Server 2022 11m51s,
Server 2025 13m35s and boundary/simulation 29m21s. All normal jobs and coverage
remain enabled. The actual campaign-control step passes all 26 controls.

Downloaded actual final Simulation TRX passes exactly eleven methods, including
all four retained regressions and the connected maintenance case. The shared
scope validator independently confirms the exact names/outcomes/counters.
Generated smoke completes eight flat/four hierarchy scenarios. Maintenance
records two native archive retirements, two native block evictions, 64 fixture
days advanced and two preserved published versions; source informational
version identifies the verified synthetic merge. Native fuzz runs all four
targets for 600.0032555 seconds, 116224 seeds and zero crashes. Supervisor
elapsed time is reported separately. Evidence: `artifacts/ci39-final-evidence/`.

The repaired-source intermediate corpus also passes: five nodes, 4096 initial
native files, 24 overwrites/four renames/four deletions, all 4120 versions
preserved and all reference/native/queue checks passing; see its separate
report. Small/intermediate runs never prove the full corpus line.

Main-push [run 39381507911](https://github.com/taskscape/PeerSync/actions/runs/39381507911)
is running on the exact squash. Its acceptance remains pending, so connected
maintenance/retention and campaign rerun-review checklist items remain open.
The new final-source 100000-file run started on the same squash in a clean
isolated worktree; the old immutable-source run remains undisturbed. Neither
has a claimed passing full-corpus report. Campaign scheduling remains inactive;
hosted preflight and five actual consecutive UTC campaign days are required.
Complete P1, production clock/service integration and later gates remain open.
