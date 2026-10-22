# Installed peer runtime local verification — 2026-10-21

The installed folder host now starts a mutual-TLS QUIC listener on the configured
device port. It accepts incoming sessions, registers them with the connection
manager and dials LAN-discovered or configured endpoints with reconnect backoff.
LAN announcements include only folders with LAN enabled. Folder admission checks
the local device row and `RequireApproval`: blocked/unpinned peers are denied,
approved pinned peers are allowed, unknown peers become pending when approval is
required, and otherwise the shared folder key permits admission. The runtime
repeatedly resumes index exchange and runs the puller for each authenticated
folder. Control status reports production sync available only while at least one
supervised folder session is active.

`InstalledFolderHostTests.InstalledNetworkRuntimeAuthenticatesAndSynchronizesAnApprovedPeer`
starts two service hosts over separate temporary state databases and indexes,
authenticates generated certificate identities, connects over loopback QUIC,
and verifies a file is applied at the receiving folder. It also verifies both
hosts report production availability while the authenticated folder session is
active. This exposed and fixed a startup ordering defect: the QUIC folder stream
previously requested an index summary before the clock Hello, causing a correct
`AwaitingHello` rejection. The stream now exchanges Hello before any clock-gated
index request. A folder Hello now includes the required device and client names.

Local validation on 2026-10-21:

- Service tests: 25 passed, zero failed or skipped, including the two-host
  authenticated file transfer.
- Net tests: 37 passed, zero failed or skipped. The certificate-pin rejection
  test accepts either TLS authentication or QUIC connection failure from the
  server side while still requiring the client authentication failure.
- Release win-x64 Service build: zero warnings and errors.
- Canonical `./build/Verify.ps1`: passed; 1,666 unit tests
  passed across nine suites, 14 simulation cases passed (8 flat and 4
  hierarchical), native probes and win-x64 publish checks passed, and the
  600-second fuzz campaign completed 177,052 seeds with zero crashes; the
  runner artifact reports 661.262 seconds.
- CLI suite: 5 passed, zero failed or skipped, including both new status
  format tests; the CLI tests are run separately from the canonical solution
  test list.
- `artifacts/verification-win-x64.json`: `status=passed`, `runtime=win-x64`,
  `gateP1Passed=false`. This local run does not count as hosted CI or close a
  campaign date.

This is a local in-process Windows loopback test. It does not establish Windows
Service Control Manager behavior, service-account private-key access, TCP
fallback, physical LAN discovery, public/NAT reachability, Wi-Fi/resume timing,
the installed stop deadline or the rendered production UI. Those requirements
remain open at P2.5 and Gate P2.
