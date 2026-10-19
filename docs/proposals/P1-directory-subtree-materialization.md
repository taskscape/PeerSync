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
