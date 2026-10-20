# Directory integration review hardening

PR #38 review identifies four gaps despite passing local acceptance. Retain
regressions before repair and submit their fixes together after local checks.

1. A received directory's original signed template remains immutable while the
   publication queue records observed native attributes separately. If metadata
   or case spelling changed before exact reconstruction, prove original child
   membership with the existing causal rebase and publish one RW metadata edit.
   Canonical original graph ancestry remains binding; never rewrite its envelope
   or acquire signing authority on RO. Preserve parent mtime against incidental
   child writes. Existing queue attributes can carry the observed value.
2. A valid legacy directory without a snapshot is unavailable preservation work,
   never an empty-tree proof. Persist per-source retry before graph/native work,
   retain the original barrier, and let independent pulls/metadata recovery run.
   Malformed metadata responses also defer that source with conservative offered
   budget accounting rather than abort the whole pass.
3. Retained descendant work must be reconsidered when a signed live file stops
   blocking it. Local tombstoning and incoming file deletion request needed
   repair; publication over a former file tombstone also rebuilds descendants.
   Test separated scans and absent-parent rehydration, preserving unrelated
   tombstone, source and recovery guards. No new child envelope is required.
   Scheduling work while its native parent is absent does not authorize implicit
   directory creation: ordinary parent-version/native creation and retry rules
   remain binding.
4. Public direct/session receives bind and validate the physical root before
   metadata admission. Keep a native root lease through admission, serialize
   first binding with folder/reference gates, reject foreign roots and unbound
   recovery state, and retain the storage root guard. Freeze page/source input
   before awaiting initialization; session clock/disconnect generation checks
   still run at admission and commit. Root binding does not replace the initial
   scan or set the coordinator initialized flag.

Required checks include hidden/case edits before reconstruction, legacy and
corrupt snapshot peers alongside unrelated valid data, separated local deletion
and recreation, incoming deletion, direct/session/connected receipt before the
first pass, concurrent initialization, root replacement/pinning and pending
recovery rejection. Run focused/full/canonical checks before one updated CI head.
