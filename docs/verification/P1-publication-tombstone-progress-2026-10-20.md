# P1 staged-publication tombstone progress - 2026-10-20

Status: local repair under canonical verification; PR #38 remains unmerged.
No P1 gate or task checkbox is closed by this report.

## Reproduction and cause

Campaign preflight shard 1 failed hierarchy seed `1583987140` on source
`819f0fd26fbd7a205d603095da131520dbf59654` (same Engine as PR #38's
`161af31474ea81a929427a672c71a1ec6ff2fa50`). A direct unchanged-build replay
also failed, exit 2, after 24 healing rounds. Evidence is retained in
`artifacts/p1-campaign-preflight-1.log` and
`artifacts/p1-replay-1583987140-before.log`, with the original native fixture at
`C:\Users\mzag\AppData\Local\Temp\PeerSync-Simulation-4bb1a021d25c42ecb99e6b0d0d27b23e`.

Node 1 has an ordinary `topic` publication waiting for reconstructed
`topic/nested`. The latter requires a child tombstone covering an old graph
member. Local `TOPIC/nested/shared` is already deleted with vector `2:2`, but
received deletion `1:2,2:2,3:2` cannot apply beneath the ordinary parent barrier.
Nodes 0 and 2 converge to regular file `TOPIC`; node 1 retains the old directory
and four needed rows. Completed case copies alone cannot resolve this cycle.

## Repair and focused results

The design and WIP task were committed before production edits. Scheduled
application may advance only an already-deleted, published, unflagged regular
file tombstone after rechecking ordinary selection policy, complete sender range,
reference ownership, case barriers, bound root and guarded native absence. It
rechecks absence after preparation and uses the existing durable expected-sequence
application/needed transaction. It never mutates native content or discards a
publication or graph proof. Recovery safely abandons uncommitted metadata work
under the existing publication barrier; normal scheduling retries it.

The focused positive test failed before the repair (`Blocked` versus `Applied`).
After repair all 29 tests selected by `*Tombstone*` pass, exit 0. Eleven additions
cover the positive case, native file/directory occupants, a live row, a flagged
row, incomplete sender ranges, case work, recreation after preparation, restart
at two boundaries and SQL rollback. Evidence:
`artifacts/p1-tombstone-complete-focused.log`.

Direct hierarchy replay now passes in 13.038 seconds, 10 healing rounds, three
nodes, 17 attempted mutations and eight preserved published versions, with all
native/loss/reference/queue oracles intact. Evidence:
`artifacts/simulation/replay-1583987140-after/hierarchical-cases.jsonl` and
`artifacts/p1-replay-1583987140-after.log`. The seed is now a fourth permanent
retained hierarchy regression. This replay is not a completed campaign day.

Roslyn Engine compiler/analyzer diagnostics report no warnings/errors and no
workspace-load warnings. Full canonical verification, including ten-minute
boundary fuzz, is running; final results and CI/main acceptance remain pending.

## Other validation

The five-shard local campaign preflight stopped after shard 1 failed; shards 2-4
were not run. Its earlier shard 0 pass is partial evidence only. Campaign tooling
is inactive, unmerged and not scheduled. The original 100000-file corpus remains
running against its immutable older source in its own worktree; no final-source
scale pass is claimed. Physical LAN/native NAT and installed production service
checks remain unverified under the existing phase rules.
