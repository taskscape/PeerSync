# Hosted bounded campaign preflight

[Run 39385642102](https://github.com/taskscape/PeerSync/actions/runs/39385642102)
passes its planner, all five Windows shards and coordinator on immutable green
main `bf68d97743167730722913672b2562e1e86cf183`. The authoritative workflow ID
is 390216948, path `.github/workflows/p1-campaign.yml`, run number 1, attempt 1,
event workflow_dispatch. This is explicitly mode=preflight.

Each downloaded actual TRX contains all seven baseline methods, all Passed;
there are 35 executed method results across the five shards. Manifests record
the expected source/run/attempt and clean checkouts. All five test binaries
share SHA-256 `F8B81C13B9F44693088B814E81D395DB989DE4EE8D1E51E988A55063F036B8A9`.
The unchanged evidence validator verifies exact method names/outcomes/counters,
case lists, source/run/role, maintenance proof and binary binding.

The actual coordinator and independent local revalidation both report ten
distinct completed flat seeds, five hierarchy samples and five connected
maintenance cases, with ten native archive retirements and ten block evictions.
Flat-case reports contain 150 operations, 92 writes, four renames, twelve
deletes and 92 ledger versions; their actual node counts are 3, 4, 5 and 6.
Hierarchy samples are separately verified. Long retained regressions are
intentionally omitted in this preflight role and remain mandatory in campaign
shard zero and normal full CI.

Shard job durations including setup/build/upload are 3m00s to 5m34s. The planner
takes nine seconds and coordinator eight seconds. GitHub completes the run at
07:24:32 UTC on 2026-10-20; the last producer completion is 07:24:06 UTC.
Downloaded evidence is in `artifacts/p1-hosted-preflight-39385642102/` and the
independent report is `artifacts/p1-hosted-preflight-revalidated-39385642102.json`.

Both reports retain qualifiedCampaignDay=false/gateP1Passed=false. No campaign
activation variables are set. The separate hosted cached-planner allocation
probe still requires merged PR/main proof and actual negative/positive reruns
before activation. Re-read the actual campaign workflow counter at activation;
the observed preflight number 1 must not be assumed to remain the latest.
The 100000-file corpus and five consecutive qualified UTC days remain open.
