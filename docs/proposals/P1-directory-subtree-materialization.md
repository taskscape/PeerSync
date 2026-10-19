# Guarded subtree materialization

P1.5.3h4c implementation design, 2026-10-19. The five connected subtree
preservation failures remain the acceptance baselines. Scanner activation
stays internal until incoming/subtree convergence passes.

Start with a bounded authenticated projection of a complete, non-retiring
source graph. Bind the original signed directory envelope/source ID and
physical root before reading members. Page the persistent canonical member
map by exact key, re-open each referenced content-addressed original envelope,
and recheck its canonical key and original parent. Map relative segments below
the source root into the stable case-conflict root, retaining every member's
signed body/signature unchanged. Parent spelling in a previously signed child
may differ after a causal rename. Canonical ancestry defines membership;
relative segment spelling defines the projected leaf. Reject unrepresentable
destination paths. This projection proves metadata only and authorizes no
native mutation, overwrite, publication or byte readiness.

Bound a projection pass to 1000 members and an 8 MiB envelope budget, permitting
one already bounded oversized envelope. Read a record-node header before its
blob fragments so the next large envelope can be deferred without loading it.
Flush any fetched metadata before returning a resumable cursor. Root/path,
record signature/hash/length, incomplete/retiring graph, cancellation and
malformed member failures must produce no usable partial result.

The subsequent native slice must persist source/root/destination-root native
identity and per-member identity/intent state before mutations. Create parents
before children, verify original file chunks, install without overwrite, and
recover owned temporaries before scanning. A root remains pending until every
authenticated member is visible/accounted and normal RW publication or RO
unsigned local-copy proof completes. Retain all source graph references while
pending. Foreign edits, occupied destinations, reparse points and unknown
ownership preserve bytes and defer/reselect a safe destination. Fetch fallback
uses another current advertiser of the exact original root envelope, never
rewritten child paths or forwarding identity as subtree ownership.

Required checks include nested/empty graphs, causal parent spelling, exact
original envelopes, bounded continuation/byte budget, damaged or replaced
metadata, missing/retiring/root-mismatched graphs and cancellation. Then add
real native member recovery/publication and connected arrival-order/gossip/
late-page/collision/source-replacement tests before activating the scanner.

## Native root journal

Use a distinct local recovery intent kind 8 for the case-copy root. Bind the
physical root, original authenticated directory envelope/source ID, stable
collision slot, sibling GUID temporary directory/native ID and preparation
state. This format remains within the existing 24 MiB intent bound, without
duplicating member envelopes. Require a complete non-retiring source graph
before reserving or recovering native root mutation. Legacy missing roots stay
pending. Bound new graph work to a shared 1000 units per preparation pass;
successful metadata continuation remains immediately schedulable.

Create the owned temporary exclusively and retain its native handle. FULL
reserve the existing source/destination case ledger and kind-8 intent before
preparation or rename. Reject nonempty reused temporaries. Prepare inherited
metadata and source mtime, FULL-mark prepared, then rename by held handle
without overwrite. FULL-account the root native identity and release the
intent together. No source completion occurs here, even for an empty graph;
member accounting and RW/RO publication proof are still mandatory.

Recovery accepts a prepared installed destination only by its persisted native
identity. It accounts that identity without erasing post-install user metadata.
A valid empty prepared temporary can finish its guarded rename; unprepared,
changed, occupied or unclassifiable cases preserve all objects, park owned
temporary metadata (protected when changed/nonempty), and release the intent
for another safe attempt. A mutation followed by a database failure retains
the intent and requires recovery before scanning. Diagnostic stages/correlation
identify graph, reservation, preparation, installation, accounting and replay
without logging source envelopes or paths. Member intents/journals follow this
root foundation and must bind the accounted destination-root native identity.

## Member ledger and intent

Schema 28 stores one installation cursor per original source ID/destination-root
native ID and one row per original canonical member. Bind the original graph
record hash/length, member source ID, type and projected destination; retain no
rewritten envelope. FULL-reserve the next exact graph member and a local kind-9
intent together. The intent binds physical/source/root/member identities,
metadata link, destination, sibling GUID temporary/native ID and preparation
state. Reauthenticate the original envelope from locally retained graph metadata
during recovery; do not duplicate two potentially 16 MiB signed bodies into an
intent. FULL native identity accounting, cursor advancement and intent removal
are atomic. Traversal exhaustion means installation coverage, never source
completion or publication proof.

Pin the accounted destination root and every intermediate parent by their
ledger native IDs, opening one guarded component at a time. Create directory
members before their children. File assembly uses the original authenticated
chunk list and advertisers of the exact original root. Prepared files require
independent identity/size/whole-hash verification; prepared directories require
identity/metadata/emptiness. Installation is a no-overwrite held-handle move.
Recovery preserves edited or unclassifiable objects. Nonempty unprepared file
staging without an authenticated content proof stays protected; directory
staging with unknown children also stays protected.

A foreign member occupant, edited installed member or changed ancestor causes
the whole copy attempt to select another root slot after safely releasing its
intent. Retain the earlier tree and its old identity-bound ledger; never move,
delete or overwrite its members to make it fit. New root identities start new
cursors. Bound admission/advancement to member pages, retain immediate scheduling
while making progress, and use persistent backoff for unavailable data. Source
barriers and original graph references remain until all members and RW/RO
publication are proven. Incoming mutation of a pending copy namespace and
intermediate directory advertisement must also be guarded before activation.
