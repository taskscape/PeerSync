# P5 scanner memory telemetry, 2026-10-23

The million-file stage trace measured a working-set peak near 2.55 GB while
the later idle sample was below 300 MB. To make future installed-host runs
diagnosable, debug-enabled scans now write three stage events under the scan's
existing session ID: `scan.memory_enumerated`, `scan.memory_hashed` and
`scan.memory_committed`. Each records elapsed milliseconds, visited-item count,
working set, private bytes and managed live bytes. These are point-in-time
samples; the benchmark's process peak remains the authority for a peak-memory
claim. Numeric values survive sanitized bundle collection. Paths, keys and
file contents are absent from these events.

Local Release verification after the change:

| Check | Result |
| --- | --- |
| Core diagnostic journal/bundle focused tests | 13 passed, zero failed/skipped |
| Scanner focused tests | 17 passed, zero failed/skipped |
| Full `PeerSync.Core.Tests` | 150 passed, zero failed/skipped |
| Full `PeerSync.Engine.Tests` | 1,036 passed, zero failed/skipped |

The already-running million-file fresh-peer benchmark uses the earlier
immutable executable, so its journal cannot demonstrate the new fields. No
new million-file benchmark was started solely to check telemetry. The
2.55 GB measured peak remains open under a peak interpretation of P5.3, and
the full fresh-peer result remains pending.
