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
Its first `waiting.json` reports the original benchmark PID 36684 live, a
60-hour prerequisite deadline, and no new benchmark child. The waiter PID
31624 was independently observed live. It will require the original run's
terminal record and child exit before starting this payload. It does not
require that older candidate to pass. At launch it rechecks all payload
hashes, the fixture and at least 6 GiB free; during the run it aborts below
1.5 GiB. A 48-hour benchmark ceiling is inside the payload, and a
120-hour task cap bounds the full wait and run.

The new run is **queued, not passed**. Its terminal must show the scanner
targets and a fresh peer with all 1,000,000 native files/bytes verified and
empty stderr before P5.3 can close. The first run is still separate and live;
no second benchmark was started concurrently.
