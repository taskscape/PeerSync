# P0 two-machine QUIC troubleshooting — 2026-10-18

This investigation is local, with user-supplied two-machine evidence. It is not
a successful physical LAN/NAT test, Windows Server deployment, or CI rerun.

## Evidence

- Both user ZIPs (`C:\Temp\logs-machine-1.zip` and `logs-machine-2.zip`)
  contain only `environment.json`, with no structured debug events. Their
  collection timestamps are 17:57 and 19:13 UTC. The local ZIP predates the
  local 20:42 UTC failures. The exact collection commands are unknown.
- Both inventories have .NET 10 installed and active Tailscale adapters.
- The local run at 20:42:06 UTC failed before `transport.authenticated`, after
  about ten seconds. Its generic QuicException/HRESULT cannot establish a cause.
- The local run at 20:42:36 UTC authenticated successfully, attached a session,
  then failed at 20:45:23 UTC, about 167 seconds later. No transfer-completed
  event is present. The original logs did not retain bytes, failure stage, or
  QuicError. The receiver's corresponding debug events are unavailable.
- A current route lookup sends `100.126.96.2` through the Tailscale interface.
  A Tailscale ping succeeded over a direct overlay connection. This establishes
  current overlay connectivity, not UDP 47821 reachability at the earlier run.
- The original probe had one three-minute deadline covering both listener
  waiting and transfer. This can leave less than three minutes for the transfer.
  Receiver timeout is a plausible cause of the later pair of errors; it is not
  proven for that remote run without the receiver journal.
- Each probe creates its own CNG-backed TLS certificate. The later successful
  TLS authentication rules out a missing TLS certificate for that connection.
  Code-signing trust on the receiver is unrelated to mutual TLS in this spike.

## Repair and local validation

The probe now reports its exact log directory before connecting, phase, byte
counts, bounded progress, specific QuicError, HRESULT and protocol error codes.
NativeError contains a native socket code only when such an exception exists.
Transfer timeout defaults to 900 seconds, starting after authentication;
listener wait remains 180 seconds. CLI validation reports usage for missing
arguments and trailing backslashes in numeric arguments.

The collector rejects missing/expired/invalid-only logs rather than silently
producing an environment-only archive. Metadata includes collection counts.

- Full solution Release build: zero warnings/errors.
- Roslyn diagnostics, Core and QuicBasics: no warnings/errors; workspace load
  diagnostics empty. Context/impact bundle tools returned invocation errors;
  source reference inspection and the full build covered the additive metadata
  change instead.
- `build/Verify.ps1 -Runtime win-x64 -SkipFuzz`: passed in 80.32 seconds.
  Core 4/4 and Crypto 5/5 tests passed; both 1 GiB loopback address families,
  certificate/session/hash correlation, HTTP/2 and HTTP/3, control-pipe checks,
  the watcher stress, and self-contained x64 publishing passed. The known
  source-port binding failures remain expected failures. Ten-minute fuzz was
  explicitly skipped for this diagnostics repair; prior CI is separate evidence.
- Collector regression: live-reader sharing, exclusion and date filtering pass
  in pl-PL/en-US; missing and expired/invalid-only logs are rejected.
- `build/Test-QuicDiagnostics.ps1`: passed. Receiver waited seven seconds before
  connection despite a six-second transfer timeout, then streamed until its
  transfer timeout. Progress and a receiver `ProbeTimeout` at `receive` were
  logged; the sender recorded `QuicException.StreamAborted` with application
  error 1. Receiver HRESULT 0x8013153B and sender HRESULT 0x80131620 reproduce
  the user's generic error pair. The regression is included in future Verify
  runs. It allocates only bounded streaming buffers, not a TiB file.
- Refreshed self-contained x64 package: signed and timestamped using the
  existing local development certificate, Authenticode status `Valid`. Two
  packaged processes completed a 16 MiB loopback transfer with matching session
  IDs and verified hashes; its bundled collector retained nine events from two
  debug files. No private key files are included. The manifest identifies the
  dirty working tree rather than claiming a committed release.

## Next external check

Use the same refreshed self-contained probe on both machines. First transfer
16 MiB with both sides using 16777216 bytes; then restart the receiver and
repeat with 1 GiB. Collect each exact printed LOGS path immediately afterwards.
For P0.2.1, repeat against the receiver's LAN IP and record physical link speed
and throughput. Tailscale-only results remain overlay evidence. Physical NAT
matrix and final transport decision remain open.
Follow [the exact retry commands](../quic-two-pc-retest.md).
