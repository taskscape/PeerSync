# P5 24-hour fuzz campaign restart, 2026-10-24

This is launch and liveness evidence, **not a P5.1 pass**. The required 24-hour
terminal result remains pending. It uses seven deterministic fuzz targets:
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

Acceptance requires the terminal JSON to report exit code zero, elapsed fuzz
seconds at least 86,400, all seven targets, positive seed count, zero crashes,
empty stderr and no crash files. Confirm task result and binary hash, then
remove the two temporary scheduled tasks. A missing terminal file or a stopped
task is incomplete or failed evidence, never a pass.

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
