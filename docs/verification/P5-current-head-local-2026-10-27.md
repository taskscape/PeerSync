# Current P5 repair branch local verifier, 2026-10-27

On tracked revision `eb1be5c59105421ce4031dcc622f2e70369479c2`,
`./build/Verify.ps1 -Runtime win-x64 -FuzzSeconds 600` returned success in
1,223.960 seconds. The first locked restore attempt against the shared NuGet
cache failed before compilation because access to
`CommunityToolkit.Mvvm.SourceGenerators.dll` was denied. The unchanged
canonical verifier then passed with `NUGET_PACKAGES` pointed at the previously
established isolated cache under
`artifacts/field/nuget-overlay-aa9ae5c/packages-v2`. This does not change
the solution or skip a verifier step.

The Release build reported zero warnings and errors. Ten current-date product
TRX files contain **1,821 passed, zero failed, zero skipped** methods,
including 1,050 Engine and 75 Service cases. The real-engine simulation TRX
adds **17/17 passed**; its generated flat and hierarchical summaries report
8/8 and 4/4 respectively. The native/runtime checks and six self-contained
spike publishes completed. The seven-target boundary fuzz ran for 600.002
seconds across 167,382 seeds with zero crashes and no substantive stderr.
The generated `artifacts/verification-win-x64.json` reports `passed` with
runtime checks, Engine tests and simulation enabled. Full output is retained
at `artifacts/field/current-head-20261027/verify-overlay-output.txt` and
the initial restore failure at `verify-output.txt` beside it.

This verifies the source repairs for block routing, transient peer
authentication and Windows-service failure reporting locally. It does not
make the failed installed 1.0.31.0 physical corpus run pass. A new signed
development build and post-reboot installed recovery check remain necessary.
The latest [hosted run](https://github.com/taskscape/PeerSync/actions/runs/40466678951)
again had all nine producer jobs fail before executing any steps and skipped
the strict aggregate; local success is not hosted CI. PR #49 merge,
physical LAN/WAN/NAT and soak gates, independent review and public release
signing remain open.
