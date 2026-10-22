# P2 installed-host clock loopback verification

The installed-host regression uses two `InstalledFolderHost` instances with generated device certificates, registered folders, approval pins and real mutual-TLS QUIC sessions. The test injects an 11-minute offset into one host's `TimeProvider`; it does not change the Windows clock or machine certificate store.

With the offset active, both service runtimes report a skewed peer clock, the clock diagnostic is emitted, no file is applied, and `ProductionSyncAvailable` remains false. After correcting only the fixture clock, the managed peer reconnects, both status snapshots report an accepted clock, production availability becomes true, and the pending file is transferred. The regression passed in the complete Service test suite (27/27).

The latest canonical command `./build/Verify.ps1` completed successfully on 2026-10-22 for `win-x64`: 1,698 tests passed with no skips, all 14 real-engine simulation cases passed (8 flat and 4 hierarchical), native probes and deployment publishing checks passed, and the five-target fuzz run processed 174,998 seeds in 600.000 seconds with zero crashes. `artifacts/verification-win-x64.json` reports `status=passed`, `runtimeChecks=true`, and `gateP1Passed=false`. This local run does not certify hosted CI or Gate P2.

This is loopback evidence through the installed host runtime. Native Service Control Manager installation/account and stop checks, TCP service transport, default-client owner/PID verification against the installed service, real WPF rendering of these installed sessions, physical LAN throughput, three-PC convergence, the 72-hour sleep/resume and Wi-Fi soak, and hosted MITM/reflection tests remain open.
