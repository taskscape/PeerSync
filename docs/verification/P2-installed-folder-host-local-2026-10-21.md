# Installed folder host local verification — 2026-10-21

The installed service startup path now loads its configured machine device
certificate, derives the device identity, opens `%ProgramData%\PeerSync\state\peersync.db`,
and starts every unpaused registered folder with a protected key and per-folder
index. The service registers each folder's authenticated-session clock view,
starts startup/watcher/periodic scans, and requests coalesced scans after resume
or network-address changes. Shutdown cancels and drains scheduled and requested
scans before disposing folder transports, scanners, indexes, keys and state.
Diagnostics record certificate load, per-folder readiness/failure, scan outcomes
and host drain without paths, FolderIds, certificate thumbprints or key material.

`InstalledFolderHostTests.InstalledHostOpensRegisteredFolderAndDrainsItsInitialScanOnStop`
uses a temporary state database and folder, verifies the initial scan reaches
`scan.complete`, verifies production sync remains explicitly unavailable, and
disposes the host before removing its fixture.

The latest canonical local `./build/Verify.ps1` run passed on 2026-10-21:

- Build: zero warnings and errors.
- Unit tests: 1,666 passed, zero failed or skipped, including 25 Service tests.
- Simulation: 14 passed, including retained hierarchical seed `1390180802`.
- Native probes, QUIC diagnostics checks and win-x64 publish checks passed.
- Fuzzing: 600.0001762 seconds, 166,402 seeds, zero crashes.
- `artifacts/verification-win-x64.json`: `status=passed`, `phase=P1`,
  `gateP1Passed=false`, 661.262 seconds. Fuzzing completed 177,052 seeds.

This is local evidence only. An earlier full run in this turn failed the fixed
hierarchical case after 32 rounds; its isolated replay then passed. Stage-only
directory diagnostics were added around native parent/node operations. Two
subsequent canonical full runs passed, including this final run.

The installed service peer listener/dialer and authenticated folder supervision
have since been wired and locally exercised. See [service peer-runtime
verification](P2-service-peer-runtime-local-2026-10-21.md) for the two-host
loopback transfer and final local test totals. Installed SCM lifecycle,
service-account private-key access, TCP fallback, physical LAN/public reach,
15-second stop behavior, two-PC reconnect timing and production UI rendering
remain unverified.
