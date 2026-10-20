# Directory parent clock closure across proven roots

The narrow same-body clock repair passes focused checks and hierarchy seed
1648781653. Replay 1044729930 still fails: multiple parent records share exact
metadata except directory snapshot hash, and repeatedly merge clocks into the
older root. Native child records have already converged. In the retained
six-node fixture, both old root graphs' direct child vectors are included by
the current child vector. Do not simply merge those parent clocks: prove every
root against current children before claiming its causal history.

Extend automatic parent-clock closure only to candidates whose entire body,
excluding Vv and DirectorySnapshotHash/Length, equals the current original
parent. Keep spelling, kind, author, times, attributes and unknown fields
strict. Include the original parent itself. Group eligible candidates by
snapshot hash/length, preserving an authenticated representative envelope and
routing hint for each root. Vector-only forwarding changes do not change the
root set and must not reset completed tree proof. A different root must never
pass the existing no-proof negative controls.

Persist a bounded root-set proof under schema 35, bound to the folder native
root, directory native identity, original metadata class, authenticated current
child input hash and source root set. Store exact representative sources and
per-root ordinal progress/completion. Protect those envelopes/graphs while
proof is live even if the current remote row advances; integrate holds with
metadata recovery, reference coverage and graph retirement. Use existing
capacity/backpressure conventions, bounded source discovery/byte pages, and
FULL transactions. Never treat missing metadata or capacity exhaustion as an
empty/equivalent subtree.

Generalize the existing causal child proof: each original graph direct child
must be the exact signed local child or be dominated by a signed current child
or tombstone, with matching canonical path/native readiness rules. Equal-vector
different-body, concurrent, unsigned, unready and unknown descendants cannot
release a root. A dominating direct kind/deletion may skip that old subtree
under the existing rule. Account all graph work against the shared 1000-unit
publication budget and persist prefixes, so large roots eventually progress.
No full unbounded root walk or small-root-only shortcut may close this task.

The global local-table epoch may change for unrelated parent clock merges.
Revalidate the full authenticated child input hash before rebinding a witness;
when that hash and native/source bindings remain identical, preserve verified
progress rather than starving large proofs. A changed child input resets
relevant proof. Restaging a vector-only version cannot silently lose the
protected representative or reuse proof for changed tree/native metadata.

After all required roots are proven, merge current authenticated parent clocks
whose roots are in the proven set, increment the local counter once, and sign
the captured current tree using observed metadata. Under FolderReferenceGate,
revalidate current child witness, native/root/local sequence and the current
eligible root set before FULL commit. A new unproven root retries. Vector-only
clock growth can reuse tree proof, but the exact signed body still must match
the final current floor. Release proof/holds atomically with queue publication;
cancellation and SQL failure retain recoverable state. No network awaits hold
the reference gate.

Add failing controls before implementation for two distinct roots whose
original children are both dominated, partial proof/restart, more than one
work page, missing/corrupt graphs, concurrent/equal-different children,
root-set and input changes, source disappearance/retirement, metadata-only
clock growth, final receipt races and SQL rollback. Retain the existing
same-body and different-metadata/root negative controls. Rerun both saved
hierarchy failures, route seed 1878823561, a measured expanded batch and the
canonical suite before one CI submission. Do not tick P1 or multi-node gates
from these local checkpoints.
