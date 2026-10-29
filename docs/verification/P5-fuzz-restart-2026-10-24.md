# P5 24-hour fuzz campaign restart, 2026-10-24

The restarted campaign completed the required 24 hours and **passes P5.1**.
It uses seven deterministic fuzz targets:
`FastCDC,PathRules,VersionVector,IgnoreMatcher,FrameCodec,MessageValidator,KeyText`.
The plan's required frame decoder, message validation, path rules, ignore
parser and key text parser are included.

The earlier attempt under
`artifacts/fuzz/p5-24h-20261022T215322Z` started at
2026-10-22 21:53:23 UTC from revision `7e9f018225848fa81cf9ac168349c99e0b16fcf2`.
Its recorded fuzz PID 53320 and watcher PID 79668 are absent; both redirected
logs are zero bytes and there is no terminal result. The machine reports last
boot on 2026-10-15. The cause of that process ending is unknown. Treat the
attempt as incomplete, not a success or a demonstrated crash.

The new campaign is retained at
`artifacts/fuzz/p5-24h-20261024T090449Z`. It was built with locked restore
from revision `30bf923efc91cd3881c765311e369172ccc3c40a`. Tracked files
outside the fuzz inputs were dirty; the fuzz inputs were not. The published
`PeerSync.Fuzz.exe` SHA-256 is
`E69FD6DFECDA3DB9E891B4EEE8D3A275BA2D9CF36D30C019977973F870D333BD`.
A three-second seven-target smoke produced 1,165 seeds and zero crashes.
`launch.json` retains this provenance and smoke result.

At repository head `2380b02`, a source diff from `30bf923` is empty for the
fuzz harness and its Core, Crypto, Protocol, Engine and Storage inputs. Later
networking, rendezvous and documentation edits do not change the seven fuzz
targets being exercised by this fixed binary. This is input-path provenance,
not a claim that the binary contains every later repository edit.

At later head `c4437d0`, the only diff within those input directories is
`Core/Diagnostics/DiagnosticBundleCollector.cs`. Its callers are the CLI and
rendezvous diagnostics commands; the seven fuzz targets do not invoke it.
The newer tracker changes are outside the fuzz dependency path. This remains
a fixed-binary campaign; accepting its eventual result still requires the
terminal and crash-file checks below.

`PeerSync-P5-Fuzz-20261024` started the exact binary with `--seconds 86400` at
2026-10-24 09:05:58 UTC as a hidden SYSTEM scheduled task with a 26-hour
execution limit. Its child PID was 52808. The companion
`PeerSync-P5-Fuzz-Awake-20261024` started at 09:07:07 UTC. It requests Windows
system-awake only while that exact fuzz PID still names the expected binary,
then clears the request. `powercfg /requests` showed a `pwsh.exe` SYSTEM
request; both tasks and the fuzz child were still running when checked. This
does not change the saved power plan. The campaign's `run.ps1`,
`keep-awake.ps1`, `running.json`, `awake.json`, stdout, stderr and eventual
`terminal.json` are retained in the ignored artifact directory.

The retained [terminal result](P5-fuzz-terminal-2026-10-25.json) reports start `2026-10-24T09:05:58.0245472Z`,
end `2026-10-25T09:05:58.3807220Z`, 86,400.0013553 fuzz seconds,
31,235,254 seeds and all seven targets. Exit code, crash count, crash-file
count and stderr length are all zero. The on-disk executable SHA-256 still
matches both launch and terminal records. Both scheduled tasks reported result
zero and were removed after completion. The retained [seed corpus manifest](P5-fuzz-corpus-manifest-2026-10-25.json)
identifies the deterministic seeds 0 through 31,235,253, the fixed generator
binary and replay method. This compact seed corpus is reproducible without
saving terabytes of generated input bytes. The crash directory is absent
because no input crashed the harness; its runner would save the offending bytes
and seed record on failure.

The host's C: drive had about 1.6 GiB free before launch preparation. Fifteen
obsolete uniquely named SQLite snapshots in the retained million-file fixture
were NTFS-compressed in place, preserving their contents and the canonical
`index.db`/`tree`; this recovered about 4.6 GB. About 5.9 GB was free at
campaign preparation. No recursive fixture deletion was performed. The
million-file performance and fresh-peer run remains reserved for after fuzz to
avoid contention; adequate free disk must be confirmed before it starts.
Two additional obsolete named WAL snapshots in that fixture and the retained
`artifacts/deploy` outputs were subsequently NTFS-compressed without deleting
their contents. C: then had about 7.5 GB free. The active Hyper-V guests and
their VHD/VHDX chains were not altered. The scale run must monitor disk use
because it creates a new native peer tree and index under the fixture.
