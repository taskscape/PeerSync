# P5.3 bounded scanner staging design

Status: implementation design, not a completed scale gate. The measured
million-file first walk uses about 1.28 GB at the commit checkpoint, and an
immediate unchanged walk reaches about 2.55 GB. Its 135-138 MB idle result
does not prove a 300 MB peak. P5.3 therefore still requires a bounded scanner
path and another committed-source million-file run.

## Current allocation boundary

`Scanner.ScanCoreAsync` materializes every previous `LocalFileRow`, every
native `WalkEntry`, and every newly signed update before history retention and
database upsert. `StageDirectoriesAsync` also groups all observed child keys.
The native enumerator itself is incremental; these long-lived maps and rows
dominate the peak. Collecting after the scan cannot lower its live peak.

## Implementation sequence

1. Introduce an owned, private scan workspace keyed by a random scan ID and
   bound to the native root identity. Its SQLite rows hold the canonical path,
   parent, case spelling, native file ID, attributes, size and times, plus the
   scanner's uncertainty and ignore state. Use bounded writes while walking;
   retain only directory paths and uncertain prefixes in memory. A workspace
   from an interrupted run is never a published index or deletion proof.
2. Process ordered pages of at most 1,000 observations. Join each page to
   the existing local index, fetching a full signed prior row only when an
   edit, rename, deletion, history transition or case decision needs it.
   Keep guarded native hashing and exact signed version creation. Stage
   resulting rows in the workspace rather than a million-row
   `ConcurrentBag`. Compare absent prior paths with the completed observation
   table before creating tombstones; protected, ignored and failed subtrees
   must retain their old rows.
3. Perform conflict-copy matching and history retention from staged rows in
   bounded pages, preserving the existing file-ID/hash match and recovery
   order. Commit local updates in ordered batches while the folder operation
   gate is held. Persist a scan-commit marker so a crash between batches
   forces a complete recovery rescan before peer advertisement or deletion
   decisions. Keep the existing root-identity rechecks and block-capture
   durability barriers.
4. Stage directory snapshots by streaming direct-child keys and observed
   directory rows from the completed workspace. Preserve the exact native
   fingerprint, signed-leaf cache guards, pending-child and received-root
   reconstruction rules. Do not relax the current local-only cache to cover
   remote or needed work as a memory shortcut.
5. Remove only the workspace owned by the completed scan, after durable
   commit and publication staging. On startup, detect abandoned workspaces,
   invalidate partial scan state, and rescan under the same safety gates.

## Acceptance evidence

- Cancellation and process interruption at walk, hash, block flush, history,
  batch commit and directory staging retain old versions and converge after
  restart. Case collisions, unreadable/reparse paths, renamed edits, ignored
  descendants, received snapshot reconstruction and conflict copies retain
  their existing semantics.
- The focused Engine suite and all retained/generated Simulation cohorts pass.
  A separate fresh-peer control checks every native byte and path before the
  million-file gate is repeated.
- A committed-source million-file first index stays below 20 minutes on SSD;
  sampled peak working set and private bytes remain below 300 MB (300,000,000
  bytes) throughout
  the first and unchanged walks, and the 30-second idle CPU remains below 1%.
  The fresh-peer million-file requirement is verified independently. Record
  source revision, binary hashes, scanner stages, peak and idle samples, and
  retained databases/logs.

This design does not alter P5.3's threshold or substitute the current
idle-memory measurement for a peak-memory result.
