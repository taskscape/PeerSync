# Directory snapshots for case preservation

Implementation design, 2026-10-19. The approved deterministic case-name policy
also needs authenticated descendants for nonempty directory versions. A signed
directory metadata record alone cannot identify its children. Packet arrival,
forwarding identity and the spelling of an old child's parent are insufficient:
causal parent case renames may retain the child's original signed path.

Add optional `directory_snapshot_hash` (field 13) and exact encoded
`directory_snapshot_length` (field 14) to FileRecordBody. A live directory may
carry a 32-byte BLAKE3 root and its bounded Entries/Branch node length; files
and tombstones cannot carry either. Length allows the existing exact-length
block-fetch contract to request the root without guessing or relaxing validation.
Legacy records have both fields empty/zero; a present root needs both fields.
Missing roots remain valid legacy metadata, never proof of an empty subtree.
Keep original signed envelopes intact. Snapshot links describe the logical
published subtree; ignored/unsupported/withheld entries cannot be invented.

Capture snapshots bottom-up after child publications and before publishing the
directory record. Presence/attributes retain their existing treatment; mere
native child-caused mtime changes remain ignored. A changed authenticated child
manifest is now a content change: advance the directory vector and publish its
new hash without treating installation's incidental native mtime as a user edit.
This supersedes metadata-only directory publication for snapshot-aware rows.
The dependency graph points from parent snapshots to child records, never back
to parent vectors. Verify finite bottom-up convergence rather than assuming it.

Use independently owned, flushed content-addressed blocks through the existing
published-block path. Every snapshot node is at most 1 MiB plus 12 bytes, below
the ordinary block-request limit. FULL publication follows durable block
accounting. References include metadata nodes and every descendant file chunk;
unknown/incomplete graphs prevent sweeping rather than granting deletion.
Pending case sources retain their entire original graph across remote-row
replacement. Reading/fetching metadata must obey bounded memory, cancellation,
per-pass work and persistent continuation; never assume a full graph fits RAM.

## Canonical node encoding

All integer fields are unsigned little-endian. The 12-byte header is ASCII
`PSDS` (4), version 1 (1), kind (1), link count (2), payload length (4).
No trailing bytes, unknown kinds/versions or reserved fields are accepted.
A link is 32 hash bytes followed by the exact encoded child-node length (4).
All hashes cover the complete encoded child node using unkeyed BLAKE3.

| Kind | Header/payload |
| --- | --- |
| 1 Entries | 0–128 links to Record nodes; payload length zero. Zero links is the canonical empty-directory snapshot. |
| 2 Branch | 1–128 links to Entries/Branch nodes; payload length zero. |
| 3 Record | 1–17 links to Blob nodes; payload length is the complete envelope byte length, at most 16 MiB + 68. |
| 4 Blob | Zero links; payload length 1–1,048,576 followed by exactly those bytes. |

Entries and Branch links are strictly increasing by unsigned hash bytes; duplicate
hashes are invalid. Builders consume strictly increasing canonical immediate-child
keys and group Record links by 128, sorting hashes only within each bounded page.
Carry full pages into 128-link Branch buffers at successive levels, then finalize
partial tails bottom-up. Return a sole page directly; no redundant single-child
root is added. Empty input yields the canonical empty Entries page. This keeps
deterministic grouping without a whole-directory hash sort or RAM collection.
Per-pass persistent continuation is still required before production activation.
A Record node preserves ordered envelope fragments; repeated chunk
hashes are allowed. Every fragment except the last contains exactly 1 MiB;
the last has the remaining bytes. Child lengths include their 12-byte headers.

An envelope consists of body length (4), exact signed body bytes (1–16 MiB),
then its original 64-byte signature. Local sequence is deliberately excluded.
Decoding checks lengths before allocation and returns owned immutable buffers.
Byte parsing is not signature verification. Graph consumers check node hash,
length and kind; reconstruct and verify every envelope with folder keys;
validate live supported types, canonical parent path membership and unique
child keys; and validate every nested directory graph. Bounded iterative
traversal detects repeated/invalid structure without recursive stack growth.

Graph validation persists a SQLite job bound to the physical root and original
signed directory source ID. Its pending page/record queue, seen context/hash
pairs, unique canonical child keys and exact metadata/file-chunk references
survive reopening. Page depth is bounded to ten levels; canonical member paths
strictly descend from their verified directory contexts. At most 1000 work
items execute per advancement. Metadata ownership flush precedes the FULL
transaction that records child work and completes its parent atomically.
Large record chunk lists append references in batches of at most 1000 before
that commit. Replays are idempotent for references; duplicate graph structure
or child keys fail. Incomplete jobs never supply reference coverage. Completed
immutable graph references can stream from SQLite without loading the whole
tree. Job retirement must be bounded before the lifecycle task is completed.

## Materialization and acceptance

Admit a directory root/source proof before normal original-path merging.
Persist bounded continuation and exact original member envelopes, bind all
recovery work to root/source/destination/native identities, and build parents
before children. Copy members through verified independent blocks and native
no-overwrite moves. Incoming paths/signatures are never rewritten; RW copies
enter ordinary local signing and RO copies remain local. Complete a source
only after every required member is visibly verified and accounted/published.
Preserve collisions, changed/unknown objects and interrupted work.

Required regressions include empty and nested directory clashes, both arrival
orders, local/remote and file/directory winners, differing child sets and
same-path contents, late index pages, forwarding, causal parent case rename,
gossip, references/expiry, cancellation and interruption at every boundary.
Retain legacy pending proofs without guessing a missing tree. This document
enables protocol foundations first; it does not claim capture, graph references
or directory mutation is implemented.
