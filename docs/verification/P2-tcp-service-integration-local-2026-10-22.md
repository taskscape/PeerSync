# Authenticated TCP service integration — local verification, 2026-10-22

The installed peer runtime now starts TCP/TLS and QUIC listeners on the
configured numeric port. Both transports use the shared certificate-bound
Hello and folder-proof handshake and are registered through the same
`ConnectionManager`. LAN and predefined-host connection attempts include TCP
after QUIC; authenticated TCP sessions use the same folder authorization,
clock admission, index synchronization, pull passes, status projection and
shutdown supervision as QUIC.

The TCP control channel correlates concurrent index-summary and index-page
requests across folders. Authenticated peers can push index targets. Block
requests use ticket-attached TCP data connections, bounded to three concurrent
lanes per session, and validate the returned content hash and length.

## Local verification

On 2026-10-22, `PeerSync.Net.Tests` passed all 38 tests and
`PeerSync.Service.Tests` passed all 28 tests. The focused
`TcpPeerProtocolSessionTests` covers mutual TLS and shared folder authentication,
session identity and endpoints, concurrent index requests for multiple folders,
index paging, content-addressed block transfer in both directions, and
bidirectional index-target notifications.

`InstalledFolderHostTests.InstalledNetworkRuntimeAuthenticatesAndSynchronizesAnApprovedPeer`
was switched to explicitly connect over TCP. It starts two installed-host
instances with separate state and folder indexes, transfers a small file and a
512 KiB file, verifies the TCP transport appears in diagnostics, checks
download throttling, and confirms production sync availability follows the
authenticated session lifecycle. It then raises the installed host's network
change event, verifies the active session is reset, confirms both peers
reconnect with a new authenticated session and production availability returns
in under 30 seconds. Service-level folder-ready, sync-pass and sync-failure
events carry the shared authenticated session ID. The test asserts that both
peers' sync-pass events have at least one identical ID, so operators can join
the two diagnostic bundles without relying on local process or folder-operation
IDs. The installed clock-skew and recovery test continues to exercise the QUIC
service path. The test port allocator now verifies that each selected port is
free for both UDP and TCP before launching the installed-host loopbacks.

The Release Service build completed with zero warnings and errors. After
adding shared session IDs to service sync diagnostics, the service and CLI
were republished with `build/Publish-Service.ps1 -Runtime win-x64`. The latest
self-contained archive is now
`artifacts/deploy/PeerSync-win-x64-20261022T032234Z-cc3fc230.zip` (99.5 MiB,
SHA-256 `55C5308FC4BFEF6C295573FECE53949593F88AD6CFA0A9E9A5B2D00F8C737D0B`).
The packaged CLI `--help` and sanitized diagnostics bundle commands ran
successfully, and the archive contains the service, CLI, runbook, diagnostics
reference and provenance manifest. All ten PowerShell blocks in the runbook
parse; the firewall rules now precede service startup and scope peer traffic
to the other test PC and discovery to the local subnet. The
manifest records `workingTreeDirty=true` and `gateP2Passed=false`, so this is a
controlled test bundle tied to source revision
`87a247fe5051009a20d6850d4ca526626bb7c724`, not a release artifact.
This latest bundle also contains the network-change session reset and
immediate known-peer redial. Its packaged CLI help ran successfully.
The packaged `psp diagnostics` command also produced a bundle from one current
structured event; a fixture containing unrecognized `folderId` and `path`
fields confirmed that the archive retained only allowlisted fields and omitted
the network address by default. An initial check against old CI artifacts was
correctly rejected because all their events had expired from the 24-hour
collection window.

The
canonical `build/Verify.ps1` then passed against current source in 710.146
seconds: all 1,687 unit tests passed, all 14 simulation cases passed (8 flat,
4 hierarchical, and the retained regression set), native transport/control
probes and win-x64 publish checks passed, and the 600-second fuzz run completed
194,825 seeds with zero crashes. The generated
`artifacts/verification-win-x64.json` reports `status=passed` and
`gateP1Passed=false`. This does not count as hosted Actions or replace the
planned Windows Server deployment, physical LAN throughput, multi-PC
convergence, resume/Wi-Fi soak, or external public/NAT checks.

After the TCP session-ownership fix and the test's folder-ID-based ordering
correction, a canonical verifier run rebuilt cleanly and passed the Core,
Crypto, Protocol, Net, Engine, Storage, Control.Contracts and Service suites
(1,673 tests). It then failed in three existing WPF render cases because
their off-screen `RenderTargetBitmap` produced no opaque pixels at all; the
remaining 11 App tests passed. A standalone rerun reproduced the assertion and
saved a transparent-black render artifact at
`artifacts/clock-ui/typed-AwaitingHello.png`. A synthetic white `DrawingVisual`
rendered through the same `RenderTargetBitmap` also yielded 0/256 opaque pixels,
while the tested window reported visible/loaded and had a live `HwndSource`.
This isolates the failure to the local WPF rendering pipeline, before app
content is painted; the original pixel assertions remain active.
Simulation and fuzz stages therefore did not run on this latest source. The
earlier complete pass above predates those two small follow-up changes and is
not a replacement for a final clean verifier run. Windows desktop capture also
failed twice with `CreateForMonitor ... 0x80070057`, so the current monitor or
secure-desktop state could not be inspected. The render assertions remain
enabled pending a valid desktop/render environment. After the later network
recovery change, the full Service suite passed all 28 tests, including the
under-30-second reconnect regression; the full canonical verifier has not yet
been rerun on that latest source.
