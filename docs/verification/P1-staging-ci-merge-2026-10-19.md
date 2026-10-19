# PR #35 staging and hierarchy merge audit

PR #35, Preserve nested deletions and owned staging, merged on 2026-10-19
at 16:32:09 UTC. Submitted head `af8f1cd2fbb5e11746ebd89bd6026abc956e1102`
and squash `779310fdf8a54c0d80ac5b9a349d9cf358779a1a` have the identical tree
`71b96c551e0b16a760b4a50f88b16659b857b809`. The main base remained
`8c9c39ad94a60d8520ee3e3214019af247dd7a2c`; merge used an exact head guard.

All five required checks report completed/success on that head:
[CI 39282066051](https://github.com/taskscape/PeerSync/actions/runs/39282066051),
x64 build/runtime/publication/signing, ARM64 build/publication/signing,
ten-minute boundary fuzz, and virtual-account/standard-user security on
Windows Server 2022 and 2025. GitHub's aggregate run status briefly lagged
the completed successful job/check conclusions. Main follow-up CI is separate.

Follow-up [main CI 39286215475](https://github.com/taskscape/PeerSync/actions/runs/39286215475)
also completes successfully on squash `779310f`; a fresh job-list query confirms
all five jobs completed/success, including the full boundary fuzz run.

Review comments 4315855832 and 4315855903 have reproducing native regressions
and fixes in the submitted head. Both threads were resolved after all five
checks became green. No extra rerun was requested. The final local canonical
check passed 1,029 cases plus native/probe/QUIC/watcher/publication checks in
121.6502721 seconds, exit 0; boundary fuzz was skipped locally and passed in CI.
See the P1.5.3f and P1.5.3g1-g3 reports for scope and exact behavior.

The earlier local 1,000-scenario run applies to pre-review head `9f17d76`,
not the submitted final head. No five-day, 100k-file, production-network or
case-conflict validation is claimed by this merge. These remain mandatory.
