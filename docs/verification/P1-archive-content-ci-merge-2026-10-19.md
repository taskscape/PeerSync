# Archive source/content CI and merge audit — 2026-10-19

[PR #31](https://github.com/taskscape/PeerSync/pull/31) is squash-merged after
[run 39230084203](https://github.com/taskscape/PeerSync/actions/runs/39230084203)
passes all five required jobs. It includes transactional archive provenance,
verified content manifests, complete-stage recovery and stable source coverage.

| Identity | Value |
| --- | --- |
| Submitted PR head | `58edbab188ff24812a31a3148e854bb7b6f6adee` |
| Tested revision from environment/signing evidence | `24de485bc4de5fcbf3bcb182baa013c22d3a865a` |
| Actual squash merge | `869239e7839f5883c9c9c29d0c4a9f3746e29086` |
| Head/tested/prospective/actual source tree | `47bab353a08caca36262a7ad3e2a2253f39b8679` |

Freshly fetched main plus `git merge-tree --write-tree` gives the same tree as
the submitted head and tested revision. The merge matched the submitted head
explicitly, and GitHub's actual commit tree matches afterward.

| Job | Result | Total job time |
| --- | --- | ---: |
| x64 build/test/publish/sign | passed | 620 s |
| ARM64 build/publish/sign | passed | 244 s |
| Native Server 2022 | passed | 479 s |
| Native Server 2025 | passed | 798 s |
| Required boundary fuzz | passed | 708 s |

All jobs finish within 15 minutes. The x64 canonical runner reports 499.8330036
seconds, with all 685 cases passing (126 Core, 15 Crypto, 5 Protocol, 427 Engine,
112 Storage). Both native Server variants independently pass all 427 Engine
cases. ARM64 compilation/signing passes; native ARM64 execution is not claimed.
Fuzz completes 125,271 seeds over 633.8057892 seconds with the full requested
600-second duration. It remains boundary fuzz, not production simulation or
five days of simulator runs.

Both signing summaries identify the tested revision, Valid signatures and
RFC3161 timestamps, tamper rejection, and removal of runner-generated trust
and private certificates. Evidence/validating TRX were downloaded under
`artifacts/ci/P1-archive-content-final/`; large repeated probe bundles were not
downloaded. Only one PR workflow was started for the submitted head. Main's
preceding PR #30 merge also passes all five post-merge jobs in run 39228544523.

This closes the provenance and content-manifest children. Policy-aware history
accounting, legacy protection, explicit expiry/reference release, verified
block deletion/recovery, simulations and the P1 gate remain open. A newly
reproduced older-archive reuse expiry issue is being repaired in the next
retention branch before enabling any block sweeping. No production sync
service or completed data-deletion policy is claimed.
