# Bounded ordinary CI Simulation cohorts

The ordinary PR #42 run 39588878721 hit its existing 50-minute boundary-job
limit. Its actual TRX contains eleven successful methods, with no failed
assertion. Earlier serial methods consumed almost all the available time:
four generated hierarchy cases took 21m15s and five completed retained seeds
took 22m09s. Seed 1648781653 started near the deadline and was canceled. The
unchanged submitted source passes that seed locally in 55.603027 seconds;
the verified Simulation binary remained unchanged throughout the replay.
The other four jobs passed. Preserve the original incomplete run and reports.

Run two Simulation cohorts concurrently inside the existing boundary job:
seven baseline methods (including generated flat/hierarchy, native oracle
controls and connected maintenance), and all six retained regressions.
Use existing complementary method filters, identical immutable outputs and
one fresh evidence directory. Each cohort owns a separate results directory
and process handle with drained stdout/stderr. The default local/campaign
Run-Simulation behavior and daily evidence format remain unchanged.

The script charges restore/build and all workers to one 2700-second budget
inside the unchanged 3000-second job. Poll owned processes for failures and
the deadline; on error stop only those owned process trees and preserve logs.
Do not start a worker if build has exhausted the budget. Fuzz still runs for
600 actual seconds over all four targets, with zero crashes required.

Validate both fresh TRX files together against the existing scope registry:
exactly thirteen unique successful results, six exact retained seeds and
one maintenance method, with each report's complete matching counters.
Reject missing, duplicate, failed, skipped, partial or extra results. Verify
unchanged eight-flat/four-hierarchy summaries, case lists, all six retained
summaries and connected maintenance evidence. Record source identity and
unchanged actual executable hashes; no build runs after worker startup.

Run meaningful positive/negative evidence controls, a bounded cleanup check,
and the complete local boundary runner. Require actual green PR/main jobs
and artifact audits before merging/closing acceptance. Do not increase job
limits, add jobs, reduce cases/retained seeds/healing rounds or weaken native,
reference, queue or published-version oracles. No further thousand-seed
campaign or repeat of the unchanged successful 100000-file corpus is needed
for this orchestration-only change.

For P2 CI only, the user approved a later [isolated-runner exception](P2-boundary-ci-runner-isolation.md)
on 2026-10-22 after both the two-cohort and four-cohort hosted layouts
exhausted the same shared runner budget. This historical P1 decision and its
failed-run evidence remain intact.
