# Installer release verification budget — 2026-10-31

The installer repair adds free manual version replacement, missing-image
service recovery, and the shared version source. Release publication still
requires the complete current-head CI scope, signed installers, and manifests.

Runs [41237761806](https://github.com/taskscape/PeerSync/actions/runs/41237761806)
and [41238164181](https://github.com/taskscape/PeerSync/actions/runs/41238164181)
both exhausted the Server 2022 verifier's shared 840-second budget. The
second run spent 94.099 seconds building and reached 840.115 seconds overall.
Its metadata yield/resume regression passed in 30.954 seconds. The first
run's Server 2025 job completed its required scope successfully.

The Server 2022 output contains 1,038 completed Engine cases without a
failing assertion, but lacks a final test summary/TRX. Comparison with the
last green Server 2022 output identifies unfinished capacity/recovery and
puller cases, including the required 1,001-conflict capacity method. Neither
partial run is a passing gate. The merge preceding this repair added 625
lines of Engine tests beyond the last green source.

Increase only the Server 2022 Actions failure ceiling from 15 to 20 minutes
and its shared build/probe/Engine budget from 840 to 1,140 seconds. Preserve
one minute for setup, evidence upload, and owned-process cleanup. The default
local full-scope verifier uses the same 1,140-second budget, while an explicit
840-second run remains available. Server 2025 stays at 25/24 minutes.

This updates the Server 2022 operational ceiling retained in the historical
[Server 2025 budget decision](../proposals/P2-server2025-ci-budget.md).
Keep the full Server 2022 Engine suite and all native, account, pipe, signature,
process-ownership, and failure-evidence checks. Keep the already approved
Server 2025 duplicate-capacity exclusion and watcher ownership unchanged.
No product deadline, test assertion, simulation method, or test count changes.

Require a complete, successful new-head CI run before publishing. The new
ceiling is a failure bound; verification still exits as soon as its work ends.
