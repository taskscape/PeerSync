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

The product is in P1. The engine is exercised through connected in-memory
transport with real Windows files and SQLite indexes. Production service,
tracker, relay, CLI and installer integration is incomplete. The P0 probes
below are deployable test executables, not a v1 release.

Structured events use UTC timestamps, component, process/run ID, session ID,
trace ID when present, transport, operation, byte counts, elapsed milliseconds,
native error number and retry delay. Debug events are opt-in. The event model
accepts metadata rather than arbitrary payloads or exception messages. Never
add folder keys, FolderIds, file contents or record bodies to diagnostics.

The diagnostic journal defaults to seven files of at most 14 MB each. Files
containing network addresses expire at 24 hours on the next write. Collection
also filters events to the last 24 hours. Debug disk writes are synchronous;
only emit progress at bounded intervals rather than for every transferred chunk.

Run `build/Collect-Diagnostics.ps1 -Logs <log-directory> -Destination <new-zip>`
on both machines immediately after reproducing. It collects only structured
debug JSONL and environment metadata. Add `-IncludeNetworkAddresses` when
investigating routing. It excludes settings, databases, certificates, keys,
arbitrary logs and crash dumps. Review the bundle before sharing it.
The collector prints the number of included files/events and refuses to create
an archive with no recent valid debug events. If it reports no files, use the
exact `LOGS` directory printed by the probe, including its process-ID child
directory. Collect after the failed run, not before it. Check both PCs' clocks
if every event is filtered as expired.

Compare timestamps and session IDs. A transport failure before folder auth is
different from a rejected folder proof, approval wait, missing block, or failed
filesystem apply. Keep the original error code when reporting a failure.
Those production-stage event hooks are added with their implementing phases;
the QUIC probe already records start, TLS authentication, completion and errors.

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
| `directory.rebase_pending` | A received original subtree still needs causal verification. `itemsDeferred` distinguishes waiting from budget continuation. |
| `directory.parent_roots_pending` | At least one eligible original parent root remains unproven. Metadata recovery or ordinary child receipt may be required. |
| `directory.parent_roots_complete` | Every eligible root is covered by authenticated current children for this attempt. Final commit still revalidates the witnesses. |
| `directory.parent_clock_template` | Parent clocks were absorbed before publication. `proven-root-floor` means all eligible roots were proven. |
| `directory.publication_retry` | `errorCode` starts with the failing stage, such as `parent-roots`, `history` or `commit`, followed by the exception category or `pending`. Inspect native error and retry delay. |

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
