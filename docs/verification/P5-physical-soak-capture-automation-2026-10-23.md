# Hourly capture and post-run comparison, 2026-10-23

The development field kit now contains
`build/Invoke-PeerSyncSoakCaptureLoop.ps1` (SHA-256
`A0321F8A26B3EEDDDAB9768E8A4102240D81914F0BA7D86BA6E486AB7DDC383A`)
and `build/Compare-PeerSyncSoakReports.ps1` (SHA-256
`8FE4668FD77CA7C60601B8A2DBC651145012FEA93978535F0F1F9F55572A51ED`).
The first waits for a common UTC hour and writes 74 new hourly snapshots plus
a local JSONL journal by default. It records and stops on a missed hour,
existing output, changed tool or failed capture. The second accepts copied
snapshot directories from each labeled PC, produces all hourly comparisons,
then runs one completeness and elapsed-time audit. Both keep native root paths
out of their shareable JSON reports. The [physical procedure](../physical-soak.md)
keeps test writes out of a common UTC quiet window and requires the actual
sleep/resume and Wi-Fi events to be recorded separately.

The final tree tool SHA-256 is
`78027326BBEF5E4A2200D553D8F80E517F03F2B3852B3378DFA873EB19C37F3A`.
That exact tool and capture loop were copied to the installed Server 2022 and
Server 2025 guests. One Windows PowerShell 5.1 checkpoint on each exited 0;
the two full-root snapshots matched seven files, eleven directories,
2,046,346 bytes and tree SHA-256
`B8EBE308F65097B1BBAB0FC791F890B7B48DEE036754321390665BA2326BCB68`.
Their comparison exited 0 with no issues. The retained final JSONL and
snapshots are in `artifacts/vm/soak-capture-loop-final-2026-10-23/`.

For the post-run path, a clearly labeled **synthetic** fixture derived from
the two installed snapshots supplied 74 consecutive hourly reports, with
capture timestamps spanning 72.9997 hours. Windows PowerShell 5.1 batch
comparison matched all 74, and the audit passed. Removing the second peer's
hour-37 snapshot made the batch exit 1: 73 matches, one failed hour, and
`hourly-report-invalid` plus `missing-or-duplicate-hour` audit issues.
The fixture and reports are under `artifacts/soak-batch-check/`. This also
checks that comparing reports later does not invalidate actual capture-time
evidence. These synthetic dates are test inputs, not proof of elapsed time.

No physical three-PC capture campaign, 50 GB corpus, 72-hour operation run,
real sleep/resume or WAN/NAT gate has been completed by this control.
