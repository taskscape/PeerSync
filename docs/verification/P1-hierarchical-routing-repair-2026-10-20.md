# Hierarchical scenario route-repair checkpoint

The previously retained seed 1878823561 passes against production repair
1154511: seven nodes, 18 operations, 18 published payload versions, two kind
changes and 11 healed rounds. All final native, pending-work, preservation and
reference-coverage assertions hold. Direct replay exits 0 in 64.637 seconds;
scenario work takes 63.566 seconds. Original failures remain retained.

A fresh whole-project smoke also exits 0: six test methods, eight flat cases
and four hierarchical cases, no failures or skips, MTP 137.186 seconds.
Flat sampling takes 12.280 seconds and hierarchy sampling 120.234 seconds.
The four hierarchy seeds (1208293936, 345689765, 594543397, 1660949200)
span four to seven nodes, 62 generated operations, 60 published versions,
four directory renames, 11 subtree deletions and five kind changes.
Temporary per-pass failures may retry; final convergence, stable high waters,
queue drain and loss/reference assertions remain mandatory.

Evidence in this worktree:
- `artifacts/p1-hierarchical-route-repair.log`
- `artifacts/simulation/hier1878823561-route-repair/`
- `artifacts/p1-hierarchical-repaired-smoke.log`
- `artifacts/simulation/20261020T011820-274bf4a6f4ca49c199335b380a3a7ee9/`

The scoped Simulation compiler/configured analyzer gate is clean at
warning/error, with no workspace-load warnings or truncation. The production
repair has two focused connected regressions plus page/corruption/deadline/
source/root/SQL tests on the primary branch; this separate property branch
is still WIP and unmerged. A larger local batch is pending. No 1000-case CI,
five-day, 100000-file, deployed service, LAN or native NAT acceptance is claimed.
The root-level staging counter is diagnostic only; it is not a recursive
retirement assertion. See `docs/simulation.md` for running and replaying.
