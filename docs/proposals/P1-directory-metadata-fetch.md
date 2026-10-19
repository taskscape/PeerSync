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
directories, pending cases and retained RO directory history in the case ledger.
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

Schema 32 stores a maximum of 4096 outstanding envelopes per bound folder.
This admits one 1000-record index page with old local, selected original/merged
roots and the existing at-most-1000 case sources, while retaining whole-update
backpressure when older work occupies capacity. Needed rewrite admits local,
selected original/desired and case directory sources atomically. Source IDs bind
exact original body/signature; snapshot/path/hint fields are immutable bindings
apart from a successful routing-hint update. Legacy absent snapshot pairs do not
enter this queue. References and bounded saved-history discovery reconcile older
protected sources. Existing `_history-` markers are strictly file-only; recover
retained directory originals from the completed RO case ledger. Capacity leaves
the enclosing index cursor/rows unchanged.

Queue ordering uses a durable monotonic run ordinal, independent of frozen or
rolled-back UTC. Read at most 32 sources/8 MiB per worker pass (one oversized
legal envelope may progress), share 1000 graph units, and charge empty/completed
graphs a scheduling unit. Persist 10-to-600-second retries and route failover.
The graph verifier still owns flush-before-progress and canonical/signature
checks. Completed graphs release their queue entries atomically after root/source
proof; reference construction protects their graph references before release.

Requested-source retention is separate from attempt ordering. Metadata-only
queue entries expire in bounded batches after 24 hours without a new protected
source request. Expiry never removes signed current/needed/case/history owners,
native data or graph references. A still-required incomplete graph remains a GC
barrier and its owner re-admits metadata work during reconciliation. New history
roots are retained through ordinary pre-replacement source admission;
root-bound, source-ID/byte-limited case-ledger pages recover retained originals.
The public cache-fetch path may run without enabling snapshot publication.
Keep at most two preferred hints plus six known-device routes per attempt.
Persist a source-specific device-ID cursor with attempt accounting and wrap at
the end of the rooted device catalog. UTC backoff and global fair run ordering
must not repeatedly select the same first route page or exclude later forwarders.
Give each route a one-second linked deadline within the ten-second source
attempt. A stalled preferred hint must leave time for the other bounded routes;
route timeout advances failover, while caller/source cancellation stops the
attempt. Transport fetches must honor their cancellation token.

Required checks include source replacement/restart, atomic admission rollback,
bounded continuation and starvation, incomplete-history/current/RO graphs,
malformed/corrupt metadata, cancellation after fetch/flush, disconnect/fallback,
independent native edits and the four-node gossip coverage baselines. Canonical,
CI/main and the full phase gates remain separate requirements.
