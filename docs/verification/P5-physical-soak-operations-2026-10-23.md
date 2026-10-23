# Scoped physical soak operations, 2026-10-23

`build/Invoke-PeerSyncSoakOperations.ps1` supplies an unattended, bounded
create/overwrite/append/rename/delete workload for the physical 72-hour
LAN/WAN gates. It accepts an explicit synchronized root, run GUID, distinct
machine label and local log directory. It operates only inside
`PeerSync-Soak/<run>/<label>` and requires the matching owner marker on
subsequent invocations. Its JSONL operation trail stays outside the synced
root and records UTC times, operation type, generated item IDs, byte counts,
content hashes and bounded error codes. It does not log native root paths,
folder capabilities or user file contents. The final tool SHA-256 is
`4C4C03E87B4F60523B6AA779EA48FE63DC066E04594B5AF8DBCACD5549EAB4C4`.

On a disposable host fixture, Windows PowerShell 5.1 completed two seeded
100-operation invocations with zero failures. The first covered all five
operation types; an unrelated sentinel file retained its SHA-256. A second
invocation reused the same run and label, demonstrating bounded continuation.
Changing the owner marker to a different run and choosing a log directory
inside the synchronized root each exited 1 before creating another JSONL.
The fixture and logs are under `artifacts/soak-operations-check/`.

The exact script was then copied to the installed Server 2025 client guest.
A 30-operation Windows PowerShell 5.1 batch (one-second spacing, seed 789)
exited 0 with all five operation types, 30 completed records and zero failed
records. The Server 2022 client guest received the resulting two binary files
and owner marker through the installed service. Both source and target had
the same three workload files and 80,266 workload bytes. Full-root read-only
tree captures on the two guests then exited 0 and matched: seven files,
eleven non-metadata directories, 2,046,346 bytes and SHA-256
`B8EBE308F65097B1BBAB0FC791F890B7B48DEE036754321390665BA2326BCB68`.
The compare report exited 0 with no issues and capture starts roughly one
second apart. The retained guest operation log SHA-256 is
`9979844606D73601F0A4B7631E902943CA394902F3537F67DE01523EB2FEB0E3`;
the comparison JSON SHA-256 is
`2565E1FAF30449E09CCFB7289EFD00B780B39E73FD2FE07B073834CA486DD643`.
Evidence is under `artifacts/vm/soak-operations-2026-10-23/`.

The [physical procedure](../physical-soak.md) now pairs the operation loop
with hourly tree capture and documents manual sleep/resume, Wi-Fi switch and
permission-change evidence. The development [field kit](P4-field-deployment-collector-2026-10-23.md)
contains the operation, tree, capture and comparison tools with their source
hashes. These local and two-guest
controls do not establish a 50 GB three-physical-PC corpus, 72 elapsed hours,
native NAT behavior or a release-candidate soak.
