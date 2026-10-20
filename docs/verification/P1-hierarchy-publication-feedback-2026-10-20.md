# Hierarchy publication feedback: local repair checkpoint

Actual main [run 39390329140](https://github.com/taskscape/PeerSync/actions/runs/39390329140)
fails hierarchy seed 1608819303 on main a4d9529f. The other ten Simulation
methods pass. All four other CI jobs pass, with 1545 distributed unit results;
fuzz completes 164704 seeds in 600.0033767 seconds with zero crashes. This
does not qualify the failed main run or permit campaign activation.

The identical production source reproduces locally: one failed generated
method, exit 2, 56.839 seconds. Its five-node/sixteen-operation trace has equal
native trees and empty needed queues, but high-waters grow every healed round.
The failure is preserved rather than omitted or given a larger round bound.
Evidence: `artifacts/hierarchy-1608819303-before.log` and the matching TRX.

Read-only SQLite and signed-envelope inspection of the hosted generated fixture
finds equal topic path/mtime/attributes but differing ChangedTicks provenance
and snapshot roots. Parent discovery excludes the other provenance class,
leaving alternating publications unable to close both clocks. Debug journals
show recurring parent-clock proof and publication events. The extraction
checks bounded size, path containment and symlink entries; inspectors use
read-only database connections. Evidence remains under
`artifacts/hierarchy-1608819303-hosted-*`.

The focused ChangedTicks test reproduces the causal omission before repair:
the local counter remains at 1 rather than closing the covered remote counter
at 101. Initial broader normalization also excluded ModifiedBy; full canonical
verification caught an RW four-node case-copy regression (1001/1002 Engine
cases passed). Its exact isolated test passes both cases on the actual main
base but fails RW on that prototype. Retaining ModifiedBy fixes both cases.
This is why the final class excludes only ChangedTicks under full root/child
proof and preserves author boundaries as well as path/mtime/attributes.

All 54 selected Parent tests now pass, including a strict different-author
control and three ChangedTicks incomplete/retiring/altered-graph controls.
Existing late-receipt, metadata-difference, bounded-progress and native/content
guards remain in place. The class digest changes to v2 so old persisted
witnesses cannot be reused. The direct exact-body fallback is unchanged.

The final timestamp-only repair's exact full seed passes in eleven rounds,
29.8563356 scenario seconds:
five nodes, sixteen operations, fourteen writes, four renames, two deletions,
all fourteen published versions retained, zero staging files, native trees
equal, references covered and all required queues drained. Actual test process
exits 0 in 31.212 seconds. It is now the fifth permanent hierarchy regression,
required by both the scope registry and campaign leader. All 28 campaign
controls pass with the expanded scope. Engine and Engine.Tests Roslyn analyzer
checks report no warnings/errors and no workspace-load warnings.

Canonical verification stopped on the prototype's four-node regression; a full
fresh canonical run, expanded generated coverage and final PR/main CI remain
pending for the narrowed repair. No hosted allocation probe or campaign has been activated.
Both pre-existing 100000-file runs retain their immutable sources and binaries;
they predate this repair and do not alone establish repaired-source scale
acceptance. P1.6.2d, the corpus and five-day gates remain unchecked.
