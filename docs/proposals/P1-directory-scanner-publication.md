# Bottom-up scanner publication

Implementation design, 2026-10-19, continuing P1.5.3h4b. Existing directory
capture/reference/publication primitives pass focused tests; this describes
their scanner/coordinator integration before enabling it.

Retain the previous signed directory row until a replacement snapshot is ready.
Never advertise a fresh legacy/no-root directory as an intermediate step. Stage
the new native metadata, own vector and expected old local sequence privately;
publish child file versions first, then directory versions from deepest paths
upward. A parent waits for pending supported child-directory publications.
Ignored/unsupported entries remain outside the logical published tree; retain
the last independently captured signed version of a temporarily withheld child.
Never infer an empty tree from an absent legacy nested root or incomplete walk.

Directory child enumeration needs an indexed parent key and bounded row/byte
batches. SQL UTF-8 text order differs from C# ordinal UTF-16 order for some
non-BMP names. Store a UTF-16BE canonical sort key so pagination and strict
capture ordering agree for every supported Unicode filename. Maintain both
keys atomically with local rows and backfill legacy rows before exposing the
index. Validate Unicode ordering and byte-budget continuation explicitly.

A private pending-publication queue retains native directory identity, signed
metadata template and expected local sequence across restart. It does not
replace the old local row. Queue dependency and apply barriers prevent ordinary
original-path mutation from consuming an unpublished local directory change.
GC coverage stays withheld while publication/capture is pending. Complete
publication validates native/root/template state, preserves old versions, and
FULL-commits the signed root/local row plus queue/draft release atomically.

Advancing a large draft must not repeat a full native tree scan or authenticate
its entire unchanged input for each 1000-member batch. A trusted local-index
capture mode uses an immutable folder-operation snapshot and the local index
high-water as an input-change witness. When unchanged, query strictly after the
saved indexed child key. When changed, revalidate the full ordered manifest;
retain progress if its identity is unchanged, otherwise restart the draft.
Storage compares the expected witness before committing progress. Arbitrary
enumerables retain the existing two-pass manifest verification path. The
coordinator advances drafts between complete scans and schedules bounded
continuation/retry work without busy loops or starving other folders.

A changed authenticated child root advances the parent vector once. Preserve
its prior signed mtime when only membership changed; native child-caused mtime
must not create edit feedback. Metadata/case changes keep their existing
semantics. Parent dependencies never feed back into child envelopes. Require
finite multi-peer/nested convergence, late-page, interruption and reference
regressions before declaring the scanner slice complete or enabling subtree
copy mutations. Full million-file performance remains a separate phase gate.
