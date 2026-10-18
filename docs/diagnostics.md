# Debugging deployments

The product is in P0. The production service, tracker, relay, CLI and installer
are scaffold projects and do not synchronize files yet. The P0 probes below
are deployable test executables, not a v1 release.

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

Compare timestamps and session IDs. A transport failure before folder auth is
different from a rejected folder proof, approval wait, missing block, or failed
filesystem apply. Keep the original error code when reporting a failure.
Those production-stage event hooks are added with their implementing phases;
the QUIC probe already records start, TLS authentication, completion and errors.

## P0: two-machine QUIC test

After `dotnet build -c Release`, copy the entire output directory
`spikes/QuicBasics/bin/Release/net10.0-windows10.0.20348.0/` to a machine with
.NET 10, or self-contained publish with `dotnet publish spikes/QuicBasics -c
Release -r win-x64 --self-contained -o artifacts/deploy/QuicBasics`.

On the receiving machine: `QuicBasics.exe listen 47821 1073741824`.
Allow inbound UDP 47821 for this executable in the test environment. On the
sender: `QuicBasics.exe dial <receiver-IP> 47821 1073741824`. A zero exit means
one GiB was exchanged, the receiver's SHA-256 matched, and both certificates
were presented. Save each process's JSON result and `logs/debug*.jsonl`.
Self-signed certificates are created per probe run and deleted afterwards.

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

The probe's self-signed server certificate is accepted by the probe client.
The production server will use its configured machine-store certificate.
Do not use the probe to relay files or expose it as the production service.
