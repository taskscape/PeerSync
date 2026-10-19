# Block catalog/reference and CI merge audit — 2026-10-19

Combined [PR #30](https://github.com/taskscape/PeerSync/pull/30) is squash-merged
after [run 39226806320](https://github.com/taskscape/PeerSync/actions/runs/39226806320)
passes all five required jobs. It includes block ownership/capture recovery,
archive retirement witnesses, persistent current/needed/history reference
snapshots, native change-time scanning, duplicate CI removal and faster P0
probe publication. PR #29 is closed because its changes are included here.

| Identity | Value |
| --- | --- |
| Submitted PR head | `42c7143df9d90be5680ad1232d66f4d3e01d851e` |
| Tested revision from x64 environment/signing evidence | `d82edc089d7ed5c376c9d1622216ebd0c15af7a6` |
| Actual squash merge | `55a452f728b838f081f1d84448bcb955da81aeff` |
| Head/tested/prospective/actual source tree | `5b8e54e7b6d93732528bf7a5b0fef838a068078a` |

The prospective tree was calculated with `git merge-tree --write-tree` against
freshly fetched main. GitHub's commit tree API and the fetched actual main tree
confirm equality after merge. The PR head was matched explicitly when merging.

| Required job | Result | Total job time |
| --- | --- | ---: |
| x64 build/test/publish/sign | passed | 612 s |
| ARM64 build/publish/sign | passed | 227 s |
| Native Server 2022 | passed | 410 s |
| Native Server 2025 | passed | 692 s |
| Boundary fuzz | passed | 700 s |

All five jobs finish within 15 minutes; the longest is 11m40s. The x64 canonical
runner reports 482.5906271 seconds and all 633 cases pass (126 Core, 15 Crypto,
5 Protocol, 402 Engine, 85 Storage). Native Server 2022 and 2025 each pass all
402 Engine cases. ARM64 is a cross-build/signing check, not native execution.
Fuzz completes 118,667 seeds over 635.5067728 seconds with the full requested
600-second duration. It is boundary fuzz, not P1 simulation or five days of
simulation evidence.

Both signing summaries report Valid signatures, RFC3161 timestamps, PE tamper
HashMismatch rejection, no private-key export, and removal of runner-generated
test trust and certificates. The environments/signing summaries identify the
same tested revision and a clean working tree. Only evidence artifacts were
downloaded to `artifacts/ci/P1-catalog-references-corrected/`; repeated large
probe bundles were not downloaded. The prior x64 timeout is documented in
`P0.1.2b-probe-publication-2026-10-19.md` and is superseded by this green run.

The submitted head starts one PR CI run and no feature-branch push CI. The
required post-merge main workflow runs separately at the actual merge commit.
No existing artifact or account budget was deleted/changed. Block retention,
archive content manifests/provenance, policy reference release, verified
sweeping, case conflicts, pull scheduling, clock guard, simulation and later
phases remain open. No deployable production sync service or passed P1 gate
is claimed.
