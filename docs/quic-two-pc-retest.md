# QUIC two-PC retry

Extract the same refreshed self-contained win-x64 ZIP on both PCs. Run the
commands below from the extracted folder containing `QuicBasics.exe` and
`Collect-Diagnostics.ps1`. Keep every runtime file; .NET installation is not
required for this package. These are diagnostic probes, not a sync service.

No manual TLS certificate creation or trust-store import is needed. The
included development `.cer` is public and is only for optional Authenticode
trust verification; it is not used to authenticate QUIC peers.

## Receiver: the other PC

If the EXE moved to a new folder, run this once in administrator PowerShell
to allow only the sender's current Tailscale IP and the test UDP port:

```powershell
New-NetFirewallRule -DisplayName 'PeerSync QUIC probe retry' `
    -Direction Inbound -Action Allow -Protocol UDP -LocalPort 47821 `
    -Program (Join-Path $PWD 'QuicBasics.exe') -RemoteAddress 100.118.160.63 -Profile Any
```

The sender address was verified during investigation on 2026-10-18; replace
it if that PC's Tailscale address changes. Keep existing firewall policy enabled.

Then start a 16 MiB receiver:

```powershell
.\QuicBasics.exe listen 47821 16777216
```

Wait for `READY 47821`. Start the sender within three minutes. Once connected,
the transfer gets a fresh 15-minute deadline. Both processes print their exact
`LOGS` directory, and failures name the stage, byte count and QUIC error.

## Sender: this PC

```powershell
.\QuicBasics.exe dial 100.126.96.2 47821 16777216
```

Both processes should exit zero and print `verified: true` in their JSON
results. If this small test passes, restart both sides with `1073741824` in
place of `16777216` to test 1 GiB. Match the byte counts on both sides.
Do not type a trailing backslash after the byte count.

## Collect immediately after success or failure

On the receiver:

```powershell
.\Collect-Diagnostics.ps1 -Logs .\logs -Destination C:\Temp\logs-machine-2-retry.zip
```

On the sender:

```powershell
.\Collect-Diagnostics.ps1 -Logs .\logs -Destination C:\Temp\logs-machine-1-retry.zip
```

These commands collect runs from the extracted probe folder. For only one
run, replace `.\logs` with its exact printed `LOGS` path. Use a new destination
filename each time. The collector must report nonzero debug events; it now
rejects environment-only bundles. Retain both console results as well.

When finished, remove only the temporary rule created above:

```powershell
Get-NetFirewallRule -DisplayName 'PeerSync QUIC probe retry' | Remove-NetFirewallRule
```

This test uses Tailscale. It does not prove PeerSync's own NAT traversal or
physical LAN performance. To collect LAN evidence, repeat against the other
PC's LAN IP with a corresponding sender LAN IP in the scoped firewall rule,
and record both NIC link speeds and the measured 1 GiB throughput.
