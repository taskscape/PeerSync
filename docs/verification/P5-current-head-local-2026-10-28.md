# Current PR head local verifier, 2026-10-28

The canonical `./build/Verify.ps1 -Runtime win-x64 -FuzzSeconds 600`
completed on tracked commit `a3a9ba50e0e5da10763c36abec8845c728efb0d3`.
The run used the existing isolated NuGet package cache at
`artifacts/field/nuget-overlay-aa9ae5c/packages-v2`; no verifier stage was
skipped. It started at 2026-10-28 00:06 UTC, finished at 00:20:49 UTC,
returned exit 0 and wrote a `passed` result with 863.487 seconds elapsed.

The Release solution build reported zero warnings and zero errors. All ten
current-date product TRX files passed: **1,836 passed, zero failed, zero
skipped**. The separate real-engine simulation TRX passed **17/17** methods;
its generated flat and hierarchical summaries passed 8/8 and 4/4 cases.
Native P0 probes, QUIC diagnostics and all six self-contained spike publishes
completed. The seven-target boundary fuzz ran 600.0029698 seconds across
202,426 deterministic seeds with zero crashes. The full verifier output is
`artifacts/field/current-head-20261028/verify-output.txt` (SHA-256
`3E4A73B43CB6ECF1EF5FB085F9FB2978AEA5608B8E0AD71833F173AB0B23D0B6`),
and the machine-readable result is `artifacts/verification-win-x64.json`.

[PR #49's matching hosted run](https://github.com/taskscape/PeerSync/actions/runs/40606070566)
failed before any of its nine producer jobs executed a step. GitHub annotated
each with “The job was not started because an Actions budget is preventing
further use.” The strict aggregate was skipped. Local success therefore does
not close the required hosted CI/merge gate.

The installed three-PC 1.0.41.0 physical soak is a separate run and was still
live when this verifier finished. Its hourly native-tree evidence, physical
LAN/WAN/NAT gates, independent security review and release signing remain
unverified by this local run.
