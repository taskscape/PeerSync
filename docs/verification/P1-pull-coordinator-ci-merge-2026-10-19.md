# Pull coordinator CI/merge audit

PR #33: https://github.com/taskscape/PeerSync/pull/33

Initial head 90b804d passes CI but review finds five issues. The final head
repairs them with 15 native regressions; the five review threads are resolved
only after code/regression verification and final green CI. See the
[hardening evidence](P1.5.3e4-recovery-scan-hardening-2026-10-19.md).

Required [run 39257061763](https://github.com/taskscape/PeerSync/actions/runs/39257061763)
passes every job below 15 minutes:

| Job | Duration |
| --- | ---: |
| x64 tests/probes/publication/signing/upload | 11m46s |
| ARM64 publication/signing/upload | 4m03s |
| Server 2022 native security/probes and 568 Engine cases | 8m15s |
| Server 2025 native security/probes and 568 Engine cases | 7m18s |
| Required ten-minute boundary fuzz | 11m30s |

Downloaded x64 TRX files confirm 897 tests: 126 Core, 15 Crypto, five Protocol,
568 Engine, 183 Storage, all passed with no failures. Canonical verification
reports 521.3235617 seconds inside the hosted x64 job. Fuzz runs 122,102 seeds
for 600.0023047 seconds with zero crashes. Evidence:
`artifacts/ci/pr33-x64/`, `artifacts/ci/pr33-fuzz/`. This is boundary fuzz,
not production sync simulation.

Submitted head: `f1ad7f4e679457d094bafb21936d005cb2373a00`.
Base: `4265237e6e73aaf90b2958261392a2494a201bca`.
Tested/prospective merge: `617e43ac2f7adcebc607f367d540819ff3a8973e`.
Actual main squash: `32c97fa6e68e9d3bd1e04074345e083185c68232`.
Submitted/tested/prospective/actual trees all match:
`f5e47fafb1f7e86d70454fe30a2debc9ab1f1fe5`.

P0.1.2d and the P1.5.3e coordinator work meet their main/green-CI acceptance.
The automatic main workflow 39259378375 is separate follow-up evidence and is
still running at this audit. Parent file/directory/case/hierarchy coordination,
temporary cleanup, clock admission/UI and production simulations remain open.
Gate P1 is not closed; the production service/transport/UI are not completed.
