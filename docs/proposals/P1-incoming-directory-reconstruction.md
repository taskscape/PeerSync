# Incoming directory reconstruction

Extend the existing durable private publication queue with a reconstruction-only
flag (schema 31). Existing rows remain ordinary publication. Scanner preview
stages the exact received signed template when its native/source-bound cache is
missing, instead of inventing an empty or partial root. Preserve its signed
metadata and current row; retain bounded capture/epoch/key/native proof.

Reconstruction rows do not block ordinary incoming apply selection or recovery:
children and a newer root must be able to arrive. Ordinary publication rows keep
their existing ancestor/descendant mutation barriers. Both kinds retain GC and
parent publication holds and count against pending work at final case completion.
CAS includes the mode. A reconstruction transaction cannot write a new signed
row; the retained current envelope must equal its original template. The engine
also checks the exact captured root hash/length and native metadata before its
FULL cache/queue/draft release.

The scanner reconciles obsolete sequence/native bindings, preserves existing
case-copy holds and refreshes incoming native child-key inventories. Touching a
stable reconstruction with a changed local input epoch clears its backoff, so
late child arrival or repaired bytes can resume promptly. A mismatch remains
visible pending work; it never becomes deletion evidence. Parent work waits for
child reconstruction. Read-only copies never manufacture a new root.

This step enables reconstruction in the existing staging preview. Production
scanner/coordinator activation remains gated on convergence, late pages, gossip,
bounded continuation, unchanged-mtime and RW/RO tests. Native edits that prevent
exact received-root reconstruction remain deferred; a later conflict/rebase
protocol must prove their original member obligations before advertising a new
parent snapshot.

The connected preview uses internal constructor overloads, preserving public
production behavior. Scanner stages only; one bounded worker pass per coordinator
pass runs after case preparation/scanning and before final preservation release.
Recompute changed advertised directory paths and include queue retry deadlines
in normal coordinator scheduling. History requests a later scan through the
existing wake path. Retain all connected baseline assertions while enabling
preview on their nodes; investigate failures before production activation.
