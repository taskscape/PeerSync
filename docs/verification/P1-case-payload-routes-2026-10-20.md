# Retained case payload route repair

Hierarchical seed 1878823561 identified a real recovery stall: two of seven
nodes retained four needed rows and pending directory cases after 24 healed
rounds. The original member payload remained cached on another connected node,
but no current remote advertisement matched the old directory envelope. The
old callback rejected every route except the original forwarding hint and
exact current advertisers. Retrying could not discover the available bytes.

Two focused connected regressions reproduced the same defect for a file case
and a nested directory case. Both fail before repair. The holder has an
unrelated signed path with identical payload bytes, is connected/known to the
receiver, and never advertises the old directory/file path. The original sender
disconnects before reconstruction. Both regressions now pass and drain work.

Schema 34 stores per-pending-source route hints and ordinal known-device
continuation, validated against the immutable original and root binding in a
FULL transaction. One six-device page is durably advanced before network work
and reused for the entire attempt, alongside two preferred hints. Response
bytes are owned and length/BLAKE3 checked before acceptance. Routes get
one-second deadlines within a ten-second source attempt; caller cancellation
propagates. Successful routes are remembered, completion deletes route state,
and no network await holds the reference gate. Existing source IDs, graph,
native installation and recovery authority remain unchanged.

New controls cover corrupt/stalled peers, cancellation, reference-gate freedom,
redacted route diagnostics, one-page reuse across multiple reads, later-page
success and remembered hints, restart/wrap, immutable inputs, source/root
rejection, completion cleanup and SQL rollback. Upgrade fixtures now remove
the new table/trigger when constructing actual older schemas. Existing retry
checks assert bounded route attempts and no retry before the durable deadline.

Final production tree bf4c2da passes `build/Verify.ps1 -Runtime win-x64 -SkipFuzz`:
all 1505 canonical cases (965 Engine, 288 Storage), eight additional flat
scenarios, diagnostic/control/native/transport probes and win-x64 publishing;
exit 0 in 179.0627134 seconds. No unit cases failed or skipped. Local boundary
fuzz was explicitly skipped; required ten-minute CI fuzz remains mandatory.
Scoped compiler/configured analyzers for Engine, Storage and both test projects
report no warning/error, workspace-load warning or truncation. Diff check passes.

The separate WIP hierarchy branch rebased onto production repair 1154511:
retained seed 1878823561 passes (18 versions, 11 healed rounds, 64.637 seconds),
and eight-flat/four-hierarchy whole-project smoke passes (six methods, 137.186
seconds). All final native/queue/preservation/reference oracles hold. A larger
batch is in progress. This is supplementary local generated-data evidence;
property acceptance, final PR CI/main and complete phase gates stay open.

Primary evidence:
- `artifacts/p1-case-route-baseline.log` (two focused baseline failures)
- `artifacts/p1-case-route-storage.log` and `artifacts/p1-case-route-engine.log`
- `artifacts/p1-case-route-accepted-canonical.log`
- `artifacts/verification-win-x64.json` and `artifacts/tests/*.trx`

Separate property-worktree evidence:
- `artifacts/p1-hierarchical-route-repair.log`
- `artifacts/simulation/hier1878823561-route-repair/`
- `artifacts/p1-hierarchical-repaired-smoke.log`
- `artifacts/simulation/20261020T011820-274bf4a6f4ca49c199335b380a3a7ee9/`

Earlier failed full attempts remain in `p1-case-route-canonical.log` and
`p1-case-route-final-canonical.log`; their stale-schema fixtures/old event and
single-route expectations are superseded by the final complete run above.
Physical LAN, native NAT, installed service, large corpus and five-day/1000-CI
acceptance remain unverified. No checklist box is ticked by this checkpoint.
