# Current-source million-file run queued, 2026-10-25

The bounded prior-path merge at revision
`8ccb4bc9a5e914c916d6aad5f8e6d2c280c841c1` builds in Release. A
self-contained win-x64 `PeerSync.Benchmarks` payload was published to
`artifacts/scale/queued-prior-merge-20261025/bin/` without changing the
earlier benchmark's immutable payload. Its 251-file size/hash manifest is
`prepared.json` (SHA-256
`34D79F8D813C495DA13B2934B653B6124524CA47D348A31F19D6EAB6CA657143`);
the executable SHA-256 is
`0F32323C3DE3E7B1BF260E29304C1128262C7F374BBADEEFDBC05265FD80690B`.
The `-PreflightOnly` check verified every payload file and the retained native
million-file fixture before registration.

The temporary SYSTEM task `PeerSync-P5-Scale-PriorMerge-20261025` runs
[`build/Run-QueuedMillionScale.ps1`](../../build/Run-QueuedMillionScale.ps1)
(SHA-256
`BC2B6F384A81DD9FC6A7C2A6F67A5E7C8E4735085C3255F7238FF9A5D5DB6962`).
An identical `run-queued.ps1` copy is retained in the queue, and the task
action points at that fixed copy for any restart. Changing the task action
left its existing waiter PID running.
Its first `waiting.json` reports the original benchmark PID 36684 live, a
60-hour prerequisite deadline, and no new benchmark child. The waiter PID
31624 was independently observed live. It will require the original run's
terminal record and child exit before starting this payload. It does not
require that older candidate to pass. At launch it rechecks all payload
hashes, the fixture and at least 6 GiB free; during the run it aborts below
1.5 GiB. A 48-hour benchmark ceiling is inside the payload, and a
120-hour task cap bounds the full wait and run.

A separate tiny **runner-only** control used a five-file executable payload,
an empty fixture directory and a synthetic two-line scanner/peer report for
1,000 files. It exercised the prerequisite-terminal check, child launch,
stdout parsing and success-terminal writer; its terminal is
`artifacts/scale/queued-runner-smoke-20261025/terminal.json` with exit 0 and
empty stderr. This tests orchestration only: no real files were scanned or
transferred in that control, and it provides no P5.3 performance evidence.

At registration the new run was **queued, not passed**. Its terminal must
show the scanner
targets and a fresh peer with all 1,000,000 native files/bytes verified and
empty stderr before P5.3 can close. The first run was then separate and live;
no second benchmark had started concurrently.

## Prerequisite passed; current-source run started, 2026-10-26

The prerequisite fixed-binary run finished with `status=passed`, exit 0,
empty stderr, scanner target met and all 1,000,000 fresh-peer native paths
and bytes verified. Its full evidence is in
[the post-fuzz run record](P5-post-fuzz-scale-queue-2026-10-24.md).
Only after that process exited did the queued watcher start the distinct
`8ccb4bc` payload at 04:31:13 UTC as benchmark PID 83240. The launch record
has the expected executable SHA-256, `prerequisiteStatus=passed` and
54,150,909,952 free C: bytes, above the 6 GiB start threshold. The 1.5 GiB
in-run disk guard remains active.

The current-source scanner row has already passed: 1,000,000 native files,
1,001,000 rows, 247.4074071-second first index, 15.5127545-second unchanged
walk, zero rehashes/updates, 0.01735% idle CPU, 129,937,408-byte observed
peak working set, 81,117,184-byte sampled peak private memory, preserved
sequence, `correct=true` and `targetMet=true`. The benchmark child is still
live in its fresh-peer source-publication phase; `stderr.log` is empty.
Neither its peer result nor a current-source terminal has been reported, so
P5.3 remains open.

## Superseded by the three-hour, 100,000-file gate, 2026-10-26

The user [reduced required test size and duration](../requirements/three-hour-ten-gib-test-budget.md)
while this immutable million-file benchmark was still running. I verified
that PID 83240 was the expected executable and command line, then stopped only
that child. Its watcher wrote `terminal.json` at 06:49:51 UTC with
`status=failed`, `reason=benchmark-failed-or-target-missed`, exit `-1`, no peer
row and empty stderr. The terminal's scanner row remains a valid stress
measurement: 1,000,000 files, 1,001,000 rows, 247.4074071-second first
index, 0.01735% idle CPU, 129,937,408-byte peak working set and zero
unchanged rehashes/updates. The last live stage was source publication; the
fresh peer had not begun native verification. Both benchmark and watcher PIDs
exited. The fixture, databases, stdout/stderr and terminal were retained.

This deliberate interruption reflects the revised test budget, not a proven
product failure or a P5.3 pass. A new current-source 100,000-file end-to-end
benchmark with a three-hour deadline is required. No duplicate million-file
run is authorized by this record.
