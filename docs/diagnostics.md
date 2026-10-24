# Debugging deployments

## Development executable signing

`build/Sign.ps1` uses the supplied certificate thumbprint in `LocalMachine\My`.
It locates `signtool.exe` on PATH or in installed Windows SDK version folders.
For a custom installation, pass `-SignToolPath <full-path-to-signtool.exe>` or
set `PEERSYNC_SIGNTOOL_PATH`. If no tool is installed, install Windows SDK
Signing Tools. The selected executable path is printed before signing.

On 2026-10-18 the user-created `CN=PeerSync Development` certificate signed
`artifacts/deploy/QuicBasics/QuicBasics.exe` locally. SHA-256 RFC3161 timestamping,
SignTool `/pa /all` verification and PowerShell Authenticode status `Valid`
passed. This is development signing with explicit local trust; CI signing and
publicly trusted release signing are not verified by this local check.

CI runs `build/Test-Signing.ps1` for x64 and Arm64 after publishing. This signs
a separate test copy using a fresh, non-exportable development certificate,
verifies its SHA-256/RFC3161 signature, and verifies that changing a PE section
produces `HashMismatch`. It removes the temporary certificate, private key and
runner-local test trust in `finally`. Only the signed smoke EXE, public `.cer`
and JSON evidence are retained under `artifacts/signing/<runtime>/`.
Published deployment probes are not signed with this disposable identity.
This exercises the CI signing path; publicly trusted release signing still
requires a release signing identity.

## Deployment status

P3 service, control app and signed development installer code is merged. The
installed service runs registered folder engines, watcher/periodic scans and
authenticated QUIC/TCP peer synchronization. A signed x64 setup and an older
installed host exchanged files in both directions over a virtual LAN; see
[the Server 2025 installed two-machine smoke](verification/P3-virtual-two-pc-sync-2026-10-22.md).
The rendezvous tracker and relay are implemented locally for P4. Physical two-PC, Windows 11
and Server 2022 clean-install, NAT and soak gates remain open; this is not a
beta or v1 release. The P0 probes below are separate test executables.

## Rendezvous server and two-PC diagnosis

Install the rendezvous service only after a current Server Authentication
certificate for `sync.taskscape.com` with its private key exists in
`Cert:\LocalMachine\My`. The installer checks that certificate before changing
the service, grants its virtual account private-key read access, installs TCP
and UDP 99 firewall rules, and preserves `appsettings.json` on upgrade. Run
`After-RendezvousRenewal.ps1` as the win-acme post-renewal action: it reapplies
that ACL and restarts the service so Kestrel loads the new certificate.

On the server, first run the packaged
`Test-RendezvousDeployment.ps1` from the installation directory; its JSON
`issues` separate local service, listener, certificate, rule, DNS and TLS
health failures. Then check `Get-Service PeerSyncRendezvous`,
`Get-NetTCPConnection -LocalPort 99`, and `Get-NetUDPEndpoint -LocalPort 99`.
From a second PC, run `psp probe sync.taskscape.com:99`; its JSON result must
show `tcp.reachable` and `udp.reachable` true and the server-observed address.
Save the JSON `sessionId` and match it to the server's `rendezvous.probed`
events for the HTTP/2 and HTTP/3 legs. `correlated: true` means the server
echoed the probe ID; older servers can respond without that field. A failed
leg reports a short category and available exception, socket, QUIC, HTTP
request and native codes, without exception messages. A timeout or missing
server event narrows the failed leg but does not prove which network hop
blocked it. The probe ID is diagnostic correlation, not device authentication.
An HTTP/2 success with HTTP/3 failure points to UDP 99, QUIC or provider
filtering. Neither a local probe nor a virtual LAN probe proves provider
reachability from another network. Check the service certificate subject and
expiry if both protocols fail before an HTTP response. The probe needs no
device certificate, but tracker and relay streams require a current P-256
device certificate and derive DeviceId from its public key.

The server writes structured events to
`C:\ProgramData\PeerSync.Rendezvous\logs` with debug events enabled by default.
It records startup, HTTP/2 or HTTP/3 probes, authentication rejection,
presence updates, punch introductions, relay joins/leaves, rate limits and
session close. A framework-level HTTP 429 increments the rate-limit counter
and writes a sampled `rendezvous.http_rate_limited` warning at most once per
five seconds per server process, with the request protocol and observed endpoint;
the default bundle removes the endpoint. An authenticated tracker rejection or initial-announcement
deadline writes `rendezvous.session_failed` under the ID sent in the stream's
first observed-address response. Relay token/join rejection writes
`rendezvous.relay_join_rejected` with a fixed code and without the token;
relay forwarding quota failures retain their joined session ID. A probe ID
belongs only to the two unauthenticated health legs and is separate from any
later tracker or relay session ID. An offer revoked before both relay participants
join reports `Aborted`; an unanswered join deadline reports `DeadlineExceeded`.
Application Event Log entries from `PeerSyncRendezvous` contain
warning/error event names, operation, session ID and error code. They omit IPs,
FolderIds, keys, paths and payloads. The configured OTLP endpoint exports
aggregate counters for tracker/relay sessions, relay bytes, rejected auth,
rate limits and probes; set `Rendezvous:OtlpEndpoint` to an HTTPS collector (or
loopback HTTP) only when one is available.

To collect a server bundle, run this elevated on the server shortly after the
failure:

```powershell
& "$env:ProgramFiles\PeerSync Rendezvous\PeerSync.Rendezvous.exe" diagnostics `
  --output C:\Temp\PeerSync-rendezvous-diagnostics.zip
```

If `Rendezvous:LogDirectory` is customized in the server configuration, pass
that absolute directory with `--logs`; the default command reads the standard
ProgramData journal directory.

On both PCs run `psp diagnostics --logs <service-log-directory> --output
<new-zip>`. Collect all three bundles after a forced relay attempt and compare
UTC timestamps and session IDs: `tracker.session_connected`,
`tracker.peer_list_received`, `tracker.relay_offer_received`,
`rendezvous.relay_joined`, `service.peer_connected`, and sync-pass events.

For one bounded collection on each installed machine, run these in elevated
Windows PowerShell. Each invocation creates a new directory, prints the path
to `deployment-evidence.json`, and writes that sanitized summary plus a ZIP.
The client command probes the central server using HTTP/2 and HTTP/3; omit
`-ProbeHost` when testing an offline machine. The server command includes its
installed local deployment preflight. Nonzero exit leaves the summary and any
successfully collected bundle in place with issue codes; it does not change
service settings.

```powershell
& "$env:ProgramFiles\PeerSync\tools\Collect-DeploymentEvidence.ps1" `
  -Role Client -ProbeHost sync.taskscape.com:99
& "$env:ProgramFiles\PeerSync Rendezvous\Collect-DeploymentEvidence.ps1" `
  -Role Rendezvous
```

Pass `-OutputDirectory C:\Temp\PeerSync-field-machine1` to choose a new,
nonexistent directory. The summary keeps the probe correlation ID and
per-protocol result but omits the observed address, peer identities, folder
keys and raw settings. It records installed binary signature and hash, SCM
state and account, client listener ownership when configured, crash-policy
audit and a SHA-256 hash for its default sanitized ZIP. A new client with no
registered folder may not have started its listener yet.
It does not assert that a folder synced or that an outside provider admits
port 99; retain a file-hash comparison and an outside-network probe for those
gates.

`tracker.outgoing_full` means the bounded tracker stream did not accept a
relay or punch request; its `errorCode` names only the request kind. The
connection attempt fails promptly and the maintainer can retry. Folder and
address changes are coalesced into the latest announcement once the writer
can send again. A failed writer ends its tracker session and reconnects.
`tracker.announced_initial` and `tracker.announced_refresh` are emitted only
after the corresponding gRPC write completes. Their `itemsVisited` field is
the announced folder count, not a folder identity. For a tracker disconnect,
`tracker.session_failed` reports the gRPC status as `rpc_<status>`, session
duration in `elapsedMs`, and a nested native socket error when one exists.
Compare those client times with `rendezvous.announced` and
`rendezvous.session_failed` in the server bundle to distinguish a missed
refresh, server rejection and a transport interruption; the client status
alone does not establish which one occurred.
The default bundles omit network addresses; add `--include-network-addresses`
only when routing details are needed and review the ZIP before sharing it.
Diagnostics expire IP-bearing journal files within 24 hours; the collector
also excludes events older than 24 hours. Never attach settings, certificate
private keys, the raw service database or arbitrary logs to a support bundle.

The installed folder host has [local verification evidence](verification/P2-installed-folder-host-local-2026-10-21.md).
Its smoke test proves registered-folder startup scan and orderly local drain;
it does not prove installed SCM operation or production peer synchronization.

Structured events use UTC timestamps, component, process/run ID, session ID,
trace ID when present, transport, operation, byte counts, elapsed milliseconds,
native error number and retry delay. The installed service currently enables
debug events in its local journal. The event model
accepts metadata rather than arbitrary payloads or exception messages. Never
add folder keys, FolderIds, file contents or record bodies to diagnostics.

The diagnostic journal defaults to seven files of at most 14 MB each. Its
15-minute timer removes journal files 24 hours after their oldest file timestamp,
even when the service is idle; a write also checks for expired files. Removal
can discard newer events in the same file. Collection independently filters
events to the last 24 hours. Debug disk writes are synchronous;
only emit progress at bounded intervals rather than for every transferred chunk.

With debug logging enabled, each scanner pass also emits
`scan.memory_enumerated`, `scan.memory_hashed` and `scan.memory_committed`.
Each event carries the same scan session ID, elapsed time, visited-item count,
and numeric `workingSetBytes`, `privateBytes` and `managedHeapBytes`. These are
point-in-time process samples, not a process-wide peak or proof that the
300 MB scale target holds throughout a scan. The sanitized diagnostic bundle
keeps these numbers and still excludes paths and content.

`scan.observation_discarded` records cleanup of an abandoned private native
walk after its exclusive owner lock is released. `scan.observation_cleanup_deferred`
warns that owned stage files could not be removed; neither event includes a
path or staged metadata. Such files are not a published index and are never
used as deletion evidence on restart.

Run `psp diagnostics --logs <log-directory> --output <new-zip>` on both
machines immediately after reproducing. The command retains only an explicit
allowlist of structured fields, filters events to the last 24 hours, excludes
network addresses and ports by default, and includes environment metadata. It excludes
settings, databases, certificates, keys, arbitrary logs, crash dumps, unknown
JSON fields, FolderIds and file contents. Review the bundle before sharing it.
Add `--include-network-addresses` only when routing details are needed. The
collector prints included, expired and invalid event counts and refuses to
create an archive with no recent valid events. If it reports no events, use
the exact `LOGS` directory printed by the service or probe, including any
process-ID child directory. Collect after the failed run, not before it.
Check both PCs' clocks if every event is filtered as expired.

For a joined physical PC whose operator is not working in a terminal,
`build/field/Collect-PeerSync-Diagnostics.cmd` runs the installed `psp
diagnostics` command from its own folder. Copy it into the joined test folder
and double-click it there; the sanitized `PeerSync-diagnostics-*.zip` is
created beside it and can sync to the other PC. The helper does not request
elevation, change configuration, include network addresses, or install an
update. Its window stays open to show a collection error. Review the ZIP
before sharing it outside the test group. A local double-click-equivalent
smoke of the `.cmd` returned exit 0 and created a sanitized ZIP under
`artifacts/field/diagnostic-collector-batch-smoke-20261024/`.

`build/Collect-Diagnostics.ps1` remains a legacy development-probe collector.
It copies source event lines and does not apply the CLI's strict field
allowlist; do not share its archive when logs may contain untrusted or
unexpected fields.

Compare timestamps and session IDs. A transport failure before folder auth is
different from a rejected folder proof, approval wait, missing block, or failed
filesystem apply. Keep the original error code when reporting a failure.
Service startup records certificate loading, folder-engine readiness/failure,
listener readiness and host shutdown. Resume/network changes queue a follow-up
recovery pass when another signal arrives mid-pass, and queue a follow-up scan
when a new signal arrives during a scan. `service.network_recovery_started`
and `service.network_recovery_complete` bracket recovery passes; `operation`
is the fixed trigger category. Scan events include the
trigger in `operation`, elapsed time, item counts and deferred count without
folder paths or identifiers. `service.network_recovery_complete.itemsSkipped`
counts additional signals coalesced into that recovery cycle.
The installed peer runtime records discovery, endpoint dialing, reconnect,
folder authorization outcome, session supervision, sync-pass counts and
failures. The QUIC probe separately records start, TLS authentication,
completion and errors.

Normal file block requests rotate across connected authenticated peers. The
`transfer.block_peer_unavailable` event records a bounded attempt number,
byte count and safe error category; `transfer.block_peer_failover` means a
later peer supplied bytes that passed the requested BLAKE3 digest check. The
events carry the shared authenticated QUIC session ID so they can be matched
with the serving endpoint's block-request events. The log contains no peer ID,
file path or block hash.
`transfer.download_rate_limited` records a block that waited over 10 ms for
the device-wide download bucket; its byte count and shared session ID identify
the affected transfer. `transfer.upload_rate_limited` records a server-side
wait and byte count. It has no session ID because the request handler does not
own the QUIC session identity; match it by timestamp with the serving peer's
`transport.block_stream_accepted` event. The client-side download event keeps
the shared session ID for direct cross-endpoint matching.

## P2: installed service peer runtime

For the experimental self-contained service package, use the step-by-step
[P2 two-PC deployment runbook](peersync-service-two-pc-runbook.md). It covers
the service virtual account, `--init`, per-folder ACLs, same-LAN UDP/TCP setup,
share pairing, sanitized bundle collection and event-based failure triage.
Direct routed endpoints can be configured with `psp hosts`; actual cross-network
service interoperability and the full installed SCM matrix remain open gates.
The P0 `QuicBasics` retry procedure below is a separate probe.

For a service failure, collect diagnostics from both devices after reproducing
the issue. Use the exact service or CLI `LOGS` directory, then run
`psp diagnostics --logs <logs-directory> --output <bundle.zip>` on each device. Keep the
two bundles separate so each preserves its run ID and local timestamps. Match
peers by `sessionId`; the address is useful for routing diagnosis but the
bundle omits addresses unless explicitly requested. Do not send state databases,
folder keys or raw index records.

After installation, `psp add <path>` registers a new local folder, starts its
engine and prints its audited share key; `psp join <key> <path>` joins an
existing share. Use `psp share <folder>` to retrieve the key again, `psp peers
<folder>` to inspect known peers and `psp dump-index <folder>` to stream the
local index. For routed peers, `psp hosts <folder> <host:port ...>` replaces
the predefined endpoint list and dials immediately; `psp hosts <folder>
--clear` removes the list. The listen port is in
`%ProgramData%\PeerSync\config\settings.json`. The folder selector may be the
full path or the hexadecimal ID shown by `psp add`/`psp join`. Append `--json`
for machine-readable output.
These commands use the owner- and PID-verified pipe as `psp status`, and the
service checks caller identity on every RPC.

Run `psp status` on the affected device to check the local service pipe,
production-sync availability and current peer clock states. `psp status --json`
returns the same snapshot for scripts; a failed owner/PID check or unavailable
service exits nonzero with a short error category.

| Event | What it tells you |
| --- | --- |
| `service.quic_listener_ready`, `service.tcp_listener_ready` | QUIC or TCP listener bound the configured device port. Both should be present and use the same number. If one is absent, inspect `service.start_failed` and the immediately preceding event. |
| `service.lan_discovery_unavailable`, `discovery.lan_socket_unavailable`, `discovery.lan_send_failed` | Local multicast/broadcast discovery could not start or send. Compare firewall and active interface state on both devices; explicit predefined hosts bypass LAN discovery. |
| `service.lan_peer_discovered` | A matching folder-tagged LAN announcement was received. If this appears on only one side, compare the LAN-enabled folder set and interface/firewall rules. |
| `discovery.host_resolution_failed`, `discovery.host_dial_failed` | A configured host did not resolve or authenticate. Verify `host:port`, DNS and the remote service listeners. |
| `control.predefined_hosts_updated` | An authorized local caller replaced a folder's configured direct endpoints; `itemsChanged` is the host count, and endpoint values are not logged. |
| `connection.candidate_started`, `connection.candidate_authenticated`, `connection.candidate_selected`, `connection.candidate_failed`, `connection.reconnect_failed` | The `operation` identifies LAN/public/punch/relay candidate source and QUIC/TCP transport. Match `candidate_selected` by `sessionId` to the connected peer and both endpoints' transport logs to identify the chosen outgoing candidate; `candidate_authenticated` may describe a path later superseded by duplicate resolution. A `public` source label does not alone prove packets crossed an outside network. Failed attempts retain the sanitized error category and HRESULT; a socket failure also records its numeric Windows `nativeError`, including when wrapped by an I/O exception. Started and selected events retain the endpoint address and port in the local raw journal when known; `psp diagnostics` omits both unless `--include-network-addresses` is used. Authenticated events contain no endpoint address. QUIC starts first and TCP follows after the 250 ms stagger, so TCP can win before a slow QUIC timeout. Reconnects back off from one second to five minutes. |
| `connection.incoming_selected` | An authenticated incoming connection won duplicate resolution. Its `sessionId` and transport identify the selected session; QUIC/TCP remote address and port are recorded only in the local raw journal and in an address-opted-in bundle. Relay sessions carry the Relay transport without a peer endpoint. The address establishes the actual remote socket, but a private address or LAN discovery alone does not prove the physical path; compare the host route and interface. Losing duplicates do not emit this event. `psp peers <folder> --json` also exposes the connected peer's transport without exposing its network address. |
| `service.peer_endpoint_expired`, `service.peer_endpoint_limit_reached` | Reconnect targets expire after 15 minutes without observation and are capped at 16 per peer. These events report only counts, so roaming and stale-address cleanup can be diagnosed without disclosing the peer identity or endpoint. Repeated observations refresh an endpoint and the strongest known source (LAN before public) is retained. |
| `transport.tcp_connect_failed`, `transport.tcp_data_attach_failed` | TCP control or data-lane setup failed. `operation` gives the bounded connect/TLS/pin/ticket/admission stage; HRESULT and socket native error are recorded when available. Certificate names and exception text are omitted. |
| `transport.authenticated`, `transport.handshake_failed`, `transport.stream_failed`, `transport.disconnected` | Authenticated session lifecycle across QUIC and TCP; compare the transport and session ID fields with both endpoints. |
| `transport.folder_auth_allowed`, `transport.folder_auth_pending`, `transport.folder_auth_denied` | The local or remote folder approval decision. `local_policy_*` means this device's policy; `remote_policy_*` means the other device's policy. Approve the DeviceId on both sides when required. |
| `service.folder_peer_ready` | The authenticated folder transport attached to the local sync engine. Both devices should report this for the same session. |
| `service.folder_sync_pass`, `service.folder_sync_failed` | Repeated resumable index exchange and apply pass. Deferred/failed counts mean work remains; compare the session and timestamp with engine `apply.*`, `index.*` and filesystem events. |
| `service.peer_session_failed`, `transport.stream_failed` | Session or stream closed after authentication. Check the earlier Hello, folder-auth and clock events before interpreting the transport error. |
| `service.folder_scheduler_stopped`, `service.peer_accept_failed` | A host loop stopped unexpectedly; the service exits visibly rather than silently dropping scans or accepts. |
| `service.folder_paused`, `service.folder_resumed`, `service.folder_settings_updated` | An active folder was drained and reopened for a policy change. `itemsDeferred=1` on the settings event means a network policy refresh needs a service restart. A pause without resume after a settings request means the new policy stayed safely paused for retry. |
| `service.folder_policy_refresh_failed` | The replacement engine opened, but LAN, tracker or predefined-host policy could not be refreshed. Inspect the sanitized error category and restart the service to apply the saved network policy. |
| `control.folder_added`, `control.folder_joined`, `control.folders_listed`, `control.peers_listed`, `control.index_dumped` | A local control operation completed. Counts and session IDs are recorded; keys and folder paths are omitted. |
| `control.folder_request_failed` | A local control operation failed. `errorCode` is the exception category; the RPC returns a sanitized status message. |
| `control.folder_keys_read` | An authorized caller retrieved a key through the audited RPC. The key and folder ID are not logged. |

The same authenticated session ID is recorded by both endpoints. A listener
ready event without a peer-discovered event points to discovery/configuration;
discovery without candidate/auth events points to the dial path; an allowed
folder decision without `service.folder_peer_ready` points to folder-stream or
clock admission; repeated sync failures after readiness point to index, block
or filesystem handling. The local two-host loopback test is described in the
[service peer-runtime verification](verification/P2-service-peer-runtime-local-2026-10-21.md).

## P1: directory reconstruction and case recovery

Engine diagnostics distinguish content reachability from publication proof.
Enable debug events when capturing a stalled directory case. Use the session
ID and timestamp to follow an attempt; ordinary retry backoff is reported in
milliseconds. A pending proof preserves the original signed version until its
children can be verified. Repeated retries should be investigated before
restarting or modifying the files.

| Event | Meaning and useful fields |
| --- | --- |
| `apply.case-route-unavailable` | A candidate could not supply valid old content. `errorCode`, `hResult`, `bytes` and elapsed time identify the failure category. |
| `apply.case-route-timeout` | A route exceeded its transfer allowance. A source attempt is bounded to 300 seconds; individual routes allow one second plus bytes at 256 KiB/s. |
| `apply.case-route-failover` | Another known peer supplied verified content. The durable hint can be reused on later attempts. |
| `directory.metadata_deferred` | Snapshot metadata recovery could not advance. Inspect the exception category and native error; missing metadata is never an empty subtree. |
| `directory.publication_pending` | A bounded child capture needs more passes; visited/changed counts show progress. |
| `directory.publication_time_prepare`, `_capture`, `_rebase`, `_parent_roots`, `_publication`, `_commit` | Debug-only milliseconds for one queued directory stage, correlated by its session ID. `itemsVisited` reports captured or proven members where relevant. The event name identifies the stage; no directory path is logged. |
| `directory.queue_unchanged` | A queued directory matched its current signed snapshot and was released without a new local version. Repeated occurrences during a large source publication can indicate rescans restaging completed siblings. |
| `directory.signed_leaf_cache_hit` | A local-only leaf with a stale global epoch still matched its exact signed child input. `itemsVisited` is its child count; no directory name or hash is logged. |
| `directory.rebase_pending` | A received original subtree still needs causal verification. `itemsDeferred` distinguishes waiting from budget continuation. |
| `directory.parent_roots_pending` | At least one eligible original parent root remains unproven. Metadata recovery or ordinary child receipt may be required. |
| `directory.parent_roots_complete` | Every eligible root is covered by authenticated current children for this attempt. Final commit still revalidates the witnesses. |
| `directory.parent_clock_template` | Parent clocks were absorbed before publication. `proven-root-floor` means all eligible roots were proven. |
| `directory.publication_retry` | `errorCode` starts with the failing stage, such as `parent-roots`, `history` or `commit`, followed by the exception category or `pending`. Inspect native error and retry delay. |
| `puller.publication_scan_deferred` | A full native scan is being shared across consecutive live-file batches. `itemsDeferred` is the installed file count awaiting a parent-snapshot scan; `retryInMs` is the remaining five-minute ceiling. |
| `puller.publication_scan_flushed` | A completed scan caught up with those installed files. `itemsChanged` is the coalesced count and `elapsedMs` is its age. A missing flush after a deferred event warrants inspection of later scan/retry events. |
| `puller.own_write_watch_coalesced` | A watcher batch contains only paths recently written by this folder's remote-file coordinator. The pass still runs, but it shares the pending publication scan; `itemsDeferred` is the number of watcher paths. Unrelated paths and overflow request a scan immediately. |
| `scheduler.pass_start`, `scheduler.pass_complete` | The installed folder scheduler ran the shared transport coordinator, which owns recovery, native scanning, needed-work repair and publication. Completion reports scanned entries, applied work and deferred/problem counts. Direct scanner-only hosts retain `scheduler.scan_start`. |
| `puller.startup_recovery_complete`, `puller.startup_recovery_failed` | The installed folder drained pending native apply state before exposing its coordinator-owned scanner. The completion event counts recovered intents; a failure records only exception and native-error categories and keeps the folder unavailable. |

These events intentionally omit file paths, content hashes, keys and signed
records. Counts and session IDs support comparison without disclosing folder
contents. The current engine tests exercise these stages locally; they do not
establish deployment readiness for the production service.

## P0: two-machine QUIC test

For a fresh two-PC diagnostic package, use the step-by-step
[retry runbook](quic-two-pc-retest.md).

After `dotnet build -c Release`, copy the entire output directory
`spikes/QuicBasics/bin/Release/net10.0-windows10.0.20348.0/` to a machine with
.NET 10, or self-contained publish with `dotnet publish spikes/QuicBasics -c
Release -r win-x64 --self-contained -o artifacts/deploy/QuicBasics`.

On the receiving machine: `QuicBasics.exe listen 47821 1073741824`.
Allow inbound UDP 47821 for this executable in the test environment. On the
sender: `QuicBasics.exe dial <receiver-IP> 47821 1073741824`. A zero exit means
one GiB was exchanged, the receiver's SHA-256 matched, and both certificates
were presented. Save each process's JSON result and `logs/<process-id>/debug*.jsonl`.
The JSON result names the exact log directory. Concurrent processes use
separate directories, and the collector reads live logs without blocking writes
or rotation.
The same session ID appears in both processes' JSON and transfer events.
Self-signed certificates are created per probe run and deleted afterwards.
There is no manual TLS certificate or root-store setup for this test. These
device certificates are separate from the development code-signing certificate.

Use matching, newly published binaries on both PCs. The probe prints `LOGS`
on stderr at startup and again on failure, with phase, bytes, elapsed time,
QUIC error, HRESULT and protocol error numbers. Progress appears every five
seconds during an active transfer. HRESULT is not a native socket error.
The receiver waits up to three minutes for a connection; once authenticated,
each peer gets a fresh 15-minute transfer budget. Set a different transfer
budget on both peers with `--timeout-seconds <1..86400>` after the byte count.
Keep the receiver and sender byte counts equal. Start with `16777216` (16 MiB),
then repeat with `1073741824` (1 GiB), restarting the receiver for each run.

For a newly extracted probe path, an existing program-specific firewall rule
for the old path may not apply. On the receiver, an administrator can add a
temporary rule scoped to the new EXE, the UDP port and the sender's IP:

```powershell
New-NetFirewallRule -DisplayName 'PeerSync QUIC probe retry' `
    -Direction Inbound -Action Allow -Protocol UDP -LocalPort 47821 `
    -Program (Join-Path $PWD 'QuicBasics.exe') -RemoteAddress <sender-IP> -Profile Any
```

Run it from the extracted probe folder. Remove the temporary rule after testing
with `Get-NetFirewallRule -DisplayName 'PeerSync QUIC probe retry' | Remove-NetFirewallRule`.
Use the sender's Tailscale address for a Tailscale test or its LAN address for
a LAN test. A Tailscale transfer verifies the overlay path; it does not measure
native 1-Gbit LAN performance or prove PeerSync's own NAT traversal. Do not use
`Test-NetConnection -Port` as proof of UDP reachability: it tests TCP.

This establishes reachability only. It does not implement folder authorization,
pinning, discovery, relay fallback or NAT hole punching.

## P0: Windows Server port 99

Copy the complete Rendezvous99 output or self-contained publish it. Verify TCP
and UDP 99 are free before starting. Run `Rendezvous99.exe server 99`, then
`Rendezvous99.exe client https://<server-IP>:99 2` and the same command with `3`
on the other PC. The client requires that exact HTTP version; it never silently
falls back. The server must observe the device certificate in each direction
of a three-message bidirectional stream. Use temporary firewall rules scoped
to the probe executable and remove them after the test.

The server prints its debug log directory. To choose it explicitly, use
`Rendezvous99.exe server 99 C:\PeerSync-P0-logs`. TLS acceptance/rejection,
stream message/completion/failure, native errors and timings are recorded.
The client's JSON session ID matches the server's stream events; collect that
directory with `build/Collect-Diagnostics.ps1` and retain the client JSON.

The probe's self-signed server certificate is accepted by the probe client.
The production server will use its configured machine-store certificate.
Do not use the probe to relay files or expose it as the production service.
