# Current PR head local verifier, 2026-10-28

The canonical `./build/Verify.ps1 -Runtime win-x64 -FuzzSeconds 600`
completed on tracked commit `a3a9ba50e0e5da10763c36abec8845c728efb0d3`.
The run used the existing isolated NuGet package cache at
`artifacts/field/nuget-overlay-aa9ae5c/packages-v2`; no verifier stage was
skipped. It started at 2026-10-28 00:06 UTC, finished at 00:20:49 UTC,
returned exit 0 and wrote a `passed` result with 863.487 seconds elapsed.

The Release solution build reported zero warnings and zero errors. All ten
current-date product TRX files passed: **1,836 passed, zero failed, zero
skipped**. The separate real-engine simulation TRX passed **17/17** methods;
its generated flat and hierarchical summaries passed 8/8 and 4/4 cases.
Native P0 probes, QUIC diagnostics and all six self-contained spike publishes
completed. The seven-target boundary fuzz ran 600.0029698 seconds across
202,426 deterministic seeds with zero crashes. The full verifier output is
`artifacts/field/current-head-20261028/verify-output.txt` (SHA-256
`3E4A73B43CB6ECF1EF5FB085F9FB2978AEA5608B8E0AD71833F173AB0B23D0B6`),
and the machine-readable result is `artifacts/verification-win-x64.json`.

[PR #49's matching hosted run](https://github.com/taskscape/PeerSync/actions/runs/40606070566)
failed before any of its nine producer jobs executed a step. GitHub annotated
each with “The job was not started because an Actions budget is preventing
further use.” The strict aggregate was skipped. Local success therefore does
not close the required hosted CI/merge gate.

The installed three-PC 1.0.41.0 physical soak is a separate run and was still
live when this verifier finished. Its hourly native-tree evidence, physical
LAN/WAN/NAT gates, independent security review and release signing remain
unverified by this local run.

## Reconnect readiness and later branch verification

The next canonical run on `ddabaed` stopped in the Net suite because its
shared TCP/UDP test asked Windows for a UDP ephemeral port that was inside a
TCP exclusion range. Commit `540c4dd` samples from 20000–49151 and verifies
both bindings; the focused test and complete Net suite passed. The following
full run on `540c4dd` reached Service 75/76. Its installed-host recovery test
observed a connected clock row just before the peer's folder transport became
active, so an immediate `ProductionSyncAvailable` assertion failed. A focused
rerun passed 1/1 in 10.479 seconds. The test now waits for both production
transports within the existing 30-second resume-recovery target. This retains
the readiness requirement and fails if either peer does not reconnect.

The unchanged canonical verifier then passed with that test source (SHA-256
`D5C9781CE2CCA5590BB7230117C8624FDB2B19CDAA2033D5D28D59104CF96B36`)
from 08:16:32 to 08:31:47 UTC. Locked restore and the Release solution build
completed with zero warnings/errors. The ten current-date product TRX files
passed **1,839/1,839**, with zero skips. The real-engine simulation passed
**17/17** methods, including generated 8/8 flat and 4/4 hierarchical cases.
Native probes and six self-contained spike publishes completed. Seven-target
fuzz ran 600.0005232 seconds over 188,212 seeds with zero crashes; its stderr
has no diagnostic content. `artifacts/verification-win-x64.json` records
`passed` and 912.7352561 seconds. The retained output is
`artifacts/field/current-head-20261028-reconnect-wait/verify-output.txt`
(SHA-256 `28A1E743B1CF9F43625E23B1A2A3AD2E3FB125BE2C36B7491F930ABE584F6C0E`),
with `run.json` recording the starting revision and test-file hash.

This is a local pass on the source later committed with the recovery assertion;
it does not replace hosted CI. [PR #49 run 40653985617](https://github.com/taskscape/PeerSync/actions/runs/40653985617)
was denied before all nine producer jobs started by the Actions budget. The
scheduled physical LAN soak and other physical, security-review and
release-signing gates remain separate.
