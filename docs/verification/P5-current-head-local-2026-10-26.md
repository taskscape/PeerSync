# P5 current-head local verifier, 2026-10-26

The canonical `./build/Verify.ps1 -Runtime win-x64 -FuzzSeconds 600` command
passed on clean tracked revision `9ee2c3137d25dc7a66dfe435d7a0d110e3f0962d`.
It completed in 1,155.987 seconds with locked restore, Release build,
runtime checks, Engine tests and real-engine simulation enabled. The build
reported zero warnings and errors. The generated summary is
`artifacts/verification-win-x64.json` (`status: passed`). This is local
verification of the branch, not a green hosted workflow or a physical gate.

| Local result | Evidence |
| --- | --- |
| Core, Crypto, Protocol, Net | 152, 15, 77, 59 passed respectively |
| Engine, Storage, Control.Contracts | 1,046, 305, 39 passed respectively |
| Service, App, Rendezvous | 73, 23, 24 passed respectively |
| Real-engine simulation | 17/17 passed, zero skipped; eight generated flat cases, four generated hierarchical cases and ten retained hierarchical seeds are covered by the suite |
| Native/runtime checks | Diagnostics collector and control-pipe readiness passed; IPv4/IPv6 QUIC, source-port rejection, pipe identity, HTTP/2, HTTP/3, unreachable-rendezvous, watcher and QUIC diagnostic checks returned their expected outcomes |
| Boundary fuzz | Seven targets, 181,820 seeds, 600.003 seconds, zero crashes |
| Self-contained spike publish | QuicBasics, QuicPunch, ControlPipe, DeviceCert, Rendezvous99 and WatcherStress published for win-x64 |

The eleven current-date TRX files in `artifacts/tests` and its `simulation`
subdirectory total 1,830 passing methods with zero failures or skips. Older
TRX files in the same artifact directory were excluded from this count. The
simulation run directory is
`artifacts/simulation/20261026T093234-8f302d690bde4cebad6c7c10c6892349`.

The [current-head GitHub run 40299717397](https://github.com/taskscape/PeerSync/actions/runs/40299717397)
for that same revision was denied before executing any job steps because the
Actions budget remains exhausted: all nine producer jobs failed with zero
steps, and the aggregate job was skipped. The user authorized local validation
when Actions credits run out. This local pass does not meet `plan.md` §0.2's
formal green-CI/merge condition for P5.3 or any physical, independent-review
or release-signing gate.
