# PR #36 case-preservation merge audit

[PR #36](https://github.com/taskscape/PeerSync/pull/36) merged at 18:24:44 UTC
on 2026-10-19. Submitted head `78056c9b8bd72192a3df269d5cf1521858a15d0f`
and squash main `c28931318289cee2bc8617a5ab3654410c6b3375` have the exact
same tree `2b2d6e0716814091a69121156e01b1261fcd3156`.

All five jobs in [run 39300189312](https://github.com/taskscape/PeerSync/actions/runs/39300189312)
passed on that submitted head: x64 7m54s, ARM64 6m37s, required fuzz 11m42s,
Server 2022 10m37s and Server 2025 14m24s. Each is under 15 minutes. Both
Server jobs retain the full Engine suite, including the real 1,001-path test.
The x64 job exercises the other suites, simulation, native probes, signing and
publication without its third duplicate Engine run. The fuzz command remains
600 seconds. Four reproduced review findings were resolved after all five
checks passed; no textual reviewer replies were posted.

Local final verification, partial-capacity baselines and repaired native/storage
cases are recorded in [the capacity/CI report](P1-case-capacity-ci-partition-2026-10-19.md)
and [review report](P1.5.3h3b-case-review-hardening-2026-10-19.md). The prior
cancelled x64 run is not passing evidence. No manual rerun was started.

The required new main-push workflow is pending at this checkpoint. Plan boxes
remain unchecked until its checks and each task's complete acceptance hold.
Directory/subtree case mapping, production authenticated transport and full
phase-gate scenarios remain open; this merge completes regular-file work only.
