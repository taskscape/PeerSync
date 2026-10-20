# Expanded hierarchy clock baseline

Historical failures below are repaired by schema 35. See
[the expanded repair report](P1-hierarchical-root-set-repair-2026-10-20.md)
for passing retained regressions and a fresh 20-case batch. These historical
source-specific results remain unchanged.

The local requested 20-case hierarchy batch on production route repair 1154511
fails, with 15 completed passing cases, two retained failure fixtures and
CsCheck reporting one shrink and three skipped samples. The method exits with
MTP code 2 after 529.497 seconds; hierarchy work summary reports 528.763 seconds.
This is failed expanded validation, not 20 passing cases.

Both failing seeds reach identical native trees and zero ordinary needed rows,
but index high waters keep increasing and directory publication queues remain:
- 1648781653: seven nodes, final highs 149,146,160,153,140,141,131.
- 1044729930: six nodes, final highs 215,201,205,215,212,190.

Seed 1648781653's root snapshot references an older signed child envelope than
the current native/local child. A direct generated-fixture diagnostic confirms
that rebuilding changes its root hash and increments its clock. Other peers
can still merge newer clock knowledge into the old original metadata/root.
Focused owned/received regressions on the primary branch reproduce the missing
clock closure and require the new local publication to dominate newer known
clocks for the exact same original body. Do not absorb a different tree or
metadata as equivalent authority. The shared template/final-gate repair is
being validated before any new PR #38 CI run.

Evidence in this worktree:
- `artifacts/p1-hierarchical-20-route-repair.log`
- `artifacts/simulation/hierarchical-20-route-repair/hierarchical-summary.json`
- `artifacts/simulation/hierarchical-20-route-repair/failures/`

Original generated fixtures and pre-inspection ZIPs are retained. A one-node
publication diagnostic intentionally advanced node 6 in the first generated
fixture; its ZIP preserves the original pre-inspection state. Do not represent
that inspected native folder as an untouched baseline. Failure replay commands
retain numeric seed/mode and operation traces; native filesystem timestamps
and scheduling remain environmental inputs. Focused checked-in regressions
provide stable reproduction of the clock defect.

The first retained seed now passes against production clock repair f23f9de
in 62.307 seconds, with all final native, queue, stable-index, loss and reference
assertions. The second replay and final canonical verification are pending.
The full primary Engine suite passes all 972 cases locally before final
logging additions. No merged/CI/1000/five-day/100000-file acceptance is claimed.

The second replay subsequently fails on source f23f9de in 116.759 seconds.
Its generated fixture `PeerSync-Simulation-ac098f597d184c8b836cc2cfe2bbd989`
retains two differing parent snapshot roots, with all other non-vector body
fields equal. The looping preserved directory's current signed direct child
is identical on all six nodes and dominates the member in both old roots.
Partial same-body clock closure is insufficient. A durable multi-root causal
proof is now designed on the primary branch; two focused owned/received
acceptance regressions fail there before implementation. Keep this integration
gate open and retain all fixture/trace evidence. No new CI run was started.
