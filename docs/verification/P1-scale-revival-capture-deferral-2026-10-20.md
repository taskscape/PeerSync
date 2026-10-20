# Older revival corpus: verified capture deferral

The immutable 100000-file run on
`4c574b9fcbdca1890d98214c7d91d53d823a4100` predates the
[concurrent-capture repair](P1-concurrent-capture-proof-recovery-2026-10-20.md).
After its valid 32/4096-file prerequisites, it reaches initial-convergence
progress with all five high-water marks at 100782 and all needed/apply/case/
publication queues empty. It does not complete reference coverage or return
from initial convergence. No final native/version oracle or successful full
summary is claimed.

A read-only inspection finds exactly one capture marker, on node 1. Native
validation against the original fixture, using frozen binaries from the actual
4c574b9 run, verifies:

- The 156-byte proof's checksum, root and marker native identity are valid.
- The final block is independent, has the expected 246 bytes and matches the
  proof's BLAKE3 hash.
- Its native identity differs from the losing proof's original data identity.
- Its hash/length/root/native-identity tuple exactly matches committed catalog
  ownership and the index's bound root.
- The original temporary and any capture-retirement witness are absent.

The old recovery code takes `data is null` with a non-null final object and
always increments its deferral count. Reference coverage explicitly requires
zero capture deferrals. This is the already-reproduced obsolete losing-proof
state, not a conclusion drawn from elapsed time or a coarse progress counter.
It independently confirms the existing repair with a 246-byte block; the
earlier bf68d97 diagnostic has a separate 48-byte block and retained fixture.

Before stopping anything, the exact old executable, source arguments, PID
58664 and creation time 11:20:29.674918 UTC are preserved and revalidated using
CIM and a bound process handle. Only that benchmark process is terminated at
16:14:40.0706305 UTC, exit -1. Its parent session 28194 exits 1; source-state
records clean 4c574b9 and runner exit -1. No scenario summary exists. The
43200-second deadline and test oracles are unchanged; this is an intentionally
stopped, confirmed nonconvergent historical diagnostic, not a passing corpus.

Post-stop native checks prove the same marker/canonical state. The original
marker bytes are unchanged, and the generated fixture remains in place with
no manual proof, block or catalog repair:
`C:/Users/mzag/AppData/Local/Temp/PeerSync-Scale-ccd3ad78b95540b99109a4604f4cd3e5`.
The separately running repaired-source corpus is untouched.

`artifacts/scale-4c574b9-obsolete-proof-evidence/` preserves the original binary
bundle and hashes/informational versions, process identity/cancellation,
original proof bytes, canonical bytes, catalog/guard observations, native
validation before/after stopping, original recovery/reference source and
partial progress/round/source-state records. A consistent read-only SQLite
backup includes committed WAL data. Byte copies and database values do not
preserve NTFS identities; the original fixture is the native identity evidence.

The diagnostic project `artifacts/scale-4c-proof-inspect/` references those
frozen original DLLs directly. Its locked build passes with zero warnings and
errors. It reads the original fixture and writes only separate evidence;
it does not rebuild the live benchmark or invoke recovery against its data.
All four original binary informational versions identify 4c574b9. Engine
SHA-256 is `B04237943553EB5D01ABC5A2D9DCD09B05A1041592542D1C4179364190335430`;
Benchmarks SHA-256 is `171F35637055651E7E6783CB1C36B4BA307F851D951604F9B335B0F44543EF79`.

The existing two-owner/restart/negative controls and green PR/main acceptance
cover the repair. This earlier-source failure does not change the active
campaign pin or justify another code/CI rerun. Actual immutable repaired-source
100000-file evidence remains mandatory, along with four further verified
campaign dates. P1.2.4b1a, its ownership parents and Gate P1 remain open.
