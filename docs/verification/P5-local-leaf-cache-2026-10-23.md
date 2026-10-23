# P5 local-only leaf publication cache, 2026-10-23

The live million-file source exposed a global-epoch cost: a read-only snapshot
at about 6,015 seconds found 309 completed directory publication caches and
847 queued directories. Some completed siblings had been restaged by a later
scan even though their direct signed children were unchanged. The run still
uses the earlier committed binary and remains diagnostic evidence; this change
cannot affect that process.

The earlier general signed-manifest shortcut was rejected after retained
hierarchical simulation failures. This narrower scanner check applies only
when a published directory's native identity, signed record source, native
child-name fingerprint and count still match, but its cached input epoch is
stale. It requires a local-only leaf: no remote or needed rows, no remote
causal entries in the parent or child vectors, no nested or pending child
directory, and an exact match of every signed child source in the original
capture digest. The indexed child metadata must agree with each signed file
record. A changed signed child or concurrent epoch/work change keeps the
ordinary publication path. The new debug event records only a member count,
without a path or key.

Local controls so far:

- The focused directory suite passed 194/194 on the tightened scanner. The
  final-source full Engine suite passed 1,038/1,038 after the metadata
  consistency check.
- The retained hierarchical suite passed 10/10 twice, including seed
  1583987140, which had failed with the rejected general shortcut. The later
  run used the parent/child vector and remote-work guards and completed in
  11 minutes 1 second. A broader local Simulation run then passed all 17 test
  methods, including the same 10 retained seeds, eight generated flat cases
  and four generated hierarchical cases. Its evidence is in
  `artifacts/simulation/20261023T012622-3eb7bf226393474a9eecf0c632f443d5/`.
  The final metadata check only changes a cache hit into a conservative miss
  if the indexed row disagrees with the signed record. After rebuilding that
  final source, the previously failing seed 1583987140 passed again in 15.7
  seconds through the generated hierarchical runner.
- A separate 10,000-file fresh-peer control completed in 457.575 seconds:
  10,000 native file bytes and pathnames verified, 10,010 destination rows,
  zero needed rows, and all source publications drained. This uses two
  production coordinators over the trusted in-memory test transport. Its
  published benchmark binary was produced before the final read-only query
  hoist and metadata check; those changes do not alter the signed-manifest
  acceptance rule on a consistent local-only corpus. The retained local
  fixture is `artifacts/scale/p5-leaf-cache-10k/`; the benchmark executable
  SHA-256 is
  `34A5FEAD6A23DD2F8DD3ACDD8A88ED6CD6A4C4C58C9602683ACBCAA36DC0F71F`
  and its Engine DLL SHA-256 is
  `EABC63ED1459433B643B230EF964F6B87E4593196C9956D4F7E365EFBFE4208B`.

The live million-file baseline still has not published all source directories.
The 10,000-file control and local tests do not meet the million-file target.
Physical transport and the P5 release gate remain open.
