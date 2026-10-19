# Clock/transport/simulation CI and merge audit

[PR #34](https://github.com/taskscape/PeerSync/pull/34) is merged. Final required
[run 39267562796](https://github.com/taskscape/PeerSync/actions/runs/39267562796)
passes all five jobs on submitted head
`8125561ad400bc170243404cb3736cc8ad44b943`:

| Job | Duration | Result |
| --- | --- | --- |
| x64 tests/probes/publication/signing/upload | 11m16s | Passed |
| ARM64 publication/signing/upload | 4m09s | Passed |
| Server 2022 native security and 620 Engine cases | 10m25s | Passed |
| Server 2025 native security and 620 Engine cases | 11m12s | Passed |
| Required ten-minute boundary fuzz | 11m49s | Passed |

Downloaded x64 evidence confirms 970 cases: 129 Core, 15 Crypto, seven Protocol,
nine Net, 620 Engine, 187 Storage and three Simulation, all passed. The generated
property method completed eight real-engine scenarios in 24.86249 seconds;
the two other Simulation methods cover the saved checker regression and the
native loss-oracle negative control. Canonical x64 verification reports
532.8462181 seconds. Fuzz runs 129,479 seeds for 600.0001733 seconds with zero
crashes. This boundary fuzz is not production simulation. Small evidence only
was downloaded to `artifacts/ci/pr34-x64/` and `artifacts/ci/pr34-fuzz/`.

Base: `32c97fa6e68e9d3bd1e04074345e083185c68232`.
Tested/prospective merge: `dff0af426e432f423c8bf34f8b00073564e1e1ff` (also
recorded by the hosted environment artifact).
Actual main squash: `8c9c39ad94a60d8520ee3e3214019af247dd7a2c`, merged
2026-10-19 14:31:02 UTC. Submitted/tested/prospective/actual trees all match:
`8bad187ec1d0a91cd4e11a49195e06ff5341f9ef`.

All four confirmed review threads are resolved after final-head CI verification:
growing range targets (4314760551), disconnected admission generations
(4314760622), source cancellation/disposal (4314760675), and shared folder
teardown (4314760711). Twelve new cases cover the fixes; baseline evidence
reproduces seven failures before repair. The original first-head green run
39264665998 did not close those findings.

This closes UTC Hello/clock foundations, the in-memory interface/fabric,
connected folder integration and its review hardening, and the initial
reproducible scenario/oracle child. Clock admission through a real authenticated
production transport and rendered service-status UI remain open, as do general
case coordination, stale-temp cleanup, expanded simulations, 1,000 CI cases,
five consecutive CI days and the 100,000-file zero-loss gate. P1.5.3f has separate
local evidence and is not included in this merged head. Production sync remains
unavailable. The automatic main-push [run 39269668129](https://github.com/taskscape/PeerSync/actions/runs/39269668129)
also passed all five jobs on the exact squash head, verified 2026-10-19. This
follow-up does not include later local hierarchy/staging changes.
