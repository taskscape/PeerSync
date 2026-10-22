# P2 current-source local verification — 2026-10-22

This report is for the dirty working tree at source revision `87a247fe5051009a20d6850d4ca526626bb7c724`. No hosted Actions result is claimed.

## Latest canonical verifier — 2026-10-22

The latest `./build/Verify.ps1 -Runtime win-x64 -FuzzSeconds 600` run passed
in 753.980 seconds. The Release build had zero warnings and errors; all
1,719 tests passed: Core 147, Crypto 15, Protocol 77, Net 48, Engine 1,030,
Storage 300, Control.Contracts 39, Service 34, App 14 and Simulation 15.
Native P0 probes, QUIC diagnostics, six spike publishes and 600 seconds of
five-target fuzz also passed; fuzz covered 183,392 seeds with zero crashes.
The machine-readable result is `artifacts/verification-win-x64.json`, and
the simulation evidence is
`artifacts/simulation/20261022T091200-0a274803c8ed468ea982681cd7e6fcb4`.
The matching self-contained deployment archive is
`artifacts/deploy/PeerSync-win-x64-20261022T092247Z-8a9b81c3.zip` (104,373,962
bytes; SHA-256 `6CBE61BD3685ED33D29388786993EA0FF24DF4247DF1A1A6C1241A5E4027E28E`).
Its manifest records the dirty source tree and keeps
`productionSyncAvailable=false` and `gateP2Passed=false`.

The run includes the retained hierarchical seed `1686969054`. Its earlier
failure showed that directory rebase causality only recognized a child at its
canonical path. The exact signed child had already been copied into a
completed `.case-conflict-*` tree, but the incomplete-preservations API omitted
that completed ledger entry. Storage now exposes completed copies for an
original member path; the engine accepts one only when the recorded source,
member hash, destination mapping, installed identity and current indexed
content all match. The seed passes and remains a retained regression. The WPF
integration test's port selector was also hardened to reject UDP ports that
Windows excludes for TCP; this resolved the intermittent TCP bind failure in
the full verifier.

The same run includes a transfer-throttle correction: permit slices and initial
burst capacity now scale with the configured rate, capped at a quarter second
of throughput (with a one-byte minimum for very low rates). This prevents a
fixed 64 KiB startup burst when the user configures a much lower bandwidth
limit. A new regression proves a 1,000 B/s limiter cannot immediately spend a
second 250-byte slice; the full Engine suite passes 1,030 tests.

This is local current-source verification only. Hosted Actions, an authenticated
session with the second PC, physical LAN discovery/throughput, actual sleep or
Wi-Fi recovery, NAT traversal and the remaining Gate P2 checks remain open.
The package manifest and plan gate remain `gateP2Passed=false`.

## Configured-host authenticated transport race — 2026-10-22

The installed host now retains a deduplicated set of authenticated LAN and
configured public endpoints per peer. It sends QUIC/TCP candidates through
`ConnectionManager.ConnectAnyAsync` in source order, with 250 ms staggering;
only authenticated sessions can win. A service regression proves a failed
LAN address falls through to a public address and synchronizes a file. A
manager regression verifies caller cancellation while a losing transport is
draining disposes both the late session and authenticated winner. Connected
configured hosts are not redialled, and maintainer creation is serialized
against duplicate discovery callbacks. The reconnect endpoint catalog refreshes
duplicate observations, retains the strongest source classification, expires
addresses after 15 minutes without observation, and caps each peer at 16 recent
endpoints. Expiry and cap diagnostics report counts only, without peer IDs or
addresses. Network recovery requests arriving during an active pass queue a
second pass instead of being lost, and a scan request arriving during folder
enumeration similarly queues another scan. Recovery start/completion and scan
events record their fixed trigger, coalesced count and elapsed time. An
installed-host integration regression sends the second network-change event
during the recovery debounce and proves the second pass reconnects both test
peers. Service tests cover route priority, expiration, bounded retention,
sanitized diagnostics and overlapping recovery signals. The canonical run
above includes these changes.
Punch/relay support and a deployed second-PC session remain open.

## Current-source package under the local SCM service — 2026-10-22

The latest package was installed into the existing local test installation
under `C:\Program Files\PeerSync`. Before replacement, the complete previous
package tree was moved to
`C:\PeerSync-Test\service-package-backup-20261022-112400-final`;
service settings, certificate, state database and synced test folder stayed in
their existing locations. SCM runs the package as `NT SERVICE\PeerSync`. The
packaged CLI reads live status (`productionSyncAvailable=false`, no peers), and
the service startup journal confirms QUIC and TCP listeners on configured port
`45973`. The CLI collected 166 events from nine logs into
`C:\PeerSync-Test\diagnostics-final-package-after-console.zip`; the 10-entry bundle retains
startup, scan, listener and control events. Review confirms the device
certificate thumbprint, test share path, install path and user profile path are
absent. This verifies the local package installation and diagnostics only; it
does not establish a remote peer session.

The same package was then run directly in the foreground with SCM stopped. It
loaded settings/certificate/state, scanned its registered folder and brought
up both listeners. The verified CLI correctly refused this non-SCM process.
Ctrl+C produced `service.stopped`, `discovery.lan_stopped` and
`service.peer_network_stopped`, with no cancellation-time
`discovery.lan_announce_failed` warning. SCM was restarted and the verified CLI
successfully read its new live instance. Full evidence is in
`docs/verification/P2-console-debug-local-2026-10-22.md`.

## Firewall allowance and second-PC reachability

After the user allowed PeerSync in Windows Firewall, the local service remains
Running with QUIC and TCP listeners on configured port `45973`. The local
machine's Tailscale address is `100.118.160.63`; its inbound PeerSync app rule
and peer-scoped test rules are enabled. The latest probe reached
`100.126.96.2` through Tailscale DERP in 26 ms, with no direct route; a bounded
TCP attempt to the remote port `45973` failed. Earlier TCP probes to remote
ports `45973` and `47821` also failed. The remote service's configured port is
still unknown, and the local `psp status` has no connected peers. The registered disposable local share
`C:\PeerSync-Test\Share` contains only the indexed `smoke.txt`; no remote join
or authenticated two-PC session has occurred. To test the user's new inbound
rule, run `Test-NetConnection 100.118.160.63 -Port 45973` on the remote PC once
its PeerSync service is running and read that machine's own `listenPort` from
`%ProgramData%\PeerSync\config\settings.json`.

## Earlier verification runs and investigations

`./build/Verify.ps1 -Runtime win-x64 -FuzzSeconds 600` restored and rebuilt the complete solution with zero warnings or errors. It passed Core 147/147, Crypto 15/15, Protocol 77/77, Net 39/39, Engine 1,029/1,029, Storage 300/300, Control.Contracts 39/39, and Service 28/28. App passed 11/14. Its three failures are in `ClockStatusRenderTests`; each reports a visible, loaded WPF window but captures a fully black 1100x480 `RenderTargetBitmap` (zero dark and white opaque pixels) before the test's content assertions. The failure artifacts are `artifacts/clock-ui/typed-AwaitingHello.png`, `runtime-skewed.png`, and `installed-runtime-skewed.png`. The first capture and successful same-host captures from earlier on 2026-10-22 were inspected: the earlier files contain rendered controls, while the current files are entirely black. No App code was changed based on these captures. The canonical script stopped at App, so its result is failed and it did not itself claim downstream stages.

To retain coverage after that stop, the remaining stages ran independently against the same built tree:

- Real-engine simulation passed 8 generated flat cases and 4 generated hierarchical cases. Reports: `artifacts/simulation/20261022T032808-24bcf9e288824019affb6ed6bd54a939/summary.json` and `hierarchical-summary.json`.
- The 100,000-operation watcher test passed with zero errors, recovered the tree after eight watcher overflows, and completed in 34.776 seconds. Report: `artifacts/p0/watcher.stdout.log`.
- Native P0 probes passed, including local QUIC IPv4/IPv6 transfer, QUIC/TCP source-port controls, MSQuic binding, control-pipe identity, and rendezvous HTTP/2 and HTTP/3. `build/Test-QuicDiagnostics.ps1` passed its malformed-input, delayed-start, transfer-timeout, progress-cadence, and error-metadata checks.
- All six self-contained diagnostic spike publications passed under `artifacts/publish/win-x64`.
- Five-target boundary fuzzing ran 600.001 seconds across 199,894 seeds with zero crashes. Report: `artifacts/fuzz/summary.log`.
- `build/Publish-Service.ps1 -Runtime win-x64` produced the current self-contained service/CLI package at `artifacts/deploy/PeerSync-win-x64-20261022T035247Z-d45a19ad.zip` (104,348,050 bytes; SHA-256 `2E0BBF7A39BC70A3571949525FE45A0F357F71DB30FC946979C144BD905685F5`). Its manifest records the dirty working tree and `gateP2Passed=false`; the packaged `psp.exe --help` smoke check passed.

The full-source build and all these local transport, service, simulation, native, publish, and fuzz stages pass. The three WPF render-capture failures remain an open validation issue; the cause has not been isolated. Physical two-PC discovery, the deployed service and production transport gates, hosted Actions, LAN throughput, NAT traversal, and the other external P2 criteria remain open. `gateP2Passed` and `gateP1Passed` remain false.

## Windows Server VM attempt

This was an environment setup attempt, not a PeerSync service test. The prebuilt Server 2025 evaluation VHDX booted and received a Hyper-V NAT address, but PowerShell Direct did not accept the available credentials. A clean Server 2025 ISO was then verified to contain an EFI boot catalog and the Desktop Experience image, but the Gen2 VM reported Hyper-V Worker event 18603 (`failed to boot an operating system`) with Secure Boot both enabled and disabled, including after moving the installer ISO to the first DVD slot. No PeerSync process ran in the clean VM. Windows Server deployment therefore remains unverified; this VM result is not a product failure and provides no Gate P2 evidence.

## Protocol violation strike supplemental check

After the verification above, protocol ingress failures were connected to the transport strike callbacks. Handshake decoding now validates inbound messages before use, records the strike against the TLS certificate-derived DeviceId, and attempts a bounded protocol-close frame. Established QUIC and TCP sessions record at most one protocol strike before closing; TCP now records the peer's received close reason in its diagnostics.

The current dirty working tree builds the full Release solution with zero warnings or errors. `PeerSync.Net.Tests` passes 42/42 and `PeerSync.Protocol.Tests` passes 77/77. Regressions cover a malformed Hello whose claimed DeviceId differs from the certificate identity, a malformed authenticated TCP control message, and QUIC failure reporting; the TCP/QUIC checks verify one strike and a `ProtocolViolation` close reason. This is focused verification after the earlier full verifier; it does not replace a full current-source verifier rerun or the open hosted and deployed-network gates.

The service package was then rebuilt from this source state at `artifacts/deploy/PeerSync-win-x64-20261022T042324Z-8307f5e8.zip` (104,349,409 bytes; SHA-256 `AC7467662C1E5F9E4E7888950C7B74FAD5D6DB98405B673A96C290091F3AD0A6`). Its CLI `--help` smoke check passed. The manifest now correctly reports both `productionSyncAvailable=false` and `gateP2Passed=false`; packaging alone does not claim an active peer session or a passed LAN gate.

## Unattended firewall setup supplemental check

The two-PC runbook now treats an unanswered Windows Firewall communication
prompt as an environment blocker rather than a product failure. The elevated
`build/Allow-PeerSync-TestTraffic.ps1` helper creates inbound/outbound TCP and
UDP rules scoped to the PeerSync service executable, peer address and configured
port, plus optional LocalSubnet discovery rules. It replaces only its own
named rules when rerun. PowerShell's parser reports no syntax errors. The
self-contained service publish includes the helper at
`tools/Allow-PeerSync-TestTraffic.ps1`; the packaged CLI `--help` smoke check
passed and the manifest still reports `productionSyncAvailable=false` and
`gateP2Passed=false`. Package SHA-256:
`BE77BED0A13868598954DF5ACA66ECC97EA9CB2BE5DA229B9A767AF577FCA5B3`.

The firewall rules were not applied in this verification because the helper is
intended to run elevated on each actual test PC before starting the service.
Physical two-PC validation remains open. If hosted Actions cannot start due to
budget, run the full canonical local verifier and keep hosted status separate.

## Current full local verifier rerun — 2026-10-22

`./build/Verify.ps1 -Runtime win-x64 -FuzzSeconds 600` rebuilt the full
solution with zero warnings or errors. Core (147), Crypto (15), Protocol (77),
Net (42), Engine (1,029), Storage (300), Control.Contracts (39), and Service
(28) passed: 1,677 tests total. App passed 11/14; its three failures are the
same `ClockStatusRenderTests` black-capture assertions. All three current
artifacts are 1100x480 black images despite visible, loaded windows and a
present presentation source; the current `typed-AwaitingHello.png` was opened
and confirmed uniformly black. The canonical script correctly stopped at App,
so `artifacts/verification-win-x64.json` was not refreshed and its older
`passed` value is not evidence for this run. No App product code was changed.

Downstream stages were then completed independently against the same build:

- `build/Run-Simulation.ps1 -Cases 8 -NoBuild` passed 14/14 tests, including
  eight flat cases and four hierarchical cases. Evidence:
  `artifacts/simulation/20261022T042949-01f2c06618c74b11b9ba82ca35278122`.
- `build/Run-P0.ps1` passed all local native probes, including QUIC IPv4/IPv6,
  source-port controls, MSQuic binding, control-pipe identity, rendezvous
  HTTP/2/HTTP/3, unreachable-endpoint handling, and watcher stress.
- `build/Test-QuicDiagnostics.ps1` passed delayed start, transfer deadline,
  progress cadence, error metadata, and argument validation.
- `build/Publish.ps1 -Runtime win-x64` published all six diagnostic spikes.
- Five-target fuzz ran 600.003 seconds across 214,177 seeds with zero crashes.

The final service bundle at
`artifacts/deploy/PeerSync-win-x64-20261022T045034Z-7a746028.zip` includes the
firewall helper at `tools/Allow-PeerSync-TestTraffic.ps1` and the runbook's
dedicated firewall-rule cleanup instructions; packaged CLI `--help` passed,
and the manifest remains `productionSyncAvailable=false` and
`gateP2Passed=false`. SHA-256:
`C843778544AAF5DF628B08B668F5DD98897F5895C97025E25630E493C6E11B11`.
The helper also passed a native Windows Firewall API smoke test: four
loopback-only direct rules plus two LocalSubnet discovery rules were created,
inspected, rerun without duplicates, and removed. No firewall dialog appeared.

This run confirms the service, protocol, engine, native and fuzz checks locally.
The current Windows logon session reports `Disc` in `quser`; the three black
captures were created after that session disconnected. Earlier same-day WPF
captures at 04:47 contain the expected controls. This points to the disconnected
desktop session as the render-capture limitation; it is not evidence of a UI
product defect, so no App code was changed. A fresh render in an active desktop
session remains the appropriate visual check. Physical two-PC LAN, hosted CI,
production service interoperability, and the remaining P2 external gates are
still open.

## Remote-PC administration preflight — 2026-10-22

The user-provided Tailscale peer `100.126.96.2` responded to `tailscale ping`,
including over a direct route. This confirms peer reachability only. A
read-only `Invoke-Command` preflight was rejected by WinRM because the target
was addressed by IP and this client has no HTTPS or TrustedHosts configuration;
a read-only `sc.exe \\100.126.96.2 query PeerSync` reached SCM but returned
`OpenSCManager FAILED 5 (Access Denied)`. The SSH port is closed. No
TrustedHosts, WinRM, SSH, service, or firewall settings were changed remotely.
This is an administration-access blocker for deploying the package on that
PC, not a PeerSync service failure or cross-network transport result.

Docker Desktop is installed, but `docker info` reports `OSType=linux`. The
current engine cannot run the Windows service image required for native SCM,
certificate-store, firewall, or WPF validation. No Docker engine or container
configuration was changed.

## Connection manager scheduling and reconnect regressions — 2026-10-22

The complete Net suite passes 44/44. One regression provides candidates in
reverse priority order and verifies attempts occur LAN, public, punch, relay,
with scheduled delays of 250, 500, and 750 ms. A second drives
`MaintainConnectionAsync` through repeated candidate requests, verifies the
first three 1/2/4-second backoffs and cancellation, while the static backoff
test checks the five-minute cap. This fills local coverage gaps for the P2.2.4
scheduling/retry acceptance; hosted CI and external punch or relay providers
remain open.

## Active-session canonical verification — 2026-10-22

After the Windows desktop session returned to `Active`, all three
`ClockStatusRenderTests` passed and captured usable WPF output. The earlier
black bitmap captures do not reproduce in the active session.

The canonical `./build/Verify.ps1 -Runtime win-x64 -FuzzSeconds 600` then
completed successfully on the current working tree in 684.248 seconds. Restore
and the full Release build passed with zero warnings or errors. Test results:
Core 147/147, Crypto 15/15, Protocol 77/77, Net 44/44, Engine 1,029/1,029,
Storage 300/300, Control.Contracts 39/39, Service 28/28, App 14/14, and
Simulation 14/14. The simulation included eight generated flat cases, four
generated hierarchical cases, and all seven retained regressions. All native
P0 probes passed, including IPv4/IPv6 QUIC, source-port controls, control-pipe
identity, rendezvous HTTP/2 and HTTP/3, unreachable-endpoint handling, and the
watcher stress check. QUIC diagnostic checks and all six spike publications
passed. Five-target boundary fuzz ran for 600.000 seconds across 197,061 seeds
with zero crashes. The authoritative summary is
`artifacts/verification-win-x64.json`; individual TRX files are under
`artifacts/tests/`, and fuzz output is in `artifacts/fuzz/summary.log`.

`build/Publish-Service.ps1 -Runtime win-x64` then produced a fresh
self-contained service/CLI archive at
`artifacts/deploy/PeerSync-win-x64-20261022T055153Z-6c7f08d3.zip` with SHA-256
`4CD4055E8DA96DBDA40E620B4BA37C4F9695327D96BC43D73BDA356AA385EBE8`. The
packaged `psp.exe --help` smoke check passed. Its manifest identifies the dirty
working tree and correctly leaves `productionSyncAvailable=false` and
`gateP2Passed=false`. No service is installed on this workstation; the package
has not been exercised between two installed machines.

The verifier reports its test phase as P1 and leaves `gateP1Passed=false`; this
is local current-source evidence, not hosted CI. Gate P2 still requires the
installed-service authenticated peer run, physical LAN throughput and corpus
tests, 72-hour soak, and hosted MITM/reflection CI.

## Firewall permission and hosted-CI check — 2026-10-22

After the user allowed PeerSync through Windows Firewall, the development host
initially showed enabled inbound allow rules for the development
`PeerSync.Service` executable, but no installed service. The Tailscale peer
`100.126.96.2` answers `tailscale ping`, while a TCP probe to `47821` times
out. That is the standalone QUIC spike port and does not represent the
randomly configured service port. A GitHub query found no open PR or workflow
run for `codex/p2-authenticated-transport`; hosted MITM/reflection CI remains
pending.

## Current package installed-service smoke check — 2026-10-22

The current package was installed under `C:\Program Files\PeerSync` on this
otherwise clean host. `--init` created the machine device certificate and
settings with configured port 45973. The SCM reports `PeerSync` running as
`NT SERVICE\PeerSync`; the owner/PID-verified `psp.exe status --json` returned
the live instance. After adding a disposable `C:\PeerSync-Test\Share`, the
service emitted both `service.quic_listener_ready` and
`service.tcp_listener_ready` and bound UDP and TCP on port 45973. Four
peer-scoped firewall rules were installed for `100.126.96.2` on that port.
The remote peer's TCP connection to 45973 timed out, so this proves local
installed-service startup and listener readiness only; it is not an
authenticated cross-PC session. The service remains installed and running,
with `productionSyncAvailable=false` and no connected peers, ready for the
other test PC to join the disposable folder using its share key. A disposable
`smoke.txt` was created afterward and appeared in `psp dump-index`, confirming
the live service can scan and publish a file from the granted test tree. The
installed `psp diagnostics` command collected 24 recent events into
`C:\PeerSync-Test\diagnostics-installed-service.zip`; review found the listener,
scan, control and watcher events, with no test-share path or certificate
thumbprint in the event log.

An additional native SCM stop/start check measured a 296.8 ms stop and 656.2 ms
from issuing `Start-Service` until SCM reported `Running`. The service returned
to the same configured port, with one TCP listener and one QUIC/UDP listener;
the owner/PID-verified CLI returned status from the new live instance. It
reported no connected peers and `productionSyncAvailable=false`. A fresh
diagnostic bundle at `C:\PeerSync-Test\diagnostics-after-restart.zip` contains
the `service.stopped` event followed by startup, folder-engine, listener,
discovery, control and scan readiness events. This demonstrates local service
lifecycle and diagnostics only; it does not establish a remote authenticated
session.

## Dynamic predefined-host configuration and current package — 2026-10-22

The installed folder runtime previously had no supported way to populate its
`PredefinedHosts` after `add` or `join`. A new `psp hosts <folder>
<host:port...>` command now replaces the list through the owner/PID-verified
control pipe; `--clear` removes it. The service validates the bounded endpoint
list, persists it with the caller SID audit entry, updates the live folder
runtime, and immediately resolves and dials configured hosts. The resolver
also starts when the list is empty, allowing endpoints to be added while the
service is running.

The focused regression proves an endpoint added at runtime initiates an
authenticated service-host QUIC session and syncs a file. Real-pipe tests cover
persisted settings, invalid endpoint rejection without changing the prior
value, clearing endpoints, and unauthorized caller rejection. The CLI and
service suites pass 6/6 and 29/29 respectively. Roslyn compiler and configured
analyzer diagnostics report zero warnings/errors, with no diagnostics
introduced relative to the pre-change baseline.

The canonical `./build/Verify.ps1 -Runtime win-x64 -FuzzSeconds 600` run passed
in 743.650 seconds: 1,708 tests (Core 147, Crypto 15, Protocol 77, Net 44,
Engine 1,029, Storage 300, Control.Contracts 39, Service 29, App 14 and
Simulation 14), zero build warnings/errors, native P0 probes, QUIC diagnostics,
six spike publishes, and 600.001 seconds of fuzz across 192,235 seeds with
zero crashes. The run summary is `artifacts/verification-win-x64.json`; fuzz
evidence is `artifacts/fuzz/summary.log`; simulation evidence is
`artifacts/simulation/20261022T060859-f565d24527274a2db9d38eaa090e0b06`.

The current package is
`artifacts/deploy/PeerSync-win-x64-20261022T061941Z-8ff3e93b.zip` (SHA-256
`4FCCCD8983A3117619F703F64D27B0AA47541AE40DA58E66529F83519D8AF925`). Its
manifest keeps `gateP2Passed=false` and `productionSyncAvailable=false`. It
was installed over the prior local test package while retaining a binary
backup at `C:\PeerSync-Test\service-package-backup-20261022`; ProgramData
settings, identity and state and the disposable test folder were preserved.
SCM returned to Running as `NT SERVICE\PeerSync`, the new CLI read the live
service, `psp hosts ... --clear` completed over the pipe, and the existing
test file remained in the index. The sanitized
`C:\PeerSync-Test\diagnostics-after-hosts-cli.zip` contains 94 events from four
logs, including listener readiness and `control.predefined_hosts_updated`; it
omits endpoint addresses and sensitive fields.

No second-machine PeerSync listener or configured port is confirmed, and no
cross-PC authenticated session is claimed. The earlier raw TCP probe to
`100.126.96.2:45973` timed out; the remote service/firewall must be configured
with its actual PeerSync listening port before the new `psp hosts` command can
exercise routed service transport. Hosted Actions, physical LAN throughput,
multi-PC corpus and soak checks remain open.

After the user allowed PeerSync communication in Windows, the four local
peer-scoped TCP/UDP rules remained enabled for `100.126.96.2`. The remote
Tailscale peer answered `tailscale ping` in 4 ms, confirming overlay
reachability, while a fresh TCP connect attempt to port `45973` still timed
out. That is the local service's port; the remote service's configured port
has not yet been confirmed. The local service is listening; the remote machine
must have PeerSync installed and running, with a matching inbound firewall
rule on its actual configured port, before a two-PC authenticated session can
be tested.

## Network recovery rescan deadline regression — 2026-10-22

The installed-host TCP recovery integration separately triggers a runtime
network-change event and a resume event. After each signal, both authenticated
peer sessions must return and the affected folder must emit
`service.resume_scan_complete`, all inside that event's 30-second deadline.
The focused integration test passed in 7.4 seconds, and all 29
`PeerSync.Service.Tests` passed in 9.1 seconds. This verifies the simulated
recovery paths and scan completion; actual Windows sleep/resume, Wi-Fi
switching and deployed second-PC recovery remain open.

## Hosted CI and Windows test-host availability — 2026-10-22

GitHub Actions is accepting work again: all five checks on PR #43 passed,
including Windows x64/ARM64 builds, both Windows service-security jobs and
fuzz smoke ([run 39613791173](https://github.com/taskscape/PeerSync/actions/runs/39613791173)).
That PR targets a different branch. The current `codex/p2-authenticated-transport`
branch has no PR and no hosted workflow run, so PR #43's checks do not validate
the dirty current-source tree or satisfy its hosted gate. This workstation is
Windows 11 with a Linux-only Docker engine and no configured Hyper-V VMs; it
cannot provide a Windows Server container/VM for the cross-version service
test. Local Windows verification remains available, while hosted current-source
CI and the second installed PC remain open.

## Native installed-peer probe and latest full verifier — 2026-10-22

A source-checkout probe created a disposable authenticated peer and connected
it to the fixed SCM `PeerSync` service through the actual listener. The default
owner/kernel-PID verified control client saw an injected 11-minute peer-clock
skew block with production availability false, then accepted recovery and a
SHA-256-verified copy of the existing 42-byte test file after the peer clock
was corrected. Disconnect returned availability to false. Two pre-package
probe runs passed, and the final wrapper was also rerun after the package
replacement below. The probe uses an ephemeral certificate/state/share and
removes its loopback-only firewall rules. Detailed native evidence is in
`docs/verification/P2-installed-service-peer-native-2026-10-22.md`.

The canonical `./build/Verify.ps1 -Runtime win-x64 -FuzzSeconds 600` passed
on the batched source state in 809.961 seconds with zero build warnings/errors.
All 1,719 tests passed: Core 147, Crypto 15, Protocol 77, Net 48, Engine
1,030, Storage 300, Control.Contracts 39, Service 34, App 14 and Simulation
15. Simulation included eight generated flat and four hierarchical cases;
the run directory is
`artifacts/simulation/20261022T093548-bee994a484ef413993ac445a90095353`.
Native P0 probes, QUIC diagnostics and six self-contained spike publishes
passed. Five-target fuzz ran 600.001 seconds over 191,167 seeds with zero
crashes. The authoritative summary is `artifacts/verification-win-x64.json`.

`build/Publish-Service.ps1 -Runtime win-x64` created
`artifacts/deploy/PeerSync-win-x64-20261022T094722Z-0f777d3a.zip`, SHA-256
`A9AFAE251D7E31C4D628C3593F1159BE416597B310AE663A320DD52904912C67`.
The prior local binary tree is retained at
`C:\PeerSync-Test\service-package-backup-20261022-114744`. The new package
started under SCM as `NT SERVICE\PeerSync`, and its default verified CLI read
the live service with no peers and production availability false. The native
peer wrapper passed again against this exact installed package in 1.722
seconds. `psp diagnostics` then collected 422 recent events from nine logs
into `C:\PeerSync-Test\diagnostics-installed-peer-current.zip`; its 10 entries
include clock, peer-ready and sync-pass events but omit the known certificate
thumbprint, share and installation paths and folder-key field. The probe's
firewall rules were removed and SCM remained Running.

The optional active-desktop `-RenderUi` run then used the actual WPF clock
window/controller with the default fixed-service client. It passed the same
authenticated skew/recovery/transfer/disconnect assertions and captured
rendered controls for each state under `artifacts/clock-ui/native-scm-*.png`.
The skew capture showed `+11.00` and exchange blocked; recovery showed `0.00`,
Clock accepted and production available; disconnect showed no peer sessions
and production unavailable. All three captures were visually inspected. A
small App copy correction after this run changes the unavailable label from
"not available in this build" to "not available right now". All 14 focused
App tests passed after the change, and the optional UI probe passed again
against the same installed service in 3.765 seconds. The disconnected capture
was re-inspected with the corrected wording. The App-only copy/probe change
does not alter the installed service package or justify repeating the timed
600-second fuzz check.

The fixed-service authenticated session and its rendered WPF status are now
locally verified. A second installed PC, physical
LAN discovery/throughput/corpus, Windows Server interoperability, the 72-hour
soak and current-source hosted CI remain open. Gate P2 is not passed.
