# PR #37 shared debug retention and Server workflow repair

Review identifies a real leak: per-run rotation cannot expire files in older
GUID directories. A probe of the original app/service allocation pattern creates
twelve finished run directories; all twelve remain, exceeding the proposed
shared eight-run limit. Evidence: `artifacts/p1-run-retention-baseline.json`.
The small baseline fixture is retained with local artifacts, not deleted through
an ambiguous cleanup command.

Both entry points now use DiagnosticRunJournal. Each active writer still owns
its distinct GUID directory and retains the existing journal rotation. Shared
startup, fifteen-minute and close sweeps enforce age/count/byte limits on
eligible inactive runs: eight, 100 MB and 24-hour debug-file expiry. Disposing
closes the journal before releasing its active lease, then sweeps the newly
inactive run; the final app exit does not require another launch to enforce its
budget. A shared sweep lease coordinates processes with a bounded close wait.

Native guards pin the root and every ancestor, open children relative to those
handles, reject reparse traversal and hardlinks, and verify the ownership marker
and strict log names before deleting exact held objects. There is no recursive
pathname deletion. Active, locked, unmarked and unexpected content is preserved;
deferred counts and quota pressure are diagnostic metadata, not permission to
delete foreign objects. Unmarked legacy directories are deliberately outside
the ownership/eligible-byte claim. Active writers remain individually bounded
and can prevent an absolute root-wide quota while they are running.

Ten native Core tests pass, including twelve sequential finished runs without
manual cleanup, shared count/byte limits, periodic expiry, active-writer and
ancestor protections, unknown/unmarked data, locked retry, root/run/file symlinks,
hardlinks and concurrent runs. A separate test process keeps writing while the
parent sweeps, then retires after it exits. Evidence is in
`artifacts/p1-run-retention-{build,tests}.log`, `artifacts/tests/run-retention/`
and `artifacts/tests/run-retention-child/`. Local-drive non-reparse log roots
are required by these guards. No redirected-profile installation is claimed.

The first PR #37 head passed all five hosted jobs, but its separate main baseline
[run 39302273686](https://github.com/taskscape/PeerSync/actions/runs/39302273686)
timed out on Server 2025. Build/setup/probes consumed eight minutes before Engine
started; its cancelled partial TRX contains 704 passed cases, not a complete
720-case pass. No assertion failure or billing denial was found. Evidence:
`artifacts/pr36-main-2025-cancelled.log` and
`artifacts/ci-pr36-main-2025/`. That main run does not close plan checkboxes.

The Server runner now starts the complete no-build Engine process immediately
after one immutable solution build and overlaps the independent P0/cross-account
probes. Probe publication touches Core/SpikeSupport outputs, not active Engine
test output. It captures stdout/stderr/TRX and both outcomes, bounds the Engine
deadline/child shutdown, and records a fresh run ID plus running/failed/passed
summary so stale successful evidence cannot survive a new failure. All five jobs,
all native/boundary cases, 600-second fuzz and 15-minute limits remain.

The first local parallel check passed 720 Engine cases and the real virtual-account,
private-key denial and unauthorized/authorized standard-user checks in 78.76
seconds after build. This is local evidence, not a hosted runtime prediction.
Final canonical `build/Verify.ps1 -SkipFuzz` passes all 1,176 cases plus
native/probe/watcher/QUIC/publication checks in 167.3637844 seconds, terminal
exit 0. Counts: 144 Core, 15 Crypto, seven Protocol, nine Net, 720 Engine,
218 Storage, 39 Contracts, eight Service, 13 App and three Simulation methods
(eight property scenarios). The separate child process is retained in its own
TRX rather than added to the outer case count. Local boundary fuzz is skipped.
The final log is `artifacts/p1-control-review-final-verification.log`; simulation
evidence is `artifacts/simulation/20261019T190057-a6d538f4bbd049529d6944bac2c176bf/`.

The final parallel runner also terminates exit 0: run
`78446f142851416db72cf2a756baacd7`, 94.9935888 seconds after build, all 720
Engine cases, virtual-account TLS, private-key denial and both standard-user
pipe checks pass. Logs/stdout/stderr/TRX and fresh status are in
`artifacts/p1-parallel-service-final-verification.log`,
`artifacts/p0-services/` and `artifacts/tests/native-engine/`.
The two temporary test services are absent afterward. Scoped Core/Core.Tests/
App/Service compiler/configured analyzers are clean without truncation or
workspace-load warnings. These are local observations, not hosted predictions.
Review resolution, final-head CI and main acceptance remain pending.
