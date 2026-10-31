# Azure diagnostic capture for the two-PC Tailscale failure

Status: implemented in source on 2026-10-31. The dedicated West Europe
`PeerSyncDiagnostics` Application Insights resource and
`PeerSyncDiagnostics-weu` Log Analytics workspace exist in the
`PeerSyncDiagnostics` resource group. The workspace retains logs for 30 days
and has a 0.1 GB/day ingestion cap. The local production PC has been upgraded
to version 1.0.1.0 and its telemetry is reaching Azure. The second PC and
rendezvous server have not yet been upgraded.

## Purpose

For a reproducible interval, put the two client services and the rendezvous
server on one UTC timeline. The first question is where the sequence stops
when Tailscale disappears on one PC: announcement, peer discovery, candidate
selection, direct connection, relay offer, authentication, or sync pass.

The service's local debug journal produced approximately 22 MB in about three
hours during the 2026-10-31 investigation. Uploading every local debug line
would be noisy and costly. Retain the full short-lived journal locally and
send a bounded subset of structured diagnostic events to Azure.

## Data path

1. Use the dedicated, workspace-based PeerSync Application Insights resource
   and Log Analytics workspace in West Europe. These are separate from the
   subscription's existing TaskBeat Application Insights resource.
2. Each PeerSync service sends directly to Azure Monitor over HTTPS, using
   the Azure Monitor OpenTelemetry .NET log exporter. The rendezvous server
   sends the same schema. The diagnostic route must remain independent of the
   PeerSync tracker: a tracker outage must not also suppress the evidence.
3. Subscribe to `DiagnosticJournal.EntryWritten`, copy allowlisted scalar
   fields into a bounded, nonblocking queue, and export from a background
   worker. The journal callback must never wait for network I/O. Count queue
   drops and exporter warnings in the local journal. A separate heartbeat
   from each process reports its run ID and drop count once per minute. Flush
   for a short bounded interval during shutdown. The exporter does not expose
   a per-record delivery acknowledgment, so an Azure query confirms arrival.
   Azure Monitor's .NET exporter buffers telemetry during network outages in
   the process-specific `azure-cache` directory (up to 50 MB and 48 hours by
   default). Live metrics, performance counters, and standard metrics are
   disabled for this log-only exporter.
4. Default capture: all structured events except the high-frequency idle
   status, request, puller, publication, index, and health events, plus idle
   `service.folder_sync_pass`. Warnings and errors always pass. When
   `FullDiagnostics` is enabled, all structured events pass the filter and
   remote IP addresses and ports are included. Both modes omit paths,
   exception text, and payloads.
5. Bound the rate and buffer before the exporter. Start with 2,000 records
   per minute per process and a 10,000-record queue; report drops rather than
   blocking sync. The workspace has 30-day retention and a daily cap as a
   final cost safeguard. Measure actual
   ingestion before raising the cap. A cap can stop collection during the
   incident, so filtering at the client is the primary control. The current
   0.1 GB/day workspace cap is the effective safeguard; the separate
   Application Insights cap has not been reduced from its service default.

## Configuration

The client setting `SendDiagnosticsToAzure` defaults to `true`; users can turn
it off in PeerSync **Preferences** by clearing **Send diagnostic events to
Azure**. `FullDiagnostics` defaults to `false` and can be enabled with **Full
diagnostics (all events and remote IPs)**. Saving either preference requires a
PeerSync service restart. Existing settings files deserialize missing fields
to these defaults. The same settings can be changed in the protected
`%ProgramData%\PeerSync\config\settings.json` when the UI is unavailable.

On the rendezvous server, `Rendezvous:SendDiagnosticsToAzure` defaults to
`true` and `Rendezvous:FullDiagnostics` defaults to `false` in
`appsettings.json`. Restart that service after changing either value.
`PEERSYNC_Rendezvous__SendDiagnosticsToAzure=false` is an environment override
for the server. The ingestion destination is compiled into the diagnostic
module as a public connection string and may be overridden with
`PEERSYNC_AZURE_MONITOR_CONNECTION_STRING` for either process. The connection
string is an endpoint identifier, not a secret; the dedicated resource and
workspace cap limit its exposure. No Entra ingestion identity is configured.

Both applications keep writing local diagnostic files when Azure upload is
off. Disabling Azure prevents the exporter from being created at the next
service start. The synthetic probe and source build do not modify an
already-running installed service. Azure can still see the source IP of an
HTTPS upload as part of operating its service. The normal feed includes only
an address class; full diagnosis also includes the remote IP and port.

## Event contract

All records use UTC `TimeGenerated`, `AppRoleName` (`PeerSync.Service` or
`PeerSync.Rendezvous`), and these `Properties` names:

| Property | Meaning |
| --- | --- |
| `eventName`, `operation`, `level`, `transport` | Existing controlled event codes and transport. |
| `originDeviceFingerprint` | Stable public-device-ID fingerprint of the emitting PC; `server` on rendezvous. This assigns tracker events to one PC. |
| `folderFingerprint`, `peerFingerprint` | Existing 16-hex public-ID fingerprints. They correlate the same folder and peer across machines without transmitting the share key or full IDs. |
| `runId`, `sessionId`, `traceId` | Process run and available session/trace identifiers. A tracker session ID differs from a later peer/relay session ID; join those stages using folder, peer and UTC until a protocol-level attempt ID is added. |
| `errorCode`, `hResult`, `nativeError`, `transportErrorCode`, `applicationErrorCode`, `retryInMs` | Bounded failure category and retry details. |
| `itemsVisited`, `itemsChanged`, `itemsDeferred`, `bytes`, `elapsedMs` | Bounded counts and duration where relevant. |
| `diagnosticSchemaVersion`, `appVersion` | Contract and deployed binary version; required to distinguish the two machines' builds. |
| `remoteAddressClass` | `loopback`, `private`, `shared_address_space` (100.64.0.0/10), or `public`; the shared range can include Tailscale but is not exclusive to it. |
| `remoteAddress`, `remotePort` | Exact endpoint, only when `FullDiagnostics` is enabled. |

Only these fields may cross the cloud boundary. Explicitly exclude
paths, hostnames, user names, share keys,
FolderIds, full DeviceIds, certificate material, file names, exception
messages/stacks, and arbitrary `ILogger` or ASP.NET request logs. The
fingerprints still reveal a stable relationship between devices and folders,
so limit access and retention. Preserve detailed IP-bearing and exception
logs on each machine for a later, reviewed local bundle if the Azure timeline
still cannot explain the failure.

## Reproduction and interpretation

1. Deploy the same diagnostic build to both PCs and the rendezvous server.
   Confirm exporter health and that all three emit a startup/version event.
2. Record the UTC time, folder fingerprint, which PC loses Tailscale, and the
   exact file change used to test transfer. Keep both PeerSync services running.
3. Capture a five-minute healthy baseline, disconnect Tailscale on one PC,
   create one small test file, wait through a tracker refresh and relay attempt,
   then reconnect Tailscale. The first pass should include at least one
   five-minute announcement interval and the reconnect backoff window.
4. Query the affected folder. Compare `tracker.announcement_sent` on each PC
   with `rendezvous.folder_announced` for each origin device. Then compare
   `tracker.peer_list_received`, `service.peer_candidate_inventory`,
   `connection.candidate_failed`, `service.relay_request_queued`,
   `tracker.relay_request_sent`, `rendezvous.relay_offered` or
   `rendezvous.relay_request_rejected`, `tracker.relay_offer_received`,
   `service.folder_peer_ready`, and `service.folder_sync_pass`/`failed`.
   Check `service.telemetry_heartbeat` and `rendezvous.telemetry_heartbeat`
   separately to distinguish an idle process from missing Azure ingestion.
5. The first missing or rejected transition narrows the fault. If both PCs
   announce but neither sees the other, inspect rendezvous presence. If a
   relay request is sent but rejected, inspect target presence and folder
   matching. If a relay is offered but no session becomes ready, inspect
   transport/auth failures. If ready sessions exist without changes, inspect
   index/sync failures and local apply logs.

Example Log Analytics workspace query after the emitter maps fields above:

```kusto
let folder = "<16-hex-folder-fingerprint>";
AppTraces
| where TimeGenerated > ago(2h)
| extend p = Properties
| where tostring(p.folderFingerprint) == folder
    or tostring(p.eventName) in ("service.telemetry_ready", "rendezvous.started")
| project TimeGenerated, AppRoleName,
    Device = tostring(p.originDeviceFingerprint),
    Event = tostring(p.eventName), Operation = tostring(p.operation),
    Folder = tostring(p.folderFingerprint), Peer = tostring(p.peerFingerprint),
    Session = tostring(p.sessionId), Transport = tostring(p.transport),
    Error = tostring(p.errorCode), RetryMs = tostring(p.retryInMs),
    Changed = tostring(p.itemsChanged), Deferred = tostring(p.itemsDeferred),
    Bytes = tostring(p.bytes), Run = tostring(p.runId),
    AddressClass = tostring(p.remoteAddressClass),
    RemoteAddress = tostring(p.remoteAddress), RemotePort = tostring(p.remotePort)
| order by TimeGenerated asc
```

Codex can query the dedicated workspace through the Azure Monitor connector
and compare all three sources during a live test. A recurring observer is
optional; a one-off agent investigation after the reproduction is sufficient
to identify the first divergent event. Cloud ingestion may lag, so the agent
should allow several minutes before treating absence as evidence.

## Access and rollout controls

- The current implementation uses a connection string because it works by
  default on self-hosted PCs. It is not an authentication secret. Possession
  can allow spoofed or high-volume data; isolation, client rate limits and the
  workspace cap matter. Microsoft Entra authenticated ingestion with separate
  client identities remains a future hardening option.
- Restrict workspace query access to the investigator. Azure upload is on by
  default and can be disabled separately on each PC and the server. Turn on
  full diagnosis only during reproduction because it can produce much more
  data and exhaust the workspace cap.
- Validate the allowlist with a synthetic event containing a path, IP address,
  exception text, and key-like sentinel; confirm none appear in `AppTraces`.
  Verify the exporter failure path leaves sync functional and reports local
  queue/export health.

To diagnose the two-PC failure, install the new build on the second PC and the
rendezvous server, then run the reproduction. The self-contained x64 package at
`artifacts/deploy/PeerSync-AzureDiagnostics-2026-10-31` contains an elevated
`Install-DiagnosticBuild.ps1` upgrade script and its README. Close the client
desktop app before upgrading it. The script backs up old binaries, preserves
configuration, and restarts the service.

## Verification on 2026-10-31

- Locked solution restore and release build passed with zero warnings and
  errors. Diagnostic filtering, field privacy, address-class and settings
  persistence tests passed. The relevant connection, control, app, and
  rendezvous tests passed; two rendezvous tests needed a retry after a
  transient test-port reservation failure.
- Synthetic default-mode telemetry appeared in the dedicated workspace's
  `AppTraces` with `AppRoleName=PeerSync.Service`, the expected run/device
  fields, and `remoteAddressClass=public`. The synthetic IP address and
  exception sentinel were absent.
- Synthetic full-mode telemetry included the fake `203.0.113.42:443` endpoint
  while still excluding exception text. A separate one-minute heartbeat
  appeared with the expected run ID and zero dropped events.
- The local production PC's service and desktop app were upgraded to version
  1.0.1.0. Its new service is running; Azure `AppTraces` contains its
  `service.telemetry_ready` record at 15:35:29 UTC and minute heartbeats at
  15:36:29 and 15:37:29 UTC. Its origin device fingerprint is
  `F724FE24E19DB3B6`, run ID `e971e677c293417bbaed432ed61b2422`, and
  the latest observed heartbeat reported zero dropped events. The upgrade
  backed up the previous binaries under
  `%ProgramData%\PeerSync\backups\diagnostic-Client-20261031T153509Z`.
- The second PC and rendezvous server are still on their existing builds, so
  the Tailscale failure's root cause is still unknown.

## Reference documentation

- [Azure Monitor OpenTelemetry exporter for .NET](https://learn.microsoft.com/en-us/dotnet/api/overview/azure/monitor.opentelemetry.exporter-readme?view=azure-dotnet)
- [AppTraces table](https://learn.microsoft.com/en-us/azure/azure-monitor/reference/tables/apptraces)
- [Microsoft Entra authentication for Application Insights](https://learn.microsoft.com/en-us/azure/azure-monitor/app/azure-ad-authentication?tabs=net)
- [Daily cap for Log Analytics and Application Insights](https://learn.microsoft.com/en-us/azure/azure-monitor/logs/daily-cap)
- [Application Insights FAQ: exposed connection strings](https://learn.microsoft.com/en-us/azure/azure-monitor/app/application-insights-faq)
