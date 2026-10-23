# Rendezvous server deployment and diagnosis

This is the P4 test procedure for a Windows Server central component and two
PeerSync PCs. It does not close the physical NAT, WAN or 72-hour soak gates in
`plan.md`. The central server carries presence and, when direct transport
fails, opaque end-to-end TLS relay traffic. It does not receive folder keys or
plaintext files.

## Prepare the central server

Use Windows Server 2022 with current cumulative updates or Server 2025. Point
the `sync.taskscape.com` A record at this server and add AAAA only if the
server has working public IPv6. Before installing, confirm port 99 is free:

```powershell
Get-NetTCPConnection -LocalPort 99 -ErrorAction SilentlyContinue
Get-NetUDPEndpoint -LocalPort 99 -ErrorAction SilentlyContinue
netsh http show urlacl | Select-String ':99/'
```

Obtain a Server Authentication TLS certificate for `sync.taskscape.com` in
`Cert:\LocalMachine\My`, with a private key. For a public deployment, configure
win-acme with DNS or HTTP validation and Windows certificate-store installation.
Set its post-renewal command to
`C:\ProgramData\PeerSync.Rendezvous\After-RendezvousRenewal.ps1`; the script
regrants the private-key ACL and restarts `PeerSyncRendezvous`. A self-signed
server certificate is suitable only for an isolated test where each client
explicitly trusts it. It is separate from the P-256 device certificates and
the code-signing certificate.

Build the x64 setup from the repository on the development PC. A test identity
in `LocalMachine\My` can be passed by thumbprint; `-DevelopmentSigning` exports
its public `.cer` next to the installer. Production release signing needs a
publicly trusted code-signing identity.

```powershell
./build/Build-RendezvousInstaller.ps1 -Version 1.0.0 `
  -CertificateThumbprint '<code-signing-thumbprint>' -DevelopmentSigning
```

Copy the resulting `artifacts\installer\PeerSync-Rendezvous-Setup-*.exe` to the
server and run it elevated. The setup checks the server certificate before
stopping an existing service. It installs under `NT SERVICE\PeerSyncRendezvous`,
grants that identity private-key access and Modify permission only on the
`%ProgramData%\PeerSync.Rendezvous\logs` directory, creates its
Application Event Log source, and adds inbound TCP/UDP 99 firewall rules.
It preserves the installed `appsettings.json` on upgrade. Run the newer setup
over the old version to upgrade; do not delete the data directory to upgrade.
The service identity must not be able to edit the administrator-run renewal
scripts in the parent data directory. Setup removes that inherited grant when
upgrading an earlier development installation.

Setup also excludes `PeerSync.Rendezvous.exe` from automatic machine-wide
Windows Error Reporting, preserving a pre-existing administrator exclusion
and removing only a setup-owned entry at uninstall. Run the packaged read-only
audit after installation or upgrade:

```powershell
& "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive `
    -ExecutionPolicy Bypass -File 'C:\Program Files\PeerSync Rendezvous\Audit-CrashPolicy.ps1' `
    -Component Rendezvous
```

Require `allAutomaticReportsExcluded: true`. Review the global and
per-executable `LocalDumps` flags before public deployment; those
administrator-configured dumps operate independently of the automatic-report
exclusion. Protect any retained dump as privileged secret material. The
[approved dump boundary](proposals/P5-crash-dump-boundary.md) also applies to
this server process.

```powershell
Get-Service PeerSyncRendezvous
Get-NetTCPConnection -State Listen -LocalPort 99
Get-NetUDPEndpoint -LocalPort 99
Get-NetFirewallRule -DisplayName 'PeerSync Rendezvous*'
```

For one machine-readable server preflight, run the script packaged
with Setup from an elevated Windows PowerShell session:

```powershell
& "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive `
    -ExecutionPolicy Bypass -File 'C:\Program Files\PeerSync Rendezvous\Test-RendezvousDeployment.ps1'
```

It returns one JSON object and exits 0 only when its local checks pass. The
`issues` array distinguishes a stopped service, absent or foreign TCP/UDP
listener, missing matching server-authentication certificate, absent inbound
allow rule, DNS lookup failure, and a loopback health/TLS failure. The
certificate check does not prove the service identity can use the private key;
the listener and health checks supply runtime evidence. The script reads the
installed `appsettings.json` and does not inspect service-process environment
overrides. It omits certificate thumbprints, private keys, paths and resolved
IP addresses from its JSON. Its health request emits one ordinary diagnostic
event, but it does not change service configuration. A passing result proves
only the server's local configuration and loopback TLS path. Keep the
outside-network `psp probe`
below as the TCP/UDP 99 acceptance test; neither a local allow rule nor a
local UDP socket proves provider or upstream firewall admission.

Read `issues` in this order. For `settings-unavailable` or
`public-url-mismatch`, inspect the installed `appsettings.json` and any
service-level `PEERSYNC_` overrides. For `server-certificate-unavailable`,
install or renew the Server Authentication certificate for the configured
host in `LocalMachine\My`; if the service remains stopped with an eligible
certificate, inspect its Application Event Log entries and rerun the packaged
certificate-grant helper. For `service-not-running`, inspect those events and
the service account before restarting the service. A
`tcp-listener-missing-or-foreign` or `udp-listener-missing-or-foreign` result
means port 99 is unbound or held by another process; inspect the reported
service PID and the corresponding `Get-Net*` endpoint command above.
`tcp-allow-rule-missing` and `udp-allow-rule-missing` identify local rule
configuration only. `local-health-or-tls-failed` includes `curlExitCode`;
after the service owns TCP 99, check certificate trust, name and the local
`/healthz` response. `dns-check-unavailable` means the public name did not
resolve from the server. Any `*-check-unavailable` result means that check
could not run and must not be treated as a pass.

Both Windows Firewall and the server provider must permit TCP and UDP 99.
`/healthz` reports HTTP service health. From a **different network**, run the
installed client CLI:

```powershell
psp probe sync.taskscape.com:99
```

The JSON must report `tcp.reachable` and `udp.reachable` true, with the
server-observed address. Exit code 0 requires both. If TCP works and UDP fails,
check provider UDP filtering and QUIC support. Both requests carry one
`sessionId`; find it as `sessionId` on the server's `rendezvous.probed` events,
which also identify HTTP/2 versus HTTP/3. `correlated: true` confirms the server
echoed that ID. A failed leg includes a bounded `category`, exception type and,
when available, socket, QUIC, HTTP request and native error codes. No server
event for a leg means it did not reach the HTTP handler; it does not alone
identify the blocking firewall or network hop. A local probe proves only the
server's own listeners, not external reachability.

## Configure two PCs

Install the signed PeerSync client setup separately on each PC and initialize
each client service. Never copy one PC's device certificate or private key to
another. The default `RendezvousServers` setting is
`https://sync.taskscape.com:99`; set each folder's `UseTracker` and `UseRelay`
to true and the device's `UseRelay` to true. `UsePortMapping` enables best-effort
UPnP/NAT-PMP for the client's TCP and UDP listener. Restart the service after
editing settings. Share only the folder capability through the intended
enrollment flow; the tracker gets a derived FolderId, not the capability.

For a controlled fallback test, first confirm a direct transfer works. Then
block the direct **peer listener port** between the two PCs while leaving
their outbound TCP 99 paths to the central server open. After five seconds the
initiating client should request a relay. Check the app's peer Route column for
`via relay`, and verify a file hash on the receiving PC. Restore the direct
firewall path after the test. Repeat from distinct home networks and a mobile
hotspot to close the WAN gate; a Hyper-V virtual LAN is supplemental only.

## Capture a failure

Run the probe and capture the command's JSON and exit code. Collect the server
bundle using `PeerSync.Rendezvous.exe diagnostics --output <new-zip>` and
each PC's `psp diagnostics` bundle immediately after the failed transfer.
The exact commands, event names, privacy rules and interpretation steps are in
[diagnostics.md](diagnostics.md). Compare the same UTC window and session ID
across the three bundles. Check `Get-WinEvent -LogName Application` for
`PeerSyncRendezvous` warnings/errors if the server service fails before a
journal is written. Keep the original transport and native error codes in the
report; do not send raw settings, databases, private keys or folder contents.
