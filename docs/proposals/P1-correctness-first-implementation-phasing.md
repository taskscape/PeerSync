# Correctness-first implementation phasing

Status: explicitly approved on 2026-10-21: "Defer CI timing blocker; continue
implementation (Recommended)". The user
explicitly directs stopping further performance tuning and completing the
system after the extended CI investigation. Performance work stops now.

The remaining P1 blocker is ordinary CI's elapsed-time ceiling. Accepted
source 3cf9eaf2cf200da4862484a1b5f2e180693657a0 already has complete green
main CI and unchanged production/Fuzz/Simulation inputs in PR #42. The
completed repaired-source five-node 100000-file corpus preserves all 100024
versions. Both actual October 20/21 thousand-seed campaign dates qualify,
with the user's explicitly approved reporting-only exception on October 21.

The current tooling source passes full local verification: thirteen actual
Simulation methods, six retained seeds, eight flat/four hierarchy cases,
connected maintenance, eleven unchanged copied assembly hashes and
600-second four-target fuzz with zero crashes. All thirty campaign controls,
twelve scope controls and two real budget/owned-cleanup controls pass.
Current hosted four-job artifacts pass exact scope/source checks, including
all 1565 unique unit results, native/account/security checks, signing,
100000 watcher operations and the 1 GiB transfer. The hosted boundary job
hits its script deadline after all seven baseline methods and five retained
results complete. Its final seed remains incomplete; no failed product
assertion is established. Preserve the failure, missing coverage and logs.

Approved narrow exception: permit merging the requirement/tooling change and
continuing P2 implementation based on this complete correctness evidence,
while recording the ordinary hosted deadline as open verification debt.
The overall failed workflow must remain visible. Do not represent its
incomplete scope as a pass or tick a green-CI requirement without this
explicit exception. Retain all tests, native durability, loss/reference/queue
oracles and every later installed-service/LAN/NAT/release correctness gate.

The exception permits phase progress and an honestly qualified P1 acceptance;
it does not authorize ignoring a failed product assertion, removing tests,
inventing evidence or declaring the full system complete. Require complete
current-source verification before release. Further CI speed work is deferred
while implementation proceeds. No new thousand-seed campaign, unchanged
corpus replay or resource-budget increase is required by this exception.
