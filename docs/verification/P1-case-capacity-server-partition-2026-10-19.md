# Required capacity stress and Server scope

PR #37 head b4b5454, [run 39311077920](https://github.com/taskscape/PeerSync/actions/runs/39311077920),
passes x64, ARM64, the unchanged 600-second fuzz and Server 2022. Server 2025
passes every P0 and cross-account probe, including PID-bound pipe readiness,
but its Engine child exceeds the unchanged eleven-minute deadline. It was
terminated by the runner; no completed TRX or full-suite success is claimed.
Evidence: `artifacts/pr37-readiness-2025-failed.log` and
`artifacts/ci-pr37-readiness-2025/`.

The earlier main Server 2025 partial TRX contains 704 successful results; all
sixteen missing results correspond to the 1,001-conflict startup saturation
method and fifteen later cases in CaseCopyRecoveryTests. The same 720-case
head now passes on Server 2022 in 335.55 seconds; that method takes 120.72
seconds there. This identifies the earlier uncompleted group, not a recovered
active-test trace from the latest terminated process.

Retain the complete unchanged saturation method in the required Server 2022
job and default local runner. Server 2025 explicitly excludes only that method,
retaining every other Engine test, all case-copy recovery, collision,
attributes and cancellation scenarios, and every cross-account/native probe.
The whole workflow retains all tests, five jobs, 600-second fuzz and existing
deadlines. Default local verification remains complete. Detailed result output
and 60-second xUnit long-running diagnostics make subsequent stalls actionable.
Running, failed and passed JSON all record the capacity and watcher scopes.

The partitioned runner passes locally: 719 Engine cases, zero failures/skips,
14.70-second test duration and 24.56 seconds after build including all probes
and real virtual-account/private-key/authorized and unauthorized user checks.
Run ID `671733d5a71c405588516594f3137a3d`; evidence
`artifacts/p1-server-capacity-partition-local.log`,
`artifacts/p1-server-capacity-filtered-summary.json`, and
`artifacts/tests/case-partition-filtered/`.

The earlier complete canonical 1,176-case verification remains applicable to
the unchanged C# implementation. Final default Server-runner verification and
submitted-head CI are recorded below once they complete. No blind rerun of the
failed head was started.

Final default Server runner passes all 720 Engine cases, zero failures/skips,
25.94 seconds of tests and 26.48 seconds after build including all native and
cross-account probes, exit 0. Run ID
`1bffe1e0b7d64d54ba96411aed0aecfb`; evidence
`artifacts/p1-server-capacity-full-local.log`,
`artifacts/p0-services/parallel-summary.json`, and
`artifacts/tests/native-engine/`. Comparing the two complete TRX name sets
finds exactly one exclusion: the unchanged startup capacity method, which
passed in 22.02 seconds locally. Temporary spike services are absent after
cleanup. This is local validation, not a green remote Server 2025 result.
