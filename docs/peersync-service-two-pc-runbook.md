# PeerSync P2 service deployment and two-PC diagnosis

This is an experimental P2 self-contained Windows service package for controlled testing. It has no installer or upgrade flow. Use disposable test folders and the same package on both PCs. The installed service listens for authenticated QUIC over UDP and TCP/TLS on the same configured port. QUIC starts first and the authenticated TCP candidate starts after 250 ms; either can win, and the losing attempt is canceled. The host races remembered LAN addresses before configured public endpoints and reconnects with bounded exponential backoff. It refreshes observed routes, expires endpoints after 15 minutes and retains at most 16 candidates per peer; diagnostics report expiry/cap counts without peer IDs or addresses. Local installed-host loopbacks cover authenticated transfers over both transports. This does not establish Gate P2, hosted CI, physical LAN throughput, NAT traversal, relay support or production readiness.

## Unattended test and firewall prompts

A Windows Firewall dialog asking whether a test executable may communicate is
an environment setup blocker, not evidence that PeerSync failed. Do not change
product code or repeat the same test while the dialog is waiting for a person.
For an unattended run, identify the executable, transport, listening port and
peer address from the test command, then create the narrow firewall rules below
before starting the listener. Scope direct peer rules to the test peer's
address; scope discovery to `LocalSubnet`. If the rule cannot be installed
because the shell is not elevated or policy denies it, record the test as
blocked by the environment and continue with independent checks. Change product
code only when a completed run produces evidence of a product defect.

If GitHub Actions cannot start because its budget is exhausted, run the full
canonical local verifier (`./build/Verify.ps1 -Runtime win-x64`) and report
that result as local evidence. Do not repeatedly rerun a hosted job that GitHub
denies before any test step starts; the hosted gate stays pending until it can
run.

## Build and copy the package

On the development PC, with the .NET 10 SDK and Git available, run:

```powershell
./build/Publish-Service.ps1 -Runtime win-x64
```

The script prints `DEPLOYMENT_ARCHIVE`, `SERVICE_EXE` and `CLI_EXE`. Copy the
same ZIP to both test PCs. The manifest records the source revision, whether the
working tree was dirty, runtime, and the still-open P2 gate. The package is
self-contained and does not need a .NET runtime installed on the test PCs.

## Install the service on each PC

Open elevated PowerShell. Extract the ZIP under `C:\Program Files\PeerSync`.
Create the local log-reader group if it does not already exist, then register
the service as its virtual account and enable its service SID:

```powershell
if (-not (Get-LocalGroup -Name 'PeerSync Users' -ErrorAction SilentlyContinue)) {
    New-LocalGroup -Name 'PeerSync Users' -Description 'PeerSync diagnostic log readers'
}
$operator = "$env:COMPUTERNAME\$env:USERNAME"
if (-not (Get-LocalGroupMember -Group 'PeerSync Users' | Where-Object Name -eq $operator)) {
    Add-LocalGroupMember -Group 'PeerSync Users' -Member $operator
}

$serviceExe = 'C:\Program Files\PeerSync\service\PeerSync.Service.exe'
sc.exe create PeerSync binPath= "`"$serviceExe`"" start= auto obj= 'NT SERVICE\PeerSync'
if ($LASTEXITCODE -ne 0) { throw "sc.exe create failed: $LASTEXITCODE" }
sc.exe sidtype PeerSync unrestricted
if ($LASTEXITCODE -ne 0) { throw "sc.exe sidtype failed: $LASTEXITCODE" }
& $serviceExe --init
if ($LASTEXITCODE -ne 0) { throw "PeerSync initialization failed: $LASTEXITCODE" }
```

`--init` provisions `%ProgramData%\PeerSync`, a device certificate with a
non-exportable machine key, a free listener port, settings, the setup user's
control authorization, and the `PeerSync` Application event source. Do not
copy a device certificate or its private key between PCs. On each PC, note the
configured `listenPort` in
`%ProgramData%\PeerSync\config\settings.json`. If you run `--init` as an
administrator account different from the account that will run `psp.exe`, add
that operator's SID to `ControlUserSids` using the documented settings file
before starting the service.

Before starting the service, create firewall rules on each PC. This avoids the
first-listen Windows Firewall prompt while the service is unattended. The
helper replaces only its own rules in the `PeerSync Test Setup` firewall group,
scopes direct TCP/UDP traffic to the PeerSync service executable, configured
port and other PC's address, and can add LocalSubnet-scoped discovery rules.
Run it from elevated PowerShell on both PCs, with each PC's peer address:

```powershell
$settings = Get-Content "$env:ProgramData\PeerSync\config\settings.json" -Raw | ConvertFrom-Json
$serviceExe = 'C:\Program Files\PeerSync\service\PeerSync.Service.exe'
& 'C:\Program Files\PeerSync\tools\Allow-PeerSync-TestTraffic.ps1' `
    -ServiceExecutable $serviceExe -ListenPort $settings.listenPort `
    -PeerAddress '<PC-A-address>' -EnableLanDiscovery
sc.exe start PeerSync
if ($LASTEXITCODE -ne 0) { throw "PeerSync start failed: $LASTEXITCODE" }
```

## Run in the foreground for console debugging

The service executable runs in the foreground when launched directly, so its
`LOGS` path and startup failures are visible in the terminal. Do not run a
console instance beside the SCM service: they would share the same certificate,
state database, listeners and control pipe. In elevated PowerShell, stop the
service and wait for it to exit before launching the executable:

```powershell
sc.exe stop PeerSync
(Get-Service PeerSync).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
Set-Location 'C:\Program Files\PeerSync\service'
& .\PeerSync.Service.exe
```

Use Ctrl+C to stop the foreground process, then restart the installed service:

```powershell
sc.exe start PeerSync
```

The console launch uses the installed settings, certificate and state. A local
smoke test confirmed startup, folder scan, both listeners and graceful Ctrl+C
shutdown on the installed package. The default `psp status` client rejects a
console-launched instance while SCM is stopped because it verifies the SCM
service owner and PID; use the foreground output and that process's `LOGS`
directory for console diagnostics.

## Probe the running installed service locally

On a development PC with this source checkout, an elevated PowerShell session
and the .NET 10 SDK, first register a disposable folder with the installed
service and place a small nonempty file in it. Then run:

```powershell
Set-Location 'C:\Projects\Sync'
.\build\Test-InstalledServicePeer.ps1 -FolderPath 'C:\PeerSync-Test\Share' -FileName 'smoke.txt'
```

To verify the actual rendered WPF clock controls against this fixed service,
run the same command with `-RenderUi` while the development PC has an active
interactive desktop. It saves skewed, recovered and disconnected captures
under `artifacts/clock-ui/` in the source checkout. A disconnected or locked
desktop may yield a blank bitmap; record that as a UI test-environment failure
and retry only after an active desktop is available.

The probe connects to the fixed SCM service through the owner/PID-verified
control client, creates a temporary certificate and peer, and uses real QUIC
or TCP transport to connect to the installed listener. It verifies that an
injected 11-minute peer clock skew blocks index exchange, then corrects that
peer clock and verifies an authenticated file transfer by SHA-256. It also
checks that production availability returns to false after disconnect. The
wrapper reserves a separate peer port, installs narrow loopback firewall rules
for its executable before launch, and removes those rules afterward. It does
not change the Windows clock or installed service settings. The source file
stays in the registered folder; the received copy and temporary peer state
are deleted at exit. Its JSON result contains no folder key, device identity
or source path. If it fails, inspect the `stage`, `error` and `hResult` fields
and collect a sanitized `psp diagnostics` bundle from the installed package.
This local check does not replace a second installed PC, physical LAN testing
or Windows Server validation.

After starting, verify the service and listeners on **each** PC. `--init`
chooses a port independently for each machine, so read the remote port from
that machine's settings instead of assuming it matches the other PC:

```powershell
$service = Get-Service PeerSync
$settings = Get-Content "$env:ProgramData\PeerSync\config\settings.json" -Raw | ConvertFrom-Json
$port = [int]$settings.listenPort
$tcp = @(Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue)
$udp = @(Get-NetUDPEndpoint -LocalPort $port -ErrorAction SilentlyContinue)
[pscustomobject]@{
    Service = $service.Status
    ListenPort = $port
    TcpListening = ($tcp.Count -gt 0)
    UdpBound = ($udp.Count -gt 0)
    PeerSyncFirewallRules = @(Get-NetFirewallRule -Group 'PeerSync Test Setup' -ErrorAction SilentlyContinue).Count
}
```

From the other PC, check the remote machine's reported TCP port with
`Test-NetConnection -ComputerName '<other-PC-address>' -Port <other-PC-listenPort>`.
This checks TCP only; correlate PeerSync's QUIC dial diagnostics to verify UDP.

Replace `<PC-A-address>` with PC A's LAN address. Apply the same command on PC
A with PC B's address. The service is configured to listen on the port created
by `--init`, so add the rules after initialization and before the service
starts. Discovery rules are scoped to the local subnet and discovery multicast
groups on UDP 47820. Direct TCP fallback only works when that TCP endpoint is
reachable; it does not configure router port forwarding or bypass restrictive
NAT. Predefined hosts can resolve and dial a directly reachable address, but
PeerSync still has no rendezvous/relay-based cross-network path. The previous
successful QuicBasics transfer over Tailscale was probe-only evidence and does
not configure PeerSync service endpoints. A TCP port check does not prove UDP
reachability.

## Pair one test folder

Use a different empty or disposable test directory on each machine. The
service account must be able to read, write, rename and delete files in each
sync directory. Grant it access only to the disposable test tree (run elevated
PowerShell on each PC):

```powershell
$share = 'C:\PeerSync-Test\Share'
New-Item -ItemType Directory -Force $share | Out-Null
icacls $share /grant 'NT SERVICE\PeerSync:(OI)(CI)M' /T
if ($LASTEXITCODE -ne 0) { throw "icacls failed: $LASTEXITCODE" }
```

Run the CLI from the matching `cli` folder as the control-authorized Windows user.
On PC A, add the share and preserve the displayed share key long enough to join
from PC B:

```powershell
Set-Location 'C:\Program Files\PeerSync\cli'
& .\psp.exe status --json
& .\psp.exe add 'C:\PeerSync-Test\Share'
```

On PC B, join that key to its own directory:

```powershell
Set-Location 'C:\Program Files\PeerSync\cli'
& .\psp.exe join '<share-key-from-PC-A>' 'C:\PeerSync-Test\Share'
```

If LAN multicast cannot reach the other device (for example, the PCs are on
different networks), configure a directly reachable endpoint on at least one
side. Use the other PC's Tailscale/LAN address and its `listenPort` from that
PC's `%ProgramData%\PeerSync\config\settings.json`:

```powershell
& .\psp.exe hosts 'C:\PeerSync-Test\Share' '<other-PC-address>:<other-PC-listenPort>'
```

The command replaces the endpoint list and starts dialing immediately. Supply
multiple endpoints to replace it with several alternatives, or use
`psp.exe hosts 'C:\PeerSync-Test\Share' --clear` to remove all endpoints. The
remote service must be running and its firewall must allow inbound TCP and UDP
on its configured port from this PC. Direct public endpoints also require
router forwarding; this command does not bypass restrictive NAT.

Transfer the key to PC B using a private, out-of-band method. Never put it in a
diagnostic bundle, issue, or log. Wait for matching devices to be approved as
the folder's approval policy requires, then inspect both sides:

```powershell
& .\psp.exe status --json
& .\psp.exe peers 'C:\PeerSync-Test\Share' --json
```

Create a small text file under PC A's test share. Confirm identical bytes
appear under PC B's test share, then repeat in the opposite direction. Check
`ProductionSyncAvailable` and peer clock state in `psp status --json`. For the
first network test, keep files small; do not use important or irreplaceable
data.

## Collect and compare logs

Immediately after reproducing a failure, run on both PCs using the CLI package:

```powershell
$logs = "$env:ProgramData\PeerSync\logs\service"
Get-ChildItem $logs -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1
```

Use the newest run directory shown above. It contains the service's structured
`debug.jsonl` and environment metadata. Create a separate bundle on each PC:

```powershell
& 'C:\Program Files\PeerSync\cli\psp.exe' diagnostics `
    --logs '<exact-newest-run-directory>' --output 'C:\Temp\peersync-pc-a.zip'
```

Use a different output name on PC B. By default, addresses are omitted. Only
add `--include-network-addresses` when the support request is specifically
about routing. Review each archive before sharing it. Do not send databases,
settings, certificates, private keys, or folder contents.

Match the same `sessionId` and UTC timestamps in both bundles:

| Last useful event | Likely stage to investigate |
| --- | --- |
| No `service.quic_listener_ready` or `service.tcp_listener_ready` | Service startup, settings, certificate access or port bind. Both listeners should bind the configured number. Read `service.start_failed` and the immediately earlier event. |
| Listener ready, no `service.lan_peer_discovered` | LAN discovery settings, interface/firewall multicast, or a cross-network path that requires a predefined public host. |
| `connection.candidate_started`, `connection.candidate_failed`, then reconnect failures | The candidate operation identifies source (`lan`, `public`, `punch`, `relay`) and transport (`quic`, `tcp`). Failed events include a sanitized error code and HRESULT. Local events also retain the remote address and port; `psp diagnostics` omits both unless `--include-network-addresses` is selected. QUIC starts first and TCP follows after 250 ms, so TCP can win before a slow QUIC timeout. `transport.tcp_connect_failed` records stages such as `tcp_connect`, `tcp_tls`, `tcp_pin`, `tcp_attach`, `tcp_ticket` and `tcp_admission_ack`; `transport.tcp_data_attach_failed` records data-lane `tcp_ticket_attach` or `tcp_ticket_ack` failures. These events include HRESULT and socket native error when available. Check both UDP and TCP firewall/port-forward rules, listener address and remote service availability. |
| `transport.folder_auth_pending` or `transport.folder_auth_denied` | Folder key mismatch, device approval, or policy on the endpoint named `local_policy_*` / `remote_policy_*`. |
| `clock.blocked` | Peer clock sample exceeded the allowed ten-minute skew or was invalid. Use `psp status --json` to see minutes and reason; correct the host clock and allow the supervised reconnect. |
| `transport.folder_auth_allowed` but no `service.folder_peer_ready` | Folder stream attachment or clock admission failed. Inspect `service.peer_session_failed` and `transport.stream_failed` for the same session. |
| `service.folder_peer_ready` then `service.folder_sync_failed` | Authenticated sync began; correlate engine `index.*`, `transfer.*`, `apply.*` and filesystem events. A nonzero deferred/failed count means work remains. |
| `service.folder_sync_pass` with no applied items | Compare the peers' index sequence, file timestamps, ignore rules and whether the source file was indexed. Use `psp dump-index <folder> --json` locally on each side. |

If a bundle contains only environment metadata, collect again from the exact
run directory printed by the service/CLI after the failure. Collection filters
events older than 24 hours and rejects an empty bundle. `docs/diagnostics.md`
contains the full event reference and the separate P0 QUIC-probe procedure.

## Remove the temporary deployment

From elevated PowerShell on each PC, stop and remove the test service, then use
the service's guarded removal command before deleting the extracted binaries:

```powershell
Get-NetFirewallRule -Group 'PeerSync Test Setup' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
sc.exe stop PeerSync
& 'C:\Program Files\PeerSync\service\PeerSync.Service.exe' --remove-data
sc.exe delete PeerSync
icacls 'C:\PeerSync-Test\Share' /remove:g 'NT SERVICE\PeerSync' /T
```

`--remove-data` deletes only `%ProgramData%\PeerSync` and its device
certificate/key. It never deletes synced folders. Keep the test folders until
you have retained any needed evidence.

The installed client packages have since completed authenticated direct,
tracker and forced-relay sync between two disposable Windows Server guests.
See [two-guest central and relay evidence](verification/P4-two-guest-central-relay-2026-10-23.md).
The [physical soak procedure](physical-soak.md) supplies read-only hourly tree
snapshots and a completeness audit. Gate P2 still requires three physical PCs,
LAN throughput and corpus evidence, actual sleep/resume and Wi-Fi switching,
and a full 72-hour soak. Virtual guests do not close those checks.
