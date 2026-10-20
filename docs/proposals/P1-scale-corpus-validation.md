# Five-node 100000-file validation

Gate P1 requires five simulated nodes converging on an actual 100000-file
corpus with zero lost versions. Small generated properties cannot establish
this gate. Keep scale work opt-in, measured and separate from required hosted
smoke/fuzz. Do not create scheduled jobs or add a large default unit test.

Provide a local explicit scale runner with a small preflight option and a
required corpus size in its report. Generate all roots beneath one fresh
fixture owned by the runner. Use five public TransportFolder instances, real
NTFS and SQLite, and the in-memory transport. No direct index mutation may
stand in for network transfer. Record exact source revision, runtime, hardware,
phase timing, counts, index high waters and durable queue state. Emit progress
at least every 30 seconds during long work. Preserve failed fixtures and logs.

Create 100000 distinct generated payload files in bounded directory buckets
on the initial RW node. Wait for complete signed local publication, then use
connected peer page/block transport to replicate to all five nodes. Require
exact native path/spelling/file-count/hash equality on each node, stable index
high waters and drained needed/recovery/case/publication work. Verify complete
authenticated block-reference coverage. Compare the recursive native oracle
only at candidate quiescence so repeated partial pulls do not reopen the whole
corpus unnecessarily. Empty directories count separately from regular files.
Private cached blocks never satisfy the loss oracle.

After initial convergence, partition peers and make a deterministic bounded
sample of overwrites/renames/deletes/conflicts; retain the expected original
and new payload digests. Heal and require the same final native/queue/reference
checks, then verify every overwritten version in visible current/conflict
bytes or an indexed native archive across the five nodes. Record actual
mutations, including skips. A run that merely copies unchanged files or hashes
private caches does not prove overwrite preservation. Sample size is reported
separately from the real 100000-file count.

Use bounded index pages, engine passes and an explicit run deadline; exhaustion
fails with retained evidence. Retry time advances through injected clocks,
with no wall-clock sleeps used to bypass backoff. Production admission and
native ownership/recovery guards stay active. Native payload generation, reads
and any cleanup must remain within the fresh fixture and prohibit reparse
traversal. Do not accept an existing user directory as a destructive fixture.
Successful cleanup verifies its exact absolute fixture root first; failures
are never automatically removed. Avoid enormous failure ZIPs: save a manifest
and preserve the actual fixture when a safe artifact-size bound is exceeded.

Begin with a five-node small corpus to verify runner/report/oracle failures,
then measure intermediate scale before the full 100000-file run. Keep every
smaller run labeled with its actual file count and `gateP1Passed=false`.
Only a fully completed 100000-file report can satisfy the corpus line; it does
not close remaining P1 tasks, 1000-seed/five-day CI or external P2/P4 checks.
