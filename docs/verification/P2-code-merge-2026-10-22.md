# P2 code merge and validation audit — 2026-10-22

PR [#44](https://github.com/taskscape/PeerSync/pull/44) merged the P2
authenticated peer service, including all commits from the narrower protocol
PR #43, as main commit `9181ad7628b8a9bd9507251f7eb582d34b6bbfbe`.
The reviewed branch head was `218fa11e817fa556ab6e9cdfa0c9144eaea74e25`;
the tested synthetic merge was `c0b628a3bba039fdabd6f8bc4f82bf9861946a31`.
The locally computed prospective merge tree, hosted synthetic merge tree and
actual main tree all equal `ce42feb98db0db56484b5246321d61a566a29461`.
The unrelated untracked `Sync.zip.tmp` was left untouched.

The current-head [PR workflow](https://github.com/taskscape/PeerSync/actions/runs/39781459500)
passed all nine jobs. Both Windows build/publish/signing jobs passed. The x64
Net TRX contains passing `ProofIsDirectionalAndBoundToBothCertificatesNoncesAndFolder`
and `DifferentCertificateDeviceIdFailsThePinBeforeFolderAdmission` tests.
Server 2025 passed all 1,029 Engine cases, native 1 GiB probes and the
virtual-account/private-key/control-pipe checks in 735.860 seconds; Server
2022 also passed its complete Engine/native scope with the capacity stress.
The strict boundary aggregate accepted 15 distinct Simulation methods, all
eight retained regressions, eight flat/four hierarchy generated cases,
connected maintenance, matching source and binary identity, and 600.006
seconds of five-target fuzz across 170,401 seeds with zero crashes.

The [main workflow](https://github.com/taskscape/PeerSync/actions/runs/39784030135)
exposed a connection race in its x64 App suite: 13 of 14 App tests passed,
but `InstalledHostQuicClockSkewAndReconnectReachVerifiedPipeAndRenderedWpf`
failed before rendering because the authenticated candidate was closed when
the connection manager tried to install it. The code waited for the losing
transport attempt to drain before installation, creating this timing window;
the remote close reason was not retained by the test. All earlier x64 suites
passed. The same source passed the PR workflow; the main failure is retained
as a real timing defect, not reclassified as a pass.
The focused follow-up installs the authenticated candidate before draining
losers, preserves caller-cancellation cleanup, and checks the timing window
with deterministic Net tests. The final main run passed its other eight jobs:
Arm64, both Windows Server security jobs, fuzz, all retained shards and the
strict boundary aggregate. Follow-up CI remains pending. Do not start P3
implementation or mark its CI prerequisite complete
until the follow-up source passes main. Historical PR failures and repairs
remain in [the causal inventory](P2-pr44-ci-repair-2026-10-22.md).

The follow-up source passed `./build/Verify.ps1 -Runtime win-x64
-FuzzSeconds 600` locally in 799.785 seconds: all nine test projects
(1,706 tests), all 15 real-engine simulation methods, native P0 and QUIC
diagnostic probes, six spike publishes, and 600.000 seconds of five-target
fuzz across 176,395 seeds with zero crashes. The App TRX has 14/14 passes,
including the formerly failing installed-host WPF test. The two new Net
cases deterministically cover a winner closing during loser cleanup and a
closed candidate falling through to the live transport. This is local
current-source evidence; the follow-up hosted PR and main runs remain
required before P3 code may start.

The first [follow-up PR #45 workflow](https://github.com/taskscape/PeerSync/actions/runs/39787315008)
failed earlier than App: its x64 Net TRX has 49/50 passes. The sole failure
was `CandidateAttemptsFollowSourcePriorityAndFallBackAfterFailure`, whose test
supplied both a public and a relay candidate that could authenticate at once
but asserted the public session must win. The manager promises the first
authenticated candidate after source-staggered starts, so scheduling may
select either session. That test now supplies only the failed LAN and live
public candidate; separate tests retain all-source ordering and first-winner
coverage. All 50 Net tests passed locally after this test-only change. The
earlier 799.785-second canonical run predates only this fixture correction;
it has not been represented as a full run of the amended PR head.
The first PR #45 run's other eight jobs passed, including both Server jobs,
Arm64, fuzz, all retained shards and the strict boundary aggregate. Its
failure inventory contains this one Net assertion.

Copilot review of PR #45 found a separate cancellation cleanup issue in the
new manager path: if disposal of a just-authenticated candidate throws after
the caller cancels, that error would mask cancellation. The follow-up now
logs the disposal error and propagates the caller's cancellation, with a
deterministic throwing-disposal test. All 14 ConnectionManager tests pass
locally. A full local Net run of this revision had 50/51 passes; the sole
failure was `TcpFallbackCanListenOnTheQuicUdpPort`, where Windows refused the
TCP bind on the UDP-probed port after 256 attempts. The local host reports
multiple excluded TCP port ranges; no transport-code change is attributed
to that environment failure. Current-head hosted CI remains required.

The next [PR #45 run](https://github.com/taskscape/PeerSync/actions/runs/39793408220)
passed x64 Net 51/51 and App 14/14, including the reviewed cancellation
case. Its Server 2022 job failed in the P0 IPv6 QUIC probe: the client
reported a verified 1 GiB digest and exited zero, but the server had received
all bytes and then raised `QuicException.StreamAborted` at `digest.ack`.
`QuicBasics` read exactly 32 digest bytes and disposed its QUIC stream
without reading the server's FIN. [.NET QUIC guidance](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/quic/quic-overview)
states that disposal aborts an unread read side. The client now requires an
EOF after the digest before closing. A rebuilt local `Run-P0.ps1 -SkipWatcher`
passed both IPv4 and IPv6 1 GiB mutual-certificate transfers, the pipe and
rendezvous probes; its `artifacts/p0/summary.json` still correctly leaves
the physical P0 gate false. The hosted Server 2022 failure is retained, and
the full amended-head matrix remains required.

The local x64 package
`artifacts/deploy/PeerSync-win-x64-20261022T125442Z-c194ea93.zip` has SHA-256
`BEE324C1F32F416556558B0923B6C029E520A7363B558437EDBCCC0B53543D50`.
It contains self-contained service/CLI executables, the scoped firewall helper
and two-PC runbook. Its manifest names source `218fa11`, retains
`gateP2Passed=false`, and records `workingTreeDirty=true` because of the
untracked unrelated ZIP; no tracked source was modified after that commit.
This package was built and inspected locally, not reinstalled on a second PC.

Gate P2 remains open. A second installed PC, physical 1-Gbit LAN QUIC
benchmark, three-PC 50 GB corpus, actual Windows Server interoperability,
sleep/network-change checks and 72-hour soak still need their specified
external evidence. The user-approved implementation phasing permits P3 source
work after code, local and main CI acceptance without turning those physical
checks into passes or calling this a LAN beta.

## Second main-run hierarchical convergence repair

PR #45 merged as main `4cd74358f2b1a0f4422c6094d288b12fd9b80961` after
its [nine-job PR run](https://github.com/taskscape/PeerSync/actions/runs/39795227892)
passed. The subsequent [main run](https://github.com/taskscape/PeerSync/actions/runs/39800861363)
passed x64 Net 51/51 and App 14/14, including the installed-host WPF test,
but its fuzz-smoke job found a separate generated hierarchical failure at
seed `1718992307`. The runner retained the replay and six-node fixture. Five
nodes converged on a deleted `topic`; the sixth retained a live
`TOPIC/nested/shared` after 32 healing rounds. Its ordinary `TOPIC`
publication waited for a child reconstruction, the reconstruction waited for
the old child to advance causally, and the scheduled remote tombstones for
that child were blocked by the ancestor publication. The aggregate cannot
accept this failed main run; other jobs may still finish independently.

The focused follow-up preserves the unpublished ancestor clock. A signed
remote tombstone may pass that ancestor barrier only when it causally
dominates the target's signed local vector, its sender index range is complete,
and neither the target nor a descendant has an ordinary pending publication.
The folder and reference gates, native guards, case-preservation checks and
durable recovery remain in force. Mutation advances the directory capture
epoch, so the queued parent cannot publish a stale child snapshot; the next
scan can rebuild it. Debug journals record
`ancestor-publication-bypassed` without paths or payloads. Four focused
native file/directory apply-and-recovery cases and a separate live-byte
archive preservation case pass. The prior native/range barrier theory still
checks altered native children, incomplete sender ranges, flagged rows and
case-preservation work; its live-row case now has an explicit archive oracle.
The exact seed
replays successfully: six nodes, ten operations, 14 published versions,
eight healing rounds, zero staging files, 22.435 seconds. This is local
source evidence, not yet a green full verifier or hosted current-head run.

The next local canonical attempt passed all 1,034 Engine and 300 Storage
tests, but its WPF render tests captured black frames because the Windows
session was disconnected (`quser` reported `Disc`). The hosted PR #45 App
job had passed the same 14 tests; current-head hosted App validation is still
required. The separately completed P0 probes, QUIC diagnostics and x64 spike
publishes passed; five-target fuzz completed 600.001 seconds and 198,540
seeds with zero crashes.

The separately run eight-case Simulation batch passed all eight existing
retained seeds and 14 of 15 methods, but generated seven-node hierarchy seed
`1994764809` exceeded the unchanged 32-round bound. Its fixture showed the
visible trees matching by round 28, while late case-clash propagation staged
48 directory publications per peer and left queued work after round 32. The
worker's default 32-directory selection could not drain that fan-out promptly.
Raising only its bounded directory count to 64, while retaining the
1,000-member cap and the same 32-round oracle, makes the exact seed converge
in 12 rounds with seven nodes, 14 operations, 15 preserved versions and zero
staging files. Both failed seeds are now permanent retained cases, divided
among the existing three CI shards. The registry and strict aggregate require
17 methods and all ten retained cases; synthetic scope and artifact rejection
controls pass (23 and 12 controls respectively). Two additional native
controls prove that a concurrent tombstone still waits behind the ancestor
and a causally dominant tombstone cannot pass its own ordinary publication;
the final Engine suite passes 1,036/1,036. The fresh full Simulation
run passes all 17 methods in 8m58s: ten retained seeds, eight flat generated
cases, four hierarchical generated cases and connected maintenance. The
strict TRX scope reader confirms 17/17 methods, ten retained and one
maintenance case. The local App render limitation and current-head hosted
PR/main matrix remain open.
