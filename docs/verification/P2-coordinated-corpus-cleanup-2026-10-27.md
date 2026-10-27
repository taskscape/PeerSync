# Coordinated physical corpus cleanup, 2026-10-27

The failed three-PC run `93781174d3fe4effab3396321847784e` cannot be
replaced while it remains in any joined folder. Moving its directory on the
first PC alone caused the other live peers to preserve the eight large losing
files as conflict copies. With two other peers, 16 copies entered each shared
tree and held 19,122,356,224 bytes. This is consistent with the specified
conflict rule for files above `ArchiveMaxFileBytes`; it is not, by itself,
evidence of a conflict-resolver defect. The second PC still had 96,199 live
old-run index rows in the read-only snapshot. The 10 GiB content cap therefore
rejects a new run.

`build/Move-PeerSyncGateCorpusOut.ps1` is the guarded local move counterpart
to `build/New-PeerSyncGateCorpus.ps1`. It constructs the exact run path from a
GUID, verifies the generator's `owner.json` (or a separately retained marker
when only late copies remain), rejects reparse points and an archive inside the
joined root, requires the archive to be on the same volume, and refuses
`-Move` while the local PeerSync service is running. Its default is read-only;
an explicit `-Move` performs one directory rename and writes a JSON report
outside the shared folder. It never deletes content or clears an index.
An externally retained owner marker is subject to the same ancestor reparse
check as the source and archive paths.

The first PC's read-only preflight identified the 16 late copies and exact
19,122,356,224-byte total while reporting `moved: false`. A disposable
Windows Server 2022 guest exercised the move with an owned two-file fixture:
the source was absent, the archive and JSON report existed, and PeerSync was
Running again after the controlled stop. The guest remained on signed
1.0.32.0. These are utility checks, not physical corpus acceptance.

For the field cleanup, first install 1.0.32.0 on the third PC and verify all
three joined roots and owner markers. Create a distinct local archive directory
outside each joined root on the same volume. Run read-only preflight on each exact
root. Stop PeerSync on **all three PCs** before any move; move each machine's
old-run directory to a unique archive path outside its joined root, then
restart all three services. Retain the original source archive and every late
copy archive. Verify each source path stays absent, the complete joined-tree
content is below 10 GiB on every PC, and each service's index no longer has
live old-run entries before generating a new 50,000-small-file corpus. Any
reappearing path or over-budget tree fails cleanup and prevents the next run.

At that checkpoint the third PC's temporary WinRM window was closed. Remote
access was later restored. All three PeerSync services were stopped for the
exact old-run move, then restarted. The first, second and third old-run paths
were moved outside their joined roots, retaining owner markers and conflict
copies. A recovered second-PC pending history intent produced one more
old-run file; it too was archived outside the root.

An exact anchored ignore rule for
`/PeerSync-GateCorpus/93781174d3fe4effab3396321847784e/` was applied
offline to all three folders. Source revision
`d1ae071f79dd7d7386a9328a67b83b065a6a49f2` then repaired inbound
needed-set selection so an ignored remote path cannot be pulled back in. Its
direct transport regression and all 1,051 Engine tests passed locally. After
the signed 1.0.33.0 deployment and removal of temporary PeerSync-only traffic
blocks, a 10-round, two-minute check found the old run absent on all three
PCs and disk usage nearly flat. Every joined tree contained 732,843,417
bytes before the new corpus.

The second PC's three large archives (10,297,354,588, 5,975,736,320 and
1,195,147,264 bytes) were copied to the first PC. Each full-tree SHA-256
digest matched source and destination before its second-PC source archive
was removed while PeerSync was stopped. The copied archives remain on the
first PC. The guarded report is retained at
`artifacts/field/current-head-20261027/prune-verified-second-archives-output.jsonl`
(ignored field evidence). This cleanup is complete for the old run; the new
[50,000-file retry](P2-three-pc-50k-retry-2026-10-27.md) is separate and
remains unverified until its terminal three-PC result.
