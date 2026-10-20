# Metadata-only tombstone progress beneath staged directory work

Hierarchy seed 1583987140 reproducibly stops with an ordinary parent publication
waiting on a reconstructed child, whose graph proof needs a newer child tombstone.
The local child is already a tombstone and its native path is absent. The parent
barrier prevents that metadata update and creates a dependency cycle.

Permit only a scheduled regular-file tombstone update beneath that barrier when
selection still requires application, its authenticated desired version is deleted,
the known local regular file is already deleted and published without flags, the
sender range is complete, and no case-preservation barrier exists. Keep operation
and reference gates, bound-root validation, exact local-sequence/intent commit and
needed recomputation. Prove native absence with guarded parent/leaf handles; probe
again after the preparation checkpoint. Any native occupant, live/unknown local
row, wrong type, case work or incomplete sender range keeps the existing barrier.
This path never removes or creates native content, discards a directory queue,
changes a snapshot proof, or merges a parent clock without that proof. Interrupted
metadata intents may be safely abandoned by existing recovery and retried normally.

Add the retained hierarchy regression, focused positive/negative native and range
controls, interruption/restart and commit rollback checks. Preserve failure evidence;
require canonical verification and CI/main before acceptance. Existing 100k run
remains evidence for its original immutable source, not a new production build.
