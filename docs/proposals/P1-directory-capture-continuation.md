# Durable directory capture continuation

Implementation design, 2026-10-19, continuing P1.5.3h4b. This extends the bounded
PSDB builder checkpoint already tested. It does not enable scanner publication
or native directory copies by itself.

Use one SQLite draft per canonical parent key, bound to the physical root,
original parent spelling, native directory identity and a digest/count of the
strictly ordered original child envelopes. Hash a domain separator, the parent
spelling and fixed-length child source IDs, plus the final Int64 member count.
Local sequence is excluded. Authenticate live child types, immediate parent
membership, canonical ordering and nested directory root pairs. A changed
root, directory identity, spelling or input manifest starts a new draft;
never reinterpret an old prefix as belonging to changed inputs.

The draft stores accepted count, last canonical child key, bounded PSDB state,
and an optional completed root hash/length. Its progress transaction is FULL
and compares the expected prior prefix and input identity. Flush independent
metadata ownership before committing progress. At most 1000 new members are
captured per advancement. Intermediate full pages and Record/Blob fragments
remain independently owned, never pointers to mutable user files.

An enumerable input can change between its manifest and capture passes. Recheck
the complete ordered manifest while capturing only the bounded new range,
including the saved cursor boundary. Commit no progress if that second digest,
count or cursor differs. Failed/cancelled additions discard the in-memory
builder and resume the preceding durable checkpoint. Capture bytes already
written remain independently accounted or recoverable through capture proofs.
The final root is returned only after all required members, final partial
pages and independent metadata accounting have completed.

Pending and complete-but-unpublished drafts withhold fresh garbage-collection
coverage: a cached prefix alone cannot enumerate every referenced descendant.
Native block deletion waits until the draft is published, superseded or safely
retired. Publication must atomically validate the draft/root/native binding and
install the signed directory row before releasing the draft. Exact child
publications and no incidental directory-mtime feedback remain mandatory.

Abandoned draft metadata is bounded to one row per parent, with last use and
24-hour expiry. Retire at most 1000 rows under the folder/reference gates;
deleting a draft does not delete native bytes. A later request reconstructs
from its authenticated inputs. Active advancement holds the folder gate, so
expiry cannot race its native ownership flush or FULL progress transaction.
Stale cursor/state, SQL failure, root replacement and cancellation preserve
prior durable progress and withhold publication/deletion authority.

Required verification: native restart at each flush/commit/final-root boundary;
exact uninterrupted root equality across 127/128/129 and nested pages; changed
input/parent identity and unstable enumerables; frozen inputs, CAS rollback and
bounded/expired drafts; pending/unpublished sweep barriers; sanitized logs; then
bottom-up scanner convergence and the retained directory case failure baselines.
