# Hierarchical content-route baseline

The new generated hierarchy harness is WIP, with stronger eventual native,
queue-drain and reference-coverage checks. It does not establish P1 acceptance.
The original flat random sequence and independent counters remain unchanged.

Local smoke `build/Run-Simulation.ps1 -Cases 8 -HierarchicalCases 4` fails,
exit 1: all eight flat scenarios pass, hierarchy seed 702753096 passes, and
seed 1878823561 retains four needed rows and pending directory cases on two
of seven nodes after 24 healed rounds. Seed 37 passes after allowing transient
per-pass failures while retaining the eventual convergence/queue/loss oracles.

Retained evidence: `artifacts/p1-hierarchical-smoke.log` and
`artifacts/simulation/20261020T010321-d8088c07f04c4ae0b85e301cc1c361a7/`.
A direct deterministic-key replay also fails in 42.580 seconds:
`artifacts/p1-hierarchical-debug.log` and
`artifacts/simulation/hier1878823561-debug/`. Temporary first-chance exception
instrumentation was removed after capturing the exact causal exception.

The stalled member is original `topic/nested/shared`; its signed payload
block exists on node 6, while both stuck nodes prefer node 2, which lacks it.
Their current remote tables contain no exact advertiser of the old directory
record. The callback throws `No authenticated directory case advertiser is
available`, caused by `Published block is unavailable`. Metadata graphs are
complete and no metadata/publication queue remains; retries cannot discover
the existing alternative payload. PR #38 stays open for a focused routing
repair and regression. Do not weaken the final oracles or tick a checkbox.

The baseline is generated NTFS/SQLite/in-memory transport evidence, not LAN,
NAT, installed-service, large-scale or five-day CI proof.
