# P5 current-head local verifier, 2026-10-26

## Repaired branch at `9b7363b`

The first canonical fallback after the Server 2022 installer repair ran on
`93b2620`. Locked restore and the Release build passed with zero warnings or
errors. Core, Crypto, Protocol, Net, Engine, Storage and Control.Contracts
passed, but Service finished 72/73: `ListenerWaitsForPreviousTcpOwnerToReleasePort`
could not bind its QUIC listener, with native socket error 10048. Its journal
recorded `service.listener_bind_retry` and no recovery before the 15-second
retry window ended. The fixture had reserved an ephemeral TCP port but had
never checked UDP availability, while production binds QUIC before TCP. This
is a test-port collision, not evidence of a sync data failure. The full failed
log remains at `artifacts/verification-p5-93b2620-20261026.log` and its
Service TRX was superseded by the next run.

Commit `9b7363bd48307c0923f10d01d0e2ac193d4074fc` makes that fixture
choose a port available to both TCP and UDP before it deliberately holds only
the TCP listener. The complete focused Service suite then passed 73/73. A
single clean-commit rerun of the canonical
`./build/Verify.ps1 -Runtime win-x64 -FuzzSeconds 600` passed in
1,002.865 seconds, with locked restore, zero build warnings/errors, all
runtime checks, Engine and real-engine simulation enabled. Eleven TRX files
created between 15:53 and 16:08 UTC total 1,830 passing methods, zero failed
and zero skipped; this includes all 1,046 Engine cases and 17 real-engine
simulations. IPv4/IPv6 QUIC, HTTP/2+HTTP/3, owner/PID pipe checks, watcher,
unreachable-rendezvous and QUIC diagnostics returned their expected outcomes.
All six self-contained spike publishes completed. The seven-target fuzz
summary reports 184,982 seeds in 600.003 seconds with zero crashes. The
terminal summary is `artifacts/verification-win-x64.json`; the preserved
console log is `artifacts/verification-p5-9b7363b-20261026.log`.

The unchanged `build/Run-InstallerCi.ps1` also passed on this source revision:
development-signed x64, Arm64 and Rendezvous setups compiled, with signed and
timestamped payloads, online update-signer/publisher-pin checks and verified
Microsoft VC++ redistributables. The setup hashes are:

| Package | SHA-256 |
| --- | --- |
| `PeerSync-Setup-1.0.0.0-x64.exe` | `68875CEB26D7BA279777911E9463044919F71859730B455C984FD1BB8BCAF3FC` |
| `PeerSync-Setup-1.0.0.0-arm64.exe` | `00F8EE9F137A93126B5A25C0C0C1140D0E018FAA0C6F4D9FF93640D361CEEBD4` |
| `PeerSync-Rendezvous-Setup-1.0.0.0-x64.exe` | `36FCD0C863C7955F2EE2CFFD76096B37EAF9FEDB0B1073120DDD124FC6F64586` |

The runner's temporary development certificate was absent from LocalMachine
Personal and Root after cleanup. Consequently, a later standalone
`Get-AuthenticodeSignature` on those test setups reports `UnknownError`
without that temporary trust; the runner verified valid signatures before
removing it. The package manifests correctly name `9b7363b` as source and
mark the working tree dirty because of the unrelated, untouched untracked
`Sync.zip.tmp`; tracked source was clean during the build. The full installer
log is `artifacts/installer-ci-9b7363b-20261026.log`. These are development
packages and do not meet the public release-signer gate. The separate signed
1.0.29.0 x64 package passed the actual Server 2022 tray-open upgrade and both
interactive uninstall choices in
[the guest evidence](P3-server2022-interactive-uninstall-2026-10-26.md).

The matching [PR run 40357729766](https://github.com/taskscape/PeerSync/actions/runs/40357729766)
failed before all nine producer jobs could execute any step: GitHub annotated
each with "an Actions budget is preventing further use" and skipped the
aggregate job. This is a complete local fallback, not hosted green CI, a
physical three-PC corpus, a WAN/NAT pass, an independent security review or
release approval.

## Earlier branch check at `9ee2c31`

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
