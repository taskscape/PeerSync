# Implementation status — 2026-10-19

Current phase: P1, starting P1.1.1 (folder keys/text). P0 gate passed on
2026-10-19 under the user's approved external-test deferrals. Code is merged through [PR #1](https://github.com/taskscape/PeerSync/pull/1),
with [green CI](https://github.com/taskscape/PeerSync/actions/runs/39141978283).
Ten tasks and the performance/certificate subchecks are verified. The CI
budget repair is merged through [PR #4](https://github.com/taskscape/PeerSync/pull/4),
with [green CI](https://github.com/taskscape/PeerSync/actions/runs/39144766855). The
physical LAN/NAT validation is deferred to Gates P2/P4 by the user's explicit
2026-10-19 decision. Development signing and diagnostics are now merged through
[PR #5](https://github.com/taskscape/PeerSync/pull/5), with all five jobs
[green](https://github.com/taskscape/PeerSync/actions/runs/39179220764) in
13m44s. The merged tree matches the tested head. See [the follow-up report](verification/P0-signing-diagnostics-2026-10-19.md).

New external evidence: two physical PCs on different LANs transferred 1 GiB
over Tailscale in 220.78 seconds (4.64 MiB/s), with matching receiver session
and byte count. The public Windows Server probe passed exact HTTP/2 and HTTP/3
on TCP/UDP 99 with mutual TLS, identity verification and three bidirectional
messages. See [the cross-network report](verification/P0-cross-network-2026-10-19.md).
This does not satisfy the physical LAN benchmark or native NAT matrix.

Implemented locally:

- All eleven production project skeletons, test project skeletons, .NET 10
  solution, central packages, lockfiles and strict analyzer settings.
- Executable P0 probes and seven spike reports with measured results and limits.
- Streaming FastCDC foundation, native BLAKE3 selection and throughput harness.
- Bounded structured diagnostics, shared QUIC session correlation, locale-safe
  filtered bundles, and a server/second-PC deployment runbook.
- CI for x64/Arm64 build/publish, actual P0 tests, 10-minute boundary fuzz,
  and native Windows Server 2022/2025 virtual-account/transport checks.

Verified locally: zero-warning Release build and Roslyn analysis of all 33
projects, nine unit cases, IPv4/IPv6 1 GiB QUIC, exact HTTP/2 and HTTP/3 mutual
TLS on port 99, standard-user/virtual-account pipe and private-key checks,
100,000 watcher operations with scan recovery, and collector filtering under
Polish and English locales. Native hashing reached 1.051 decimal GB/s on the
documented warm-cache 10 GiB corpus.

The full local runner completed in 736.65 seconds, including 225,275 boundary
fuzz seeds over 600 seconds with zero crashes and x64 publishing. Arm64 probes
also self-contained published; Arm64 execution is unverified. A separate
Server 2025 CI job passed all native probes and service/security checks on
build 26100. The Server 2022 service readiness/port repair also passed in its
follow-up VM job. Final push and PR CI are now green: x64 verification took
694.36 seconds and completed 68,810 seeds over 600 seconds with no crashes.
The complete push workflow took 14m18s. Later post-merge jobs exposed cleanup
timeouts, now repaired by separating fuzz into its own required parallel job.
The repaired PR workflow passed in 11m32s; its fuzz job completed 129,037 cases
over 600 seconds with zero crashes. See the verification and spike reports for
the exact scope of each historical run.

Open prerequisites:

- [Physical LAN/native NAT evidence](https://github.com/taskscape/PeerSync/issues/2)
  for P0.2.1 at Gate P2 and P0.2.2 at Gate P4. These tasks remain unchecked.
  All three source-port reuse options failed locally; the approved implementation
  baseline is reachable direct connections and relay after five seconds. The
  final native transport decision remains required before the WAN gate.
- Public release signing remains separate from the completed P0.1.3
  development signing path and the user's local development certificate.

P1.1.1 is in progress; P2-P5 have not started.
The production engine, client service,
central tracker/relay, control app and installers are not implemented. The
user-requested production diagnostic hooks are explicitly tracked as P2.5.6
and P4.1.7. P0 bundles are self-contained test probes, not a deployable sync
product. P6 remains optional.
