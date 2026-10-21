# P1 campaign requirement: two consecutive UTC dates

Approved by the user on 2026-10-21: change the five-day requirement to a
two-day requirement and document it in the requirements. This supersedes the
earlier five-date policy in historical design decisions and verification
reports; those reports retain their original observations.

Gate P1 now requires exactly two successful campaign reports from distinct
run IDs on consecutive actual UTC completion dates, with the same immutable,
merged, green source commit and first workflow attempts. Each date still
requires 1000 distinct flat seeds across three-to-seven-node simulations,
five hierarchy samples, all six retained regressions, five connected
maintenance scenarios and unchanged native/reference/queue/version oracles.
Preflights, partial/cancelled/failed results, reruns, repeated/gapped dates,
mixed sources and incomplete evidence cannot satisfy the gate.

The already verified 2026-10-20 run 39440958034 may count as the first date.
Today's scheduled run 39582029908 may count as the second date only after all
seven jobs complete successfully and its actual artifacts and source, binary,
method, count and UTC-date evidence pass independent validation. Both runs
use source `3cf9eaf2cf200da4862484a1b5f2e180693657a0`; preserve this pin and
their actual binary identities. Updating only history acceptance and campaign
resource policy does not require repeating unchanged production simulations.

New activations permit at most two attempts. This active campaign began at
workflow number 3, so reduce its last permitted number from 7 to 4 without
canceling or rerunning today's attempt. Do not extend the ceiling on failure.
After successful two-date acceptance, clear both activation variables and
retire the daily schedule. Keep the original source/run evidence and cost
bounds visible.

Update the history validator and meaningful positive/negative controls,
validate the two real reports against authoritative GitHub metadata, and
retain green merged PR/main validation for the tooling change. The completed
100000-file corpus and every other P1 task remain required. P2 starts only
after all updated Gate P1 criteria are proved and ticked; physical LAN,
native NAT and installed-service/production-transport checks remain mandatory
at their previously approved P2/P4 gates.
