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
