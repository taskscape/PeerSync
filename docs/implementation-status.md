# Implementation status — 2026-10-18

Current phase: P0. [PR #1](https://github.com/taskscape/PeerSync/pull/1).
No phase gate has passed. Task boxes await the plan's merged-to-main and green
CI rule in addition to their individual acceptance criteria.

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
documented warm-cache 10 GiB corpus. The full fuzz/CI/publish runs have their
own evidence and must not be inferred from this list.

Open prerequisites:

- [Physical hardware/LAN/NAT evidence](https://github.com/taskscape/PeerSync/issues/2)
  for P0.1.4, P0.2.1 and P0.2.2. All three source-port reuse options failed
  locally; relay for hard NATs is provisional.
- [Signing identity](https://github.com/taskscape/PeerSync/issues/3) for P0.1.3.
- The version-stamped Server VM and final CI results are pending validation.

P1-P5 have not started because section 0.1 of plan.md forbids starting a phase
before the previous gate passes. The production engine, client service,
central tracker/relay, control app and installers are not implemented. The
user-requested production diagnostic hooks are explicitly tracked as P2.5.6
and P4.1.7. P0 bundles are self-contained test probes, not a deployable sync
product. P6 remains optional.
