# P1 foundations: CI and merge audit — 2026-10-19

PRs #11–#20 are merged into main, ending at
`9b153b634ef29c7b467cbcf8f2140ce541eadc06`. Every listed run completed
all five required jobs successfully on its exact PR head. Before each merge,
the prospective main merge tree and the head tree were compared with the tree
from CI's recorded checkout revision. The actual merge tree matched afterward.
No administrator bypass or billing exception was used.

| PR | Required CI | Tested checkout | Actual merge | Matching tree |
| --- | --- | --- | --- | --- |
| [#11](https://github.com/taskscape/PeerSync/pull/11) | [run 39194274449](https://github.com/taskscape/PeerSync/actions/runs/39194274449) | `b45ea45d7b1e8a74043a1b0ad3b51f0eab71d70f` | `c87b58d6ee026c06c4ad1dd77e79a837176414d9` | `236238aa9ffb9a01df6797f353adca45d2bcf279` |
| [#12](https://github.com/taskscape/PeerSync/pull/12) | [run 39194295995](https://github.com/taskscape/PeerSync/actions/runs/39194295995) | `40916fc1d4b2a42c9e0c767b2dfa31e781832348` | `bd5f7d64d45466b5874d096773a31b320333e087` | `b7aeff9c19ed6469fe7101143edcd91dfdae4d6e` |
| [#13](https://github.com/taskscape/PeerSync/pull/13) | [run 39194321410](https://github.com/taskscape/PeerSync/actions/runs/39194321410) | `fe0ae9b9b83e696af2617b9daf01739291b7b86b` | `c3690bd0a3d43a20ed40f72ae2cd4a69cd1c7288` | `11ba319c2a7a4e8601dcd9b777d7091c713ef782` |
| [#14](https://github.com/taskscape/PeerSync/pull/14) | [run 39194335654](https://github.com/taskscape/PeerSync/actions/runs/39194335654) | `c271bea3291c726e4394853bf3d19a3158d86813` | `68ff2c34a17e6f9fcf48a59070698462f1cdf1bb` | `5c8256806107935c941f6216fee916e66adebb0f` |
| [#15](https://github.com/taskscape/PeerSync/pull/15) | [run 39194360122](https://github.com/taskscape/PeerSync/actions/runs/39194360122) | `47b4200d2506169ea4f6840099f30281a986fb2c` | `e449e5b1b6945fc379dd4397a15de9604a253bd2` | `eda2d0690914f47f0fec39549f022f61af045151` |
| [#16](https://github.com/taskscape/PeerSync/pull/16) | [run 39194375492](https://github.com/taskscape/PeerSync/actions/runs/39194375492) | `d7f19f9522828c8d0c01becfbc36d9fceb925a6e` | `80ec0bb850131a4ea1337bbc5d21074a72ea878e` | `3115ae884507dd38f2ced095ccb22dce306ab71d` |
| [#17](https://github.com/taskscape/PeerSync/pull/17) | [run 39194379089](https://github.com/taskscape/PeerSync/actions/runs/39194379089) | `cb1abd42d1cde55ac821e38989fba550fe947f02` | `53d76134af491046030f55b730a094734c65dfc7` | `050fae1fb05cfbe9a7576d7ddc56ef1fb00f95e0` |
| [#18](https://github.com/taskscape/PeerSync/pull/18) | [run 39195418389](https://github.com/taskscape/PeerSync/actions/runs/39195418389) | `896fedcda87fd89a7ccbe2ad42d29c7a503d5d1e` | `eeb6a4c9195c0d1a09f5b9532461d8dd300176c8` | `34515dc94ca903287280ae314ba533ee1d863ee6` |
| [#19](https://github.com/taskscape/PeerSync/pull/19) | [run 39196897016](https://github.com/taskscape/PeerSync/actions/runs/39196897016) | `7a742cd58bdace0294c77da8f06a7f3f7dc1e939` | `344b4bc2c19e62081ade98e458f25b04fe1a21b2` | `e9caec9ea0073b32e4d4789365b475ec4469e450` |
| [#20](https://github.com/taskscape/PeerSync/pull/20) | [run 39198195497](https://github.com/taskscape/PeerSync/actions/runs/39198195497) | `06241d73272238e2853c6f9854f92aa655dac7fc` | `9b153b634ef29c7b467cbcf8f2140ce541eadc06` | `4710b1f81bf5ce7588d079dac45f21a213af45f5` |

Downloaded evidence is under `artifacts/ci/`: `P1.3-final`, `P1.4.1-final`,
`P1.4.2-3-final`, `P1.5.1-final`, `P1.5-safe-final`, `P1.5-assembly-final`,
`P1.5-metadata-final`, `P1.5-journal-final`, `P1.5-gate-final` and
`P1-review-final`. The machine-readable comparison record is
`artifacts/ci/p1-promotions-2026-10-19.json`.

The final review-repair run passed 307 x64 cases (126 Core, 15 Crypto,
5 Protocol, 124 Engine, 37 Storage), native probes, diagnostics and watcher
checks. Both Server 2022/2025 virtual-account/security jobs and their Engine
tests passed. Win-arm64 is cross-build/publish/signing coverage; no native
Arm64 execution is claimed. Signing evidence records a valid timestamped test
signature, tamper rejection and cleanup, without exporting a private key.
The separate required boundary fuzz passed 126,299 seeds in 635.62 seconds.
This exercises parsers/chunking/vector/glob boundaries; it is not the future
P1.6 multi-node simulation or its five-consecutive-day gate.

The earlier local evidence remains relevant: scanner 100k unchanged walk below
10 seconds, one-million-row indexing below 60 seconds, state-key protection,
native watcher recovery and process-wide scan serialization. Review repairs
are included in main before any affected task is marked complete. Existing
verification reports describe their historical local state; this audit
supersedes their pending-billing/CI/merge status.

Completed acceptance supports P1.3.2, P1.3.3, P1.4.1, P1.4.2, P1.4.3,
P1.5.1 and P1.5.3a. Conflict-table filesystem integration, the puller, archive
policy, clock guard, simulation and Gate P1 remain open. Regular-file
application/recovery subsequently merged in PR #21 with green CI; its separate
[verification report](P1.5.3b-recoverable-install-2026-10-19.md) records that
run and merge. No production sync deployment or phase completion is available.
