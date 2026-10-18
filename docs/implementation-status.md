# Implementation status — 2026-10-18

Current phase: P0. Code is merged through [PR #1](https://github.com/taskscape/PeerSync/pull/1),
with [green CI](https://github.com/taskscape/PeerSync/actions/runs/39141978283).
Nine tasks and the performance/certificate subchecks are verified. The
remaining LAN/NAT and signing items are explicitly blocked; Gate P0 has not
passed. See [the verification report](verification/P0-2026-10-18.md).

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
projects, eight unit cases, IPv4/IPv6 1 GiB QUIC, exact HTTP/2 and HTTP/3 mutual
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
The complete push workflow took 14m18s. See the verification report and spike
reports for exact scope and historical source/run evidence.

Open prerequisites:

- [Physical hardware/LAN/NAT evidence](https://github.com/taskscape/PeerSync/issues/2)
  for P0.2.1 and P0.2.2. All three source-port reuse options failed
  locally; relay for hard NATs is provisional.
- [Signing identity](https://github.com/taskscape/PeerSync/issues/3) for P0.1.3.

P1-P5 have not started because section 0.1 of plan.md forbids starting a phase
before the previous gate passes. The production engine, client service,
central tracker/relay, control app and installers are not implemented. The
user-requested production diagnostic hooks are explicitly tracked as P2.5.6
and P4.1.7. P0 bundles are self-contained test probes, not a deployable sync
product. P6 remains optional.
