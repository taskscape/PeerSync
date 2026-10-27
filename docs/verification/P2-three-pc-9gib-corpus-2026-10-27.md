# Physical three-PC 9 GiB corpus, 2026-10-27

Gate P2's bounded physical corpus run uses the already joined Windows PCs:
the first PC at `192.168.0.176`, the second at `192.168.0.183`, and
LARKBOX-X-1 at `192.168.0.164`. All three installed service executables
report version 1.0.31.0. The first and third services were observed Running;
the second PC's installed binary was read through its administrator share.
The existing 4 KiB three-PC byte checks and direct QUIC path are documented
separately in [the join report](P2-three-pc-lan-join-2026-10-26.md).

Run ID: `93781174d3fe4effab3396321847784e`. On a fresh read-only preflight,
each physical joined tree contained 732,843,417 content bytes, excluding its
root `.psp` metadata. The first PC had 36,581,523,456 free bytes, the second
47,433,207,808, and the third 138,457,083,904. The field generator required
24,696,061,952 free bytes on the source for native content, an independent
block reserve and 5 GiB operational headroom. Adding 9,663,676,416 corpus
bytes projected 10,396,519,833 total synchronized content bytes, below the
10,737,418,240-byte ceiling. The remote tree counts were read through the
second PC's administrator share and a restricted WinRM session to the third.

The first PC's test folder was paused at 2026-10-27 04:19:15 UTC. The
Windows PowerShell 5.1 generator started at 04:19:21 UTC and completed at
04:21:00 UTC, creating exactly 100,032 files and 9,663,676,416 bytes:
100,000 small files, 24 deep-tree files and eight large files. Its SHA-256 was
`C7BFA44A820217EE9D4D52DDFC649D9A6D5CD4008322B362D64B1AA366501191`.
The first PC resumed the folder at **04:21:05 UTC**. The three-hour
convergence deadline is **07:21:05 UTC**. Preflight and generation JSON are
retained under `artifacts/field/physical-preflight-20261027/` (ignored by Git).

The generation result proves the source corpus only. Gate P2 remains open
until the second and third PCs independently hold the complete native tree,
full tree hashes match across all three PCs, the elapsed time is within the
three-hour window, and physical transport/disk/service evidence is retained.

At 05:09 UTC, the second PC had 9,380 native files and 9,570,814,127 bytes
within this run's subtree; the third had 517 files and 5,976,265,903 bytes.
These are interim counts, not a pass. The same `Measure-PeerSyncTree.ps1` source
(SHA-256 `94B81351E1632B748DC3E0D4F08F6D682B3558C2754E7CFA359E8A1544025588`)
and its bounded soak helpers are staged and hash-verified outside the joined
folders on both receivers. Each has a temporary SYSTEM task waiting for a
capture request; the task removes itself after capture or timeout. The first
PC's bounded monitor at
`artifacts/field/physical-preflight-20261027/Monitor-PhysicalCorpus.ps1`
checks exact native counts and bytes, signals all three full-tree captures
only after both receivers reach the source totals, and writes its journal and
terminal result under that ignored evidence directory. A failed or missing
terminal result must be investigated; interim counts cannot close the gate.

At 06:21:47 UTC the second PC's installed 1.0.31.0 service stopped with
`AuthenticationException` during a TCP data attach to the third PC. Its
service journal records `transport.tcp_data_attach_failed`, `assembly.failed`,
`apply.failed`, `puller.pass_failed` and `service.folder_scheduler_stopped` in
that order; the exception then reached `service.start_failed`. The second
native file count stalled at 77,431. The original service journal is
`\\100.88.55.83\C$\ProgramData\PeerSync\logs\service\ef6ba258f84242ab84c92d121a3d4a85\debug.jsonl`.
The same installed service was started again at approximately 06:29 UTC
without restarting the corpus or changing its 07:21:05 UTC deadline. The
second count resumed, reaching 81,431 by 06:34 UTC; the third reached 45,781.
This is an intervention and a real availability failure even if the native
hashes eventually match. The source-side retry repair is recorded in
[the authentication-recovery note](P2-peer-authentication-recovery-2026-10-27.md);
it has not been installed on the three field PCs in this run.

The monitor wrote its terminal result at **07:21:05 UTC**, the original
three-hour deadline. It reports `failed` with
`native-convergence-deadline`. The source and second PC each held exactly
100,033 files and 9,663,676,591 bytes in this run's native subtree; the
third held 74,620 files and 9,637,653,679 bytes. The deficit was exactly
25,413 small files and 26,022,912 bytes. No common capture hour was
signaled, so no three-way native hash comparison exists. The monitor stderr
file is empty. Its terminal JSON and one-minute progress journal are retained
under `artifacts/field/physical-preflight-20261027/`.

At the deadline the manually restarted second service was running, and the
third service journal still recorded successful `apply.complete` events and a
1,000-file pull pass with no deferred items; it had not stopped or run out of
content. Its recent journal also recorded requests to a peer that could not
serve a block, causing avoidable fallback traffic. The installed 1.0.31.0
build predates [author-first block routing](P2-source-aware-block-routing-2026-10-27.md),
the [authentication retry repair](P2-peer-authentication-recovery-2026-10-27.md),
and [service recovery reporting](P3-service-noncrash-recovery-2026-10-27.md).
This run is a failed physical gate, not a throughput pass or a hash pass.
The source repairs need a new signed test build and a fresh bounded physical
run; a later arrival of the remaining files cannot change this run's result.
The subsequent [requirement amendment](../requirements/three-hour-ten-gib-test-budget.md)
sets the next physical run to 50,000 small files under the same 9 GiB and
three-hour bounds, as the user had approved reducing file count to fit that
window. This historical 100,000-file run remains failed.

For the next run, the exact owner-marked source subtree was moved atomically
from the first PC's synchronized root to
`C:\PeerSync-Test\GateCorpus-Archive\93781174d3fe4effab3396321847784e`
at 08:00:23 UTC. The archive retained its matching `owner.json` and the
immediate counted shared content was 732,843,417 bytes. Eight old large files
then reappeared in the synchronized path from peers that still held the old
corpus (9,561,178,112 bytes). The revised generator's whole-tree preflight
correctly rejected a new 9 GiB run. The first PC's scanner started at
08:00:25 UTC and was still processing this bulk removal at 08:07 UTC; this
record does not yet classify the reappearance as a persistent deletion defect.
The old corpus must be absent from all three synchronized roots, and the
preflight must pass, before a fresh 50,000-file run begins.
