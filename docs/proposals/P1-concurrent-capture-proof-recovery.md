# Concurrent capture proof recovery

Read-only inspection of the live bf68d97 five-node corpus finds drained queues,
all 782 directory graphs complete per node and one remaining capture proof on
node 1. Validation using its immutable Engine binary proves checksum, root and
marker identity; the independent 48-byte final block matches the expected
hash/length and committed catalog native ID. The original proof refers to a
different native ID, and its old temporary is absent. The proof was captured
at 2026-10-20 09:08:22 UTC. No fixture bytes or databases were changed.

Recovery currently defers this exact state, making reference coverage false.
The concurrent-installation loser path explains its creation: it tracks the
winning native object but omits the losing proof from its post-catalog cleanup
set, then deletes its own temporary. Reproduce both behaviors against unchanged
submitted sources before fixing them. The green bounded CI run does not cover
this two-owner state and is insufficient to merge over a known liveness issue.

Track the losing proof through FULL registration of the verified winner, then
remove only that proof metadata. For already retained/restarted states, require
a valid root/marker-bound proof, confirmed absent temporary, guarded independent
final bytes with exact hash/length, and an existing committed ownership entry
matching the same root/hash/length/final native ID. An indexed read-only catalog
predicate must not register or adopt a replacement. Preserve unknown/changed /
linked/reparse objects and foreign occupied temporaries. Keep the existing
24-hour byte-retirement policy, reference guards and loss/convergence oracles.

Add deterministic two-owner and obsolete-proof/reference-coverage regressions,
catalog/native negative controls, registration-failure/restart coverage and
sanitized diagnostics. Record focused baseline failures, repaired focused/full
validation and final PR/main acceptance. Reopen ownership parents for this new
counterexample. Preserve all live corpus source identities and evidence; do not
hot-patch or manually clean their fixtures. A confirmed nonconvergent run may
be canceled only after preserving its actual failing state, never because an
observation interval elapsed. A completed repaired-source full corpus remains
mandatory for Gate P1.
