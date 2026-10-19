# P1 review repairs — 2026-10-19

The review findings were checked against the current source and reproduced
before repair. The new oversized-settings regression failed, and the Engine
suite exposed 16 failures in role transitions, tombstone recovery, RO causal
selection, naming, nested unknown fields, attribute validation, diagnostics
and retry classification. A never-published RO deletion control still passed.

| Finding | Repair and regression evidence |
| --- | --- |
| PR #11 r4310047709: settings writer exceeds reader limit | Bound ToJson output to the same 65,536 characters. A 256-entry valid DNS list now fails before persistence; a fitting list round-trips. Failed settings updates leave readable state, pause state and audit count unchanged. |
| PR #11 r4310047731/r4310047745: missing path indexes | Already implemented by additive migrations 3 and 2 in PRs #14/#12, with query-plan and migration-backup regressions. Do not rewrite the already-shipped migration 1. |
| PR #12 r4310082458: unsigned RO rows stay unpublished after upgrade | RW scans require a signed record before using the unchanged fast path, so valid tracked files/directories publish once on upgrade. |
| PR #12 r4310082481: pending RO deletion never signs | Sign a causal tombstone when write capability returns and prior published knowledge exists, retaining observed deletion time. Previously published unsigned edits can also become signed deletions immediately when a writable scan first observes removal. Never-published/unsyncable removals remain unpublished. |
| PR #12 r4310082503: suppressed tombstone stays hidden | For successfully walked nonignored missing paths, clear transient flags and allocate a fresh sequence while preserving the original signed body and edit vector. Tests cover ignoring an existing parent and a recreated, locked, then removed file. |
| PR #13 r4310074700: SQLite transient failure stops scheduler | Busy/locked/I/O/full/open errors enter 10–600 s backoff and release the scan budget. Other folders keep running. Corruption/not-a-database remains fatal and releases the permit. Tests inject these exceptions through the existing scanner clock seam, not an actual full disk. Debug events retain a SQLite-specific code and extended result number. |
| PR #14 r4310083680: RO cached vector defeats signed descendant | Select candidates using their signed vectors; validate cached logical knowledge and merge it only afterward. The A1/B1/A2 receive-order regression chooses the signed causal descendant even with its older restored mtime. |
| PR #14 r4310083725: truncated Windows reserved stem | Use a fitting safe fallback stem if truncation produces CON/COM1/LPT1. Bounds and no-overwrite collision reservation remain mandatory. |
| PR #14 r4310083760: nested vector unknown fields lost | Reuse entries from the cloned winning body, updating counters and adding only genuinely new entries. A nested unknown Protobuf field survives signing without mutating the original. |
| PR #14 r4310083791: logging changes receive outcome | Engine paths use best-effort TryWrite. Committed receives still succeed and original verification/cancellation errors survive unavailable logs. Strict Write remains for validation tools; dropped events are counted. Core tests cover blocked/disposed output, formatting rejection, redaction and later successful writes. |
| PR #14 r4310083812: unsupported attributes remain perpetually needed | Reject bits outside read-only/hidden before storing a verified version. A signed Archive-bit record is covered by malformed-metadata tests. |

Current focused suites pass 126 Core, 124 Engine and 37 Storage cases. Scoped
Core/Engine/Storage and their test-project compiler/analyzer checks are clean,
without workspace load warnings or truncation. The canonical runner passed
306 cases, native probes, diagnostics, watcher recovery and publishing in
110.86 seconds, exit 0, with fuzz explicitly skipped. Final SQLite diagnostic
detail and the extended-result regression then passed all 124 Engine cases.
There are 307 current cases including unchanged Crypto/Protocol tests; do not
confuse that total with the earlier 306-case canonical execution.

Evidence: `artifacts/p1-settings-before.log`,
`artifacts/p1-engine-review-before.log`,
`artifacts/p1-core-review-after.log`, `artifacts/p1-engine-review-after.log`,
`artifacts/p1-storage-review-after.log`, `artifacts/p1-review-verification.log`
and matching TRX directories. Required CI and merge remain pending. Earlier
foundations may merge as WIP, but affected tasks cannot be ticked until these
repairs also merge with green checks. No production sync deployment or phase
gate completion is claimed.
