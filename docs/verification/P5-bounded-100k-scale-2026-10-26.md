# P5.3 bounded 100,000-file scale gate, 2026-10-26

The revised [test budget](../requirements/three-hour-ten-gib-test-budget.md)
requires a current-source scan and exact fresh-peer native verification of
100,000 files within one three-hour benchmark execution. These local
performance assertions **passed**. The formal P5.3 task remains open until
the PR is merged and current-head CI is green under `plan.md` §0.2. This run
does not stand in for installed-service throughput or the remaining physical
LAN, WAN and release checks.

The runner was `build/Run-P5-Scale.ps1 -Files 100000 -NoBuild`. Its retained
evidence is in
`artifacts/scale/p5-20261026T065907-6c2710a9dbe14c209f0376eff8ef96e4/`;
the separate `-fixture` directory retains source and peer native trees,
databases and diagnostic journals. `provenance.json` records clean tracked
source revision `36c24c80119e608c49db5e94a7c9c78188022db0`, binary
SHA-256 `59C81456F24EFD4A7CE740C8F11CEE2C790BEADA9ACCE448CF452B9F48C41728`,
100,000 requested files and a 2026-10-26 06:59:07 UTC start. The later
`cd7317b` commit only changed the physical corpus default and documentation;
it did not change the benchmark or sync engine.

| Assertion | Retained result | Limit |
| --- | ---: | ---: |
| First index, including database open | 22.7908399 s | <1,200 s |
| Native source files / local index rows | 100,000 / 100,100 | Exact |
| Unchanged rescan | 2.0581112 s, zero hashed or updated rows, sequence preserved | Zero changes |
| Idle CPU | 0.0130207% | <1% |
| Peak OS working set | 132,837,376 bytes | <300,000,000 bytes |
| Sampled peak private bytes | 82,735,104 bytes across 100 samples | <300,000,000 bytes |
| Fresh-peer native path/byte verification | 100,000 of 100,000 files and 100,100 peer index rows | Exact |
| End-to-end elapsed time | 5,332.17265 s (88.87 min) | <10,800 s |

`summary.json` reports `status: passed`, `runnerExitCode: 0`, scanner
`targetMet: true`, and peer `correct: true`. The retained merged benchmark
stream contains the final peer result and no `Exception`, `ERROR`, `FAILED`
or failed-status record; this runner did not save a separate stderr file.
Free C: space went from 36,335,833,088 to 34,064,773,120 bytes, so the
test did not approach its 6 GiB start guard. The scanner observed zero
unchanged rehashes/updates. The peer's verification checks every generated
native pathname and byte, plus the expected index-row count.

The benchmark uses 100,000 one-byte real files in 100 directories and two
production folder coordinators connected by the trusted in-memory transport.
It measures file-count indexing and convergence, not a 9 GiB physical corpus,
on-wire throughput, service installation, NAT, relay or a three-hour soak.
Those independent gates remain open in `plan.md`.
