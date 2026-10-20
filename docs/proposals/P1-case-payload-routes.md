# Case payload routes after directory versions advance

Hierarchical simulation seed 1878823561 leaves two of seven nodes stuck after
24 healed rounds: a signed old member's payload remains on another connected
peer, while no peer advertises the exact old directory envelope. Treat current
advertisements as hints rather than a prerequisite for content-addressed reads.

Use the same bounded route attempt for file case copies and directory metadata
and member reconstruction. Per pending source, schema 34 stores the last
successful route and an ordinal known-device cursor. A lazy FULL transaction
verifies the pending original/root, returns at most six known devices, and
advances/wraps its cursor before network work. Persist only one page per entire
source attempt, including multiple metadata nodes/chunks. Two preferred hints
(the remembered route or forwarding device, and original author) precede that
page. Deduplicate and exclude self. Restarts and repeated unavailable peers must
not starve later devices. Successful hints are durable; completion removes route
state. Retain source IDs, original envelopes and graph/native/recovery guards.

Authenticated connection and signed length/hash validation authorize bytes;
no current path/envelope advertisement is necessary. Freeze transport bytes,
validate length and BLAKE3 before accepting a route, and continue after corrupt
responses. Each candidate gets one second plus its requested byte count at a
256 KiB/second transfer allowance (at most 33 seconds for an 8 MiB block).
Each source attempt gets 300 seconds of wall-clock network opportunity, enough
for all eight worst-case candidate slots plus local scheduling overhead.
A fixed one-second payload limit would reject legitimate slower links. Caller cancellation propagates. Unavailable,
corrupt and timed-out routes remain pending under existing durable backoff.
No network await holds FolderReferenceGate. Emit redacted session/stage/count,
error-type and HRESULT diagnostics, never paths, content, envelopes or hashes.

Prove the absent-advertiser failure before implementation. Check a later-page
payload route, corrupt and stalled routes, cancellation, root/source rejection,
restart cursor continuation and SQL rollback. Rerun the retained hierarchical
seed and canonical suite before one batched PR #38 push. Hierarchy properties
remain WIP until their own acceptance. Do not add unmeasured CI runs or tick
unmerged tasks.
