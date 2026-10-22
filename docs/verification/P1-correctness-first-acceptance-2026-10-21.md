# P1 acceptance under the approved timing exception

The user explicitly approves "Defer CI timing blocker; continue implementation"
on 2026-10-21. Apply the [narrow phase exception](../proposals/P1-correctness-first-implementation-phasing.md),
stop performance tuning and proceed to P2. This acceptance is qualified by
open hosted timing verification debt. No failed workflow is called green.

PR [#42](https://github.com/taskscape/PeerSync/pull/42) is merged as
`d06808e4c297da3e5a7ed6218d01f9288fb21832`. Its tree
`58364e4bd16013f815fa34a0787310f38a4e5099` exactly matches the audited
submitted/synthetic trees. Fresh review has no unresolved threads or requested
changes. Production/Fuzz/Simulation inputs match the already green merged
source 3cf9eaf2cf200da4862484a1b5f2e180693657a0.

Correctness evidence remains complete: the repaired five-node 100000-file
corpus preserves all 100024 versions; the two real campaign dates each prove
1000 distinct flat seeds/five hierarchy samples/six retained regressions and
native maintenance; the full local boundary runner passes thirteen actual
methods, unchanged native/reference/queue/loss oracles and 600-second fuzz.
Thirty campaign, twelve scope and two real cleanup/budget controls pass.

Current hosted run [39598885501](https://github.com/taskscape/PeerSync/actions/runs/39598885501)
passes all four packaging/Server jobs. Downloaded artifacts prove the exact
1565-unit method set, native/account/security probes, signing and temporary
identity cleanup, 100000 watcher operations and the 1 GiB transfer. The
boundary job fails at 2700.7006037 seconds: baseline seven methods pass,
five retained results complete, and the last retained case remains incomplete.
Fuzz passes 600.0013087 seconds/112086 seeds/zero crashes. All owned processes
stop without cleanup errors. No new failed product assertion is established.
Preserve this failure and the earlier timed-out run; neither is full hosted
Simulation acceptance.

Actual source/run/job/TRX/artifact metadata, partial and exact-scope audits,
logs and qualified merge proof remain under `artifacts/ci42-cohorts-pr/` and
`artifacts/p1-ci42-qualified-merge-audit.json`. Full corpus and both campaign
reports are linked from plan.md. No more campaigns, corpus replays or CI
performance experiments are started. All tests and full current-source
verification remain mandatory before release; installed-service, physical
LAN and native NAT gates retain their approved P2/P4 destinations.
