# Current-source physical client upgrade, 2026-10-27

The first physical PC (`192.168.0.176`) and second physical PC
(`192.168.0.183`) were upgraded from the development-signed 1.0.31.0 client
to 1.0.32.0. This package is built from revision
`68b7b7530af575f52d6129cf6caed8b4578b007c`, after the author-first
block-routing, peer-authentication retry and service recovery-policy repairs.
It is a development build, not a release-signed candidate.

The x64 installer SHA-256 is
`97CB272C9160548ED76EAE2E0423489D73E1546725A0F8CCA7641FE8A261CCAC`.
Its manifest reports `localDevelopmentOnly: true` and
`publicReleaseSigning: false`. Authenticode is valid under development signer
`6AE1CBFC31F1DB189AF03A82D60C535AE8E0A7BD`. The upgrade ZIP SHA-256
is `6ABFAE477DE518968BC097E667A4BE7850DFBD5DB117C3849E59BDB7D905C3B6`.

The first PC's silent setup exited 0. Its installed service is Running at
1.0.32.0, the SCM non-crash failure flag reads TRUE, and the existing
`first-pc-check.txt` SHA-256 remained
`979EF36BBE2399AEEC99C226BB8F12C9FEF41C929A2EE5F090B07A91E69903B4`.
The setup log is retained in ignored field evidence at
`artifacts/field/current-head-20261027/first-pc-upgrade-1.0.32.0.log`.

The second PC's one-time SYSTEM upgrade task completed with result 0 and was
removed. Its retained `C:\Temp\PeerSync-1.0.32.0\result.json` reports setup
exit 0, installed version 1.0.32.0, Running service, valid matching installer
signature/hash, SCM non-crash failure flag TRUE, and the same unchanged
sentinel SHA-256. The task ran from 07:51:27 to 07:51:56 UTC. Its setup log
and JSON remain on that PC outside the synchronized tree.

The third PC is not yet upgraded in this record. PeerSync 1.0.32.0 has not
completed the three-PC corpus or three-hour physical soak. An SCM
`qfailureflag` value alone does not prove automatic recovery after a
non-crash stop; Windows applies changes to that policy after the next boot.
The separate [post-reboot Server 2022 VM check](P3-service-noncrash-recovery-2026-10-27.md)
now verifies that mechanism, while physical availability must be reassessed
in the next field run.

## Old corpus cleanup remains blocked

After the exact old run directory was moved outside the first PC's synchronized
root, a service-index dump showed 100,159 tombstones for that run but 16 live
large conflict copies. Those copies contained 19,122,356,224 bytes, exceeding
the user-approved 10 GiB test-content cap before a fresh run could start.
The second PC's installed 1.0.32.0 service was Running, yet its index still
showed 96,199 live old-run rows and only 3,978 tombstones; its filesystem
contained 97,069 old-run files and 19,221,738,496 bytes. Both indexes retained
the original eight large names as tombstones, while the 16 preserved copies
had `.sync-conflict-` names. These read-only snapshots were taken around
08:28–08:30 UTC and retained under ignored
`artifacts/field/current-head-20261027/`; the second-PC index dump remains at
`C:\Temp\old-corpus-index-second.tsv` on that PC.

This is an unresolved propagation/cleanup issue, not a passing scale run.
The conflict copies must be preserved outside synchronized roots on all three
PCs and the old paths verified absent before generating a new corpus. The
third PC's temporary remote administration window has expired, so coordinated
cleanup and its 1.0.32.0 upgrade await renewed access. No additional corpus
run was started.
