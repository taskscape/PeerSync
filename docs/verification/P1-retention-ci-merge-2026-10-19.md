# Retention and database initialization CI/merge audit

PR #32: https://github.com/taskscape/PeerSync/pull/32

The first run (39245578390) passes all tests but x64 upload and Server 2025
cleanup exceed the 15-minute limit. Exact job annotations and local repair
evidence are linked in the cost and fresh-database reports. No check or runtime
limit was removed. After atomic fresh initialization, required run
[39248576984](https://github.com/taskscape/PeerSync/actions/runs/39248576984)
passes all five jobs:

| Job | Duration |
| --- | ---: |
| x64 build, 834 tests, probes, publication/signing/upload | 10m39s |
| ARM64 publication/signing/upload | 4m07s |
| Server 2022 native security/probes and 522 Engine tests | 8m30s |
| Server 2025 native security/probes and 522 Engine tests | 10m05s |
| Required boundary fuzz | 12m00s |

Fuzz runs for 600.0028198 seconds: 127,631 seeds, zero crashes. This is boundary
fuzz, not production sync simulation. x64's canonical verification reports
521.5502879 seconds; complete job timings above include setup/upload/cleanup.
Evidence is retained under `artifacts/ci/pr32-repaired-{x64,fuzz}/`.

Submitted head: `7f3c582b9366cdc355489f028a735e2cfb7fc070`.
Base: `869239e7839f5883c9c9c29d0c4a9f3746e29086`.
Prospective/tested merge: `0f9f092db51ef90c1d085ac888aab27cc11b5ad4`.
Squash merge on main: `4265237e6e73aaf90b2958261392a2494a201bca`.
All submitted/prospective/actual trees match:
`f998fef980b77a52de76b906d191834aa8cddba9`.

P0.1.2c meets its main/green-PR-CI acceptance. P1.2.4b2b2c and its parent tasks
remain WIP pending production simulation invariants; the production service,
P1 gate and externally deferred physical LAN/native NAT checks are not closed.
The automatic main workflow is separate follow-up evidence. Run 39250207275
passes all 834 unit tests, both native Server jobs, ARM64 and fuzz, then fails
x64's QUIC progress smoke. [The causal timing diagnosis and repair](P0.1.2d-initial-quic-progress-2026-10-19.md)
are included with the pending coordinator batch. No unstarted billing failure
or test failure is inferred from that diagnostic regression; main is currently
red pending its repair.
