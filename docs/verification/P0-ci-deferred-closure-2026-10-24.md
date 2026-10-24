# P0 CI deferred-item closure audit — 2026-10-24

Four P0.1.2 child items remained marked WIP after their code had merged. This
audit closes those **historical P0 CI implementation** items from retained
local checks, the exact submitted PR heads, and main-push workflows. It does
not claim that current P5 CI, the physical gates, or a release passed.

| Item | Relevant merged evidence | Required workflow result |
| --- | --- | --- |
| Remove third duplicate Engine execution | [PR #36 merge audit](P1-case-ci-merge-2026-10-19.md) records full Engine on both Servers and all other x64 checks. The later [approved Server 2025 scope](P1-case-capacity-server-partition-2026-10-19.md) retains the full 1,001-conflict method on Server 2022 and default local, omitting only its duplicate on Server 2025. | [PR #36 final head](https://github.com/taskscape/PeerSync/actions/runs/39300189312) passed all five jobs under 15 minutes. [PR #37 final head](https://github.com/taskscape/PeerSync/actions/runs/39313959928) and its [exact main squash](https://github.com/taskscape/PeerSync/actions/runs/39315657236) passed all five under 15 minutes with the approved partition. |
| Overlap Server Engine and independent native/security probes | [Review verification](P1-control-retention-review-2026-10-19.md) records one immutable build, parallel no-build Engine/probes, separate stdout/stderr/TRX and bounded child cleanup. [PR #37 merge audit](P1-control-ci-merge-2026-10-19.md) ties the implementation to its final tree. | PR #37 final head and exact main squash above passed; main's longest job was Server 2025 at 13m02s. |
| Wait for actual ControlPipe readiness | [Delayed real-server regression](P0-control-readiness-ci-2026-10-19.md) reproduces the old early-client failure and verifies fresh PID-bound readiness plus owner/caller and wrong-PID checks. | PR #37 final head and exact main squash above passed, including required x64 watcher coverage and unchanged pipe deadlines. |
| Scope QUIC diagnostic assertions across PID reuse | [Saved real mixed-run replay and synthetic regression](P0.1.2e-quic-pid-evidence-2026-10-19.md) verify process lifetime, role and run ID; `build/Verify.ps1` invokes the fixture and real timeout smoke on x64. | [PR #35 final head](https://github.com/taskscape/PeerSync/actions/runs/39282066051) and its [exact main squash](https://github.com/taskscape/PeerSync/actions/runs/39286215475) passed all five jobs. |

The plan's older “both Servers run every Engine case” wording predates its
2026-10-19 decision record: Server 2025 may omit only the duplicate generic
1,001-conflict capacity method while Server 2022/default local retain it.
The separate approved watcher partition keeps the complete 100,000-operation
watcher check in required x64/default local verification and omits only its
duplicate under overlapping Server disk load. The current scripts still call
the readiness and QUIC evidence checks, and `Test-QuicDiagnostics.ps1` is
unchanged since the PR #35 squash. Later P2 expansion changed the Server 2025
job budget to 25 minutes; the recorded P0 workflows satisfied their original
15-minute limit before that expansion.
