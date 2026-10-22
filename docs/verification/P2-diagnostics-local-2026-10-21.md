# P2 deployment diagnostics: local verification

The current worktree adds a sanitized `psp diagnostics` command and initial
service lifecycle events. The command accepts an explicit log directory and
new output path, filters structured events to 24 hours, copies only allowlisted
fields, excludes network addresses unless requested, and refuses empty or
overlapping output. Service startup, status-only mode, control readiness and
normal shutdown emit structured events. The support runbook now distinguishes
the sanitized CLI bundle from the legacy development collector.

## Local verification

On 2026-10-21, `./build/Verify.ps1` completed successfully on Windows x64 with
the default scope in 677.8646527 seconds and a 600-second fuzz run. Locked
restore, Release build,
diagnostic collector probes, control-pipe readiness, all canonical test
projects, all 14 simulation cases, native probes and probe publishing passed. The fuzz
runner completed 600.0004276 seconds over five targets with 190095 seeds and
zero crashes.

| Canonical test project | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Core | 147 | 0 | 0 |
| Crypto | 15 | 0 | 0 |
| Protocol | 77 | 0 | 0 |
| Net | 37 | 0 | 0 |
| Engine | 1013 | 0 | 0 |
| Storage | 300 | 0 | 0 |
| Control.Contracts | 39 | 0 | 0 |
| Service | 22 | 0 | 0 |
| App | 13 | 0 | 0 |
| **Total** | **1663** | **0** | **0** |

The run wrote `artifacts/verification-win-x64.json`, canonical TRX files under
`artifacts/tests/`, simulation evidence under
`artifacts/simulation/20261021T191522-1e49bb9f840f4633a1998f33558e5e8e/`,
and published P0 probes under `artifacts/publish/win-x64/`. The first
canonical locked restore attempt failed because the new Core/Crypto project
references changed dependency graphs. `dotnet restore PeerSync.slnx
--force-evaluate` refreshed the affected lock files; the canonical locked
restore then passed.

The runner metadata still says `gateP1Passed=false`; this is full local
regression evidence for the worktree, not P1 campaign acceptance or hosted
Actions evidence.

## Scope still open

This is local evidence for the current working tree. It does not establish a
hosted Actions result or deployed Windows Server behavior. P2.5.6 remains
incomplete: configuration recovery, certificate loading, discovery and retry,
index/block verification, apply/archive and filesystem failure events still
need production hooks, and the two-PC runbook still needs validation.
P2.2.1/P2.2.3 installed-service integration and physical LAN checks also
remain separate gates. This run was executed locally; hosted GitHub Actions
evidence is still separate.
