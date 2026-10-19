# Protected directory metadata fetching

Four-node connected preview tests now preserve original/winning subtree bytes,
use one stable case-copy name on all nodes and drain case, needed and publication
queues. They still fail graph reference coverage on all four nodes, including
the read-only receiver variant. These failures remain mandatory acceptance.

Normal logical file/directory apply can change signed child/root inputs without
a native change or watcher event. Request snapshot reconciliation after applied
logical work in the preview, with at most the existing scan/worker pass bounds.
Keep ordinary public behavior gated until connected acceptance succeeds.

Add persistent metadata-source work bound to physical root, exact original root
envelope/source CID, authenticated snapshot hash/exact length and route hint.
It represents authenticated metadata availability, not native ownership or a
new file version. Sources include signed local directories, needed remote
directories, pending cases, retained RO originals and saved directory history.
Admit the original envelope durably before source replacement can drop it.
Startup/reference reconciliation must recover older protected sources too.

Advance the existing graph verifier with one shared 1000-unit metadata budget
and bounded source/envelope pages. Preserve exact source records, validate every
hash/length/signature/canonical parent, flush owned nodes before FULL progress,
and resume after restart. Retain per-source 10-to-600-second backoff and fair
continuation; unavailable sources must not starve independent work. Route hints
are not authority: use authenticated folder peers and verify returned bytes,
including forwarding/disconnect and corrupt-response cases.

Separate this work from reference activation and sweeping. Network awaits must
not hold FolderReferenceGate; only bounded admission/progress/accounting uses
the appropriate FULL/reference transitions. GC remains withheld for incomplete
required graphs, and metadata work cannot authorize native replacement/deletion,
new signatures, or a claim that unseen file payloads were downloaded.

Bound outstanding source-envelope admission and byte memory. Capacity failure
must withhold the complete enclosing source/index update rather than acknowledge
an envelope that cannot be retained. Completed work may release its queue
envelope once ordinary protected-source/reference lifecycle retains it; obsolete
work retires in bounded transactions after its protection window, never by
removing visible copies. Final queue schema/capacity/ownership transitions must
be specified with the implementation before activation.

Required checks include source replacement/restart, atomic admission rollback,
bounded continuation and starvation, incomplete-history/current/RO graphs,
malformed/corrupt metadata, cancellation after fetch/flush, disconnect/fallback,
independent native edits and the four-node gossip coverage baselines. Canonical,
CI/main and the full phase gates remain separate requirements.
