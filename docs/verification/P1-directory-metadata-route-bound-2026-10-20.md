# Metadata route page bound repair

Review found that the bounded route page was being advanced for each fetched
graph node rather than once per source attempt. A root-only peer followed by
a peer holding the remaining metadata reproduced the defect: the second fetch
skipped that working peer in the first page, and the source attempt deferred.
The new one-test baseline fails before repair, exit 2, 1.780 seconds.

Resolve one route page lazily per source attempt and reuse it for every node
fetch. Successful hints remain usable, the next page cursor persists with the
attempt, and later attempts still reach forwarders beyond the first page. This
enforces the existing two-hint/six-device design bound without advancing past
working peers between nodes. Cache-only attempts do not advance the cursor.

After repair all 941 Engine tests pass, zero failures/skips, exit 0,
33.096 seconds, including the new regression, route paging across restarts,
stalled-hint failover and four-node/edited/offline-owner directory convergence.
Engine and Engine.Tests compiler/configured analyzer checks are clean at
warning/error, without workspace warnings or truncation. `git diff --check`
passes. Storage is unchanged from the preceding all-282-pass checkpoint.

Evidence: `artifacts/p1-metadata-route-bound-baseline.log`,
`artifacts/p1-metadata-route-bound-full-engine.log` and their corresponding
TRX directories under `artifacts/tests/metadata-route-bound*`.

The immediately preceding canonical runner passed all 1474 cases plus eight
simulations/probes/publishing before this narrow repair. It was not repeated
for this Engine-only change; the expanded full Engine suite above verifies the
final code. No production activation, CI/main or full phase completion is
claimed.
