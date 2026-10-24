# Prepared physical WAN check with Tailscale kept available — 2026-10-24

The third PC needs Tailscale for management and Taildrop. Its matching
`outside-pc-20261024.txt` hash therefore proves cross-PC file bytes but not
which network carried them. The next check must prevent **PeerSync itself**
from using the Tailscale adapter while leaving the Tailscale client online.

`build/field/Test-NativeWanWithoutOverlay.ps1` is a bounded Windows
PowerShell 5.1 field test for that purpose. It requires an elevated shell,
the joined folder, the installed PeerSync service, an Up physical Wi-Fi
adapter and an Up Tailscale adapter. It first verifies that the route to
`sync.taskscape.com` uses Wi-Fi. It then installs two temporary Windows
Firewall **block** rules for the PeerSync service executable on the Tailscale
interface only, restarts PeerSync to close pre-existing overlay sockets,
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

Preparation checks on the first PC: Windows PowerShell 5.1 parsed the script
without errors; `-PreflightOnly` found the installed 1.0.23.0 service,
Tailscale Up and an Ethernet route to the server. A disabled, temporary
program-and-interface-scoped firewall rule and a temporary SYSTEM scheduled
task both registered and were removed successfully. The live five-minute
mobile-PC test has **not** run yet. The script's SHA-256 is
`F532E12841C445C849AD1BA0C8F84D3FD0759BCBA314A287B9003AA789383363`;
Taildrop to the third PC returned exit 0, but recipient execution is pending.
