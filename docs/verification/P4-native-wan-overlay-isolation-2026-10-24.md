# Prepared physical WAN check with Tailscale kept available — 2026-10-24

The third PC needs Tailscale for management and Taildrop. Its matching
`outside-pc-20261024.txt` hash therefore proves cross-PC file bytes but not
which network carried them. The next check must prevent **PeerSync itself**
from using the Tailscale adapter while leaving the Tailscale client online.

`build/field/Test-NativeWanWithoutOverlay.ps1` is a bounded Windows
PowerShell 5.1 field test for that purpose. It requires an elevated shell,
the joined folder, the installed PeerSync service, an Up physical uplink
adapter and an Up Tailscale adapter. It first verifies that the route to
`sync.taskscape.com` uses the requested physical uplink. It then installs two temporary Windows
Firewall **block** rules for the PeerSync service executable on the Tailscale
interface only, stops PeerSync, waits for its old process and bound sockets
to exit, and starts it again to close pre-existing overlay sockets,
creates a uniquely named text file in the joined folder, and leaves the
rules in place for five minutes. It writes a JSON report with the file name,
SHA-256 and UTC test window. A `finally` block removes the rules and
restores the service; an independent SYSTEM scheduled task removes the rules
three minutes after the planned test end if the shell is killed. No Tailscale
service or adapter setting is changed. The operator should leave the third
PC on its mobile hotspot throughout the interval.

On the third PC, keep `Run-NativeWanWithoutOverlay.cmd` beside
`Test-NativeWanWithoutOverlay.ps1` in Downloads, then right-click the CMD
file and choose **Run as administrator**. The launcher supplies the joined
folder path and keeps the window open for the result. The initial pasted
PowerShell command on the third PC had an unclosed quote: the `>>` prompt
was PowerShell waiting for more input, so the test did not start and no
firewall rules were installed.

The script prints `READY` with the generated file name and hash as soon as
the isolation window begins, then `CLEANED` and `REPORT` after cleanup.
The first PC must observe that exact file and verify its native SHA-256
**before** the reported interval ends. Its service journal and the third PC's
diagnostics should then identify the selected authenticated transport.
An arrival only after cleanup cannot close the native WAN gate. The test
does not by itself classify the mobile carrier's NAT type, validate the
whole P0.2.2 matrix, enforce a relay-only path, or measure provider-side
relay rate caps. Those require separate retained evidence.

The first third-PC launcher run reached preflight with installed service
version 1.0.23.0, Tailscale Up and the rendezvous route over physical Wi-Fi.
The immediate `Restart-Service` failed before `READY`; its report showed
`rulesRemoved=True`, `recoveryTaskRemoved=True`, no test file and exit 1.
The returned startup journal identifies `SocketException` 10048 (address
already in use). The follow-up inspection found the service Running with no
test rules remaining. No native-WAN transfer was measured.

Windows Remoting to the second physical PC reproduced that failure with the
same 1.0.23.0 service, using Ethernet as the physical uplink. An ordinary
restart without block rules first succeeded. In the failed test, the old
process logged `service.stopped` at 19:46:17.199Z, the new process logged
`service.start_failed`/10048 at 19:46:17.675Z, and the old process logged
`service.peer_network_stopped` only at 19:46:19.032Z. SCM's Stopped state
preceded release of the old QUIC/TCP listeners. The test script now waits
for the old process to exit before starting another, both at setup and
cleanup; its independent recovery task uses the same process-exit wait.

The corrected script passed a **60-second physical LAN control** on the
second PC (`192.168.0.183`, Ethernet 1 Gbps) while PeerSync alone was blocked
on Tailscale. It printed `READY` at 19:48:16.994Z. The first PC received
`native-wan-second-lan-20261024T194816Z-75e28a0470b5.txt` while the rules
were still active and verified SHA-256
`99130FF9D36A8B016E2DF2CA0733DE5D412F8EC53DDA8CFF4840AD34ADEFE1AC`.
Its journal selected authenticated QUIC to `192.168.0.183:26328` and opened
a block stream immediately before the file appeared. The second-PC report
records `rulesPresentAtEnd=True`, `rulesRemoved=True`,
`recoveryTaskRemoved=True`, `serviceRunningAfterCleanup=True` and no failure;
the service is Running and no test rule/task remains. Retained reports and
journals are under `artifacts/field/second-pc-overlay-isolation-20261024/`.
Windows PowerShell 5.1 parsed the revised script; its SHA-256 is
`FAEA7A4F88DAFDF9E1890B3CA9658173F3B1C16FF9031EFCC27FE37D72067E6A`.
The revised script and its matching `Run-NativeWanWithoutOverlay-v2.cmd`
launcher were sent to the third PC by Taildrop as distinct v2 filenames to
avoid Windows renaming an existing download and accidentally rerunning v1.

This confirms the isolation mechanism and direct physical-LAN transfer, not
the required native mobile-WAN path. The corrected five-minute third-PC run
and first-PC observation are still pending. The user operates that PC.
