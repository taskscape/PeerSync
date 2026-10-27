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

The first PC's read-only preflight identified the 16 late copies and exact
19,122,356,224-byte total while reporting `moved: false`. A disposable
Windows Server 2022 guest exercised the move with an owned two-file fixture:
the source was absent, the archive and JSON report existed, and PeerSync was
Running again after the controlled stop. The guest remained on signed
1.0.32.0. These are utility checks, not physical corpus acceptance.

For the field cleanup, first install 1.0.32.0 on the third PC and verify all
three joined roots and owner markers. Run read-only preflight on each exact
root. Stop PeerSync on **all three PCs** before any move; move each machine's
old-run directory to a unique archive path outside its joined root, then
restart all three services. Retain the original source archive and every late
copy archive. Verify each source path stays absent, the complete joined-tree
content is below 10 GiB on every PC, and each service's index no longer has
live old-run entries before generating a new 50,000-small-file corpus. Any
reappearing path or over-budget tree fails cleanup and prevents the next run.

The third PC's temporary WinRM window was closed at this checkpoint. The
coordinated field move, new corpus and physical three-hour gate remain open.
