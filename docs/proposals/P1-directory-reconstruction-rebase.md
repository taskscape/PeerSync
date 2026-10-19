# Causal rebase of received directory snapshots

The offline-owner forwarding baseline preserves both subtree payloads and
drains case/needed work, but the RW receiver retains two reconstruction queues.
Their signed roots contain child envelopes superseded during conflict
resolution. Exact reconstruction alone can never release these roots.

Keep exact reconstruction as the first choice. For RW only, allow a different
captured root after proving every original direct child is represented by an
exact signed local envelope or a causally dominating signed local version
(including a dominating tombstone). Concurrent, older, absent, unsigned or
unverified live file rows cannot satisfy this proof. A dominating directory or
file replacement represents its older descendants; do not demand obsolete
descendants beneath a dominating tombstone/file. Additional current children
are covered by the ordinary complete scanner inventory and capture guards.
Nested pending publications still complete bottom-up. RO never rebases/signs.

Use the complete authenticated original graph, reading its members in indexed
pages and authenticating direct child envelopes from owned metadata. Persist
schema-33 proof on the queued publication: source graph, current local mutation
epoch, capture input hash, verified graph cursor and completion bit. Bind all
updates to queue version/root/native/template and capture identity. Share the
existing 1000-unit publication budget; descendants consume units even when no
direct-child check is required. Advance only the exact next graph prefix in a
FULL transaction. A local mutation invalidates the prefix, including across
restart. Queue restaging clears it. Missing metadata/children remain pending.

After proof completion, increment this RW device's directory vector once,
preserve the signed parent mtime, sign the captured root and retain history.
The existing guarded FULL publication commit checks complete non-retiring graph,
proof epoch/input/queue binding and normal native/capture guards before replacing
the received row. Queue/draft/proof release is atomic. No intermediate local
root or new native mutation is introduced. A crash before final commit resumes
the proof; changed input restarts it. Original source retention and GC barriers
remain unchanged.

Acceptance includes the retained offline-owner RW/RO baseline, late missing
children, concurrent/older/equal-but-different envelopes, local member edits,
tombstones/type transitions, bounded continuation/restart/epoch invalidation,
forged/missing graph metadata and final commit failure. Activation and CI/main
remain separate gates.
