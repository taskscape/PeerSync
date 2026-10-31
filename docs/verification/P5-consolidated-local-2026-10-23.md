# P5 consolidated local verification, 2026-10-23

Source revision `442a3fb89cc718ad1994b6120be0de23e9ebc634` on
`codex/p5-hardening` passed one complete local `build/Verify.ps1 -SkipFuzz`
run on the Windows x64 development host. The run took 1,070.235 seconds and
wrote `artifacts/verification-win-x64.json`. Locked restore and the Release
solution build passed with zero warnings and errors. The ten test projects
passed 1,785/1,785 tests, zero failed or skipped:

| Core | Crypto | Protocol | Net | Engine | Storage | Control.Contracts | Service | App | Rendezvous |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 151 | 15 | 77 | 57 | 1,045 | 305 | 39 | 58 | 21 | 17 |

The simulation runner passed all 17 methods, including retained and generated
cases. Its generated flat 8/8, hierarchical 4/4 and connected-maintenance
result are in `artifacts/simulation/20261023T065454-3b0d99e3c7f14149b57d9ea43f99b9ff`.
The local native P0 probes passed, including QUIC IPv4/IPv6, rendezvous HTTP/2
and HTTP/3, the 1 GiB loopback transfer and 100,000 watcher operations;
`artifacts/p0/summary.json` explicitly limits this to one host. The QUIC
timeout/diagnostic controls passed. Six isolated self-contained diagnostic
probes were published to `artifacts/publish/win-x64`, whose manifest records
the source revision. The pre-existing untracked `Sync.zip.tmp` makes the
manifest's broad `workingTreeDirty` flag true; no tracked source edit was in
the verifier input.

The updated `build/Test-UpdateRestartRecovery.ps1` passed under Windows
PowerShell 5.1, including post-reboot receipt checks and safe nonrecursive
slot cleanup. The separate `build/Run-InstallerCi.ps1` passed after the
verifier: timestamped development signatures, online-chain and wrong-pin
checks, x64 and Arm64 client installers and the x64 rendezvous installer.
Its packages and manifests are in `artifacts/installer`. This is a
development-signing/package result, not a release-signer or clean-machine
upgrade result.

The verifier omitted its separate ten-minute fuzz smoke because a 24-hour
five-target campaign started at 2026-10-22 21:53:23 UTC from an immutable
binary in `artifacts/fuzz/p5-24h-20261022T215322Z`. That campaign's terminal
result remains open. The current-source million-file fresh-peer run is also
open; running its scale measurement under the active fuzz load would confound
the performance result. Neither physical LAN/NAT tests, installed Server
2022/2025 recovery, 72-hour soaks, nor independent security review can be
claimed from this single-host run.

[PR #49](https://github.com/taskscape/PeerSync/pull/49) remains a draft.
[Its CI run 39914027406](https://github.com/taskscape/PeerSync/actions/runs/39914027406)
was denied before any job step started by the GitHub Actions budget, including
all nine producer jobs; the aggregate was skipped. The local pass is retained
as fallback evidence, not represented as a green hosted workflow or a P5 gate.
