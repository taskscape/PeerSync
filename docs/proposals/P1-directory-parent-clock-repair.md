# Directory publication clock closure

Hierarchy seed 1648781653 retains identical native trees and zero ordinary
needed rows, yet a preserved directory republishes every healed round and
keeps its publication queues live. A captured original parent snapshot still
contains an older signed child envelope. Automatic reconstruction publishes
the current child graph from an older staged parent clock; concurrent peers
can merge that new clock back into the old parent's otherwise identical
metadata and select the stale root again. Native equality alone cannot close
this failure. Preserve the fixture and all quiescence assertions.

Before signing an automatic directory publication, close its parent clock over
currently known authenticated envelopes with exactly the same non-vector body
as the current original parent: same portable spelling, author, times,
attributes, unknown body fields and snapshot hash/length. Only Vv entries may
differ. Require the original to have a signed directory snapshot. These
records describe the very same original tree/metadata already covered by the
existing capture/rebase authority; they add clock knowledge, not another root.
Do not absorb clocks from a different subtree, case spelling, metadata or kind.
Merge that floor and increment the local clock once. Apply this to both owned
restaging and received-root reconstruction. Keep the observed native metadata
and original graph/child proof unchanged.

A shared deterministic template builder validates the expected current local
sequence and derives this floor for construction. Recompute the exact expected
body while holding FolderReferenceGate before FULL queue commit. A late
receipt that changes the applicable floor must fail/retry rather than commit
an obsolete template. No network await occurs under that gate. Do not modify
original signed envelopes, weaken input/native/root checks, or infer empty
subtrees. No new durable schema is needed: remotes are already durable and
publication intents remain tied to their original local sequence.

First add focused failing controls for owned and reconstructed roots with a
newer same-body parent clock admitted after queue staging. Add negative
controls for different roots/metadata, plus a late-receipt final-commit race.
Rerun the saved seven-node failure, loss/reference/queue oracles, full local
canonical checks and one batched PR #38 CI run. Keep all boxes open pending
merged green evidence; the 1000/five-day/100000-file gates remain independent.
