# Durable bottom-up publication queue

Implementation design, 2026-10-19, for the next scanner/coordinator slice of
P1.5.3h4b. Capture, indexed continuation, reference coverage and atomic root
publication primitives are implemented locally; no scanner activation yet.

Persist one pending template per canonical directory. Bind a unique queue
version, physical root, native directory identity, original path, full native
attributes, expected old local sequence and exact signed metadata template.
The private template has no snapshot root; it is never advertised. Preserve
the prior signed local row while work is pending. Preserve template time/vector
across identical restaging; a changed native identity/metadata/input inventory
replaces the queue version rather than reusing stale completion authority.

The scanner commits file versions/tombstones first and stages directory
templates from complete supported native observations. Retain expected direct
child-key count/digest. New unsigned supported children, incomplete/colliding
walks or pending child-directory templates defer parent publication. Do not
mistake missing nested publications for an empty subtree. Indexed input-key
checks can reuse their mutation epoch; authenticated body capture uses the
existing checked-epoch mode.

Process pending directories deepest first with bounded continuation between
full native scans. Do not repeat a million-file walk for each 1000-member
capture batch. Capture/progress and retry remain separate: successful bounded
progress can run again; I/O/unstable input failures use bounded persistent
backoff, including clock rollback, while independent directories can progress.
Publish children before their parents. Retain old versions through the existing
history path before replacing a local row.

When the captured root and native metadata match the current authenticated
directory row, release the draft without changing its vector/sequence. Otherwise
combine the stable template with the completed root and publish one new version.
Preserve prior signed mtime for content-only changes. A FULL transaction compares
queue version, draft/input/native/root binding and old local sequence, then
installs or retains the local row, updates a compact published-input cache and
releases queue/draft atomically. The cache binds native identity, exact current
record source ID, input manifest/count and post-commit mutation epoch. It is
never ownership or deletion authority by itself.

Pending publication/capture work withholds fresh GC coverage. Ordinary incoming
original-path mutation must wait for relevant pending directory work, including
ancestor/descendant namespace transitions. Scanning and the owned publication
worker can advance their own work. After completion, recompute/wake affected
needed paths; retain fairness and bounded work. Expired private queue/cache
metadata is retired under gates in bounded batches without deleting native
bytes or silently authorizing incomplete coverage.

Readonly local copies never acquire signing authority. A root reconstructed
from received original envelopes can be cached only against an identical
authenticated current root; releasing that reconstruction must not manufacture
a new signed row. Missing original metadata remains pending/fetchable, not an
empty-tree proof. Preserve complete original case-source graphs while subtree
materialization and its visible/publication proof remain outstanding.

Required integration checks: unchanged/child-only/attribute/case scans and
finite nested convergence; new/withheld/unsigned/ignored children; input changes
and interrupted queue/capture/publication; file-directory transitions; late
index pages and readonly behavior; pending namespace/GC barriers, retry/fairness,
cached no-op scans and bounded advancement without repeated complete walks.
The five retained directory case baselines remain mandatory in P1.5.3h4c.
