# Fixed SCM authenticated TCP and QUIC probe — 2026-10-24

The installed `PeerSync` service remained Running as the fixed SCM instance
while a separate physical peer was connected. Its installed service version was
`1.0.16+24ae22395f1e43ebdcf5b16e50615513a6df47c2`. The selected source
was the existing 82-byte `first-pc-check.txt`, SHA-256
`979EF36BBE2399AEEC99C226BB8F12C9FEF41C929A2EE5F090B07A91E69903B4`.
No installed service binary, folder settings or host clock was changed.

`spikes/InstalledServiceProbe` and `build/Test-InstalledServicePeer.ps1` now
accept `-Transport Tcp` as well as the existing QUIC default. The probe chooses
an ephemeral P-256 device identity, creates its own temporary state/share,
injects an 11-minute peer clock skew, and uses the default installed-service
client that verifies SCM owner and kernel PID. It checks only the generated
peer's status when another peer is already active, requires a missing target
during skew, waits for an accepted/connected recovered session and a native
SHA-256 match, then checks the installed `ListPeers` transport and disconnect.
The original quiet-baseline path still additionally checks global production
availability. The probe executable SHA-256 was
`0EBD247953EC4A23EF14023136CDC00DFA0EADB056A023DC25F7B79A4F05CA88`.

The focused Release build passed with zero warnings and errors. The first TCP
attempt exposed a probe-control issue: a correct clock rejection closed the
direct TCP dial with `IOException` before the harness could inspect status.
The service journal showed TCP Hello and folder authentication followed by
`clock.blocked` with `Skewed`; existing sessions stayed connected. The harness
now tolerates that narrow close only at the skew step and still requires the
verified client to observe the 11-minute block. The repaired focused runs were:

```powershell
./build/Test-InstalledServicePeer.ps1 -FolderPath 'C:\PeerSync-Test\Physical-Two-PC-20261024' -FileName 'first-pc-check.txt' -Transport Tcp
./build/Test-InstalledServicePeer.ps1 -FolderPath 'C:\PeerSync-Test\Physical-Two-PC-20261024' -FileName 'first-pc-check.txt' -Transport Quic
```

Both runs exited zero. Their final JSON reported `scmInstanceVerified`,
`authenticatedClockRejected`, `recoveredTransferVerified` and
`disconnectObserved` true, `bytes: 82`, `quietBaseline: false`, and the exact
selected transport (`Tcp` and `Quic` respectively). TCP elapsed 0.802 seconds;
QUIC elapsed 0.792 seconds. The fixed service journal correlates Hello,
folder authentication, authenticated session, `service.folder_peer_ready`,
`service.folder_sync_pass` and disconnect for TCP session
`7c4a3975-b617-823a-d017-b35ceb7e08ea` and QUIC session
`fc1c4e83-9cd1-dbd5-86b3-3e6e03762103`. These are loopback sessions to the
real installed service, not a second physical PC or a WAN path.

The helper removed its temporary firewall rules for all three attempted ports
(40110, 43456 and 36498); no matching rules remained. The service stayed Running and the
two previously connected physical-peer status rows remained connected. The
24-hour fuzz process was still live after the probes. The test source's large
other files may begin transferring to the disposable peer before it exits;
the probe accepts only the named file's native hash and removes its own
temporary share after exit.

This closes the missing fixed-SCM **TCP transport observation** alongside the
existing QUIC check. The subsequent [native control lifecycle matrix](P2-native-control-lifecycle-2026-10-24.md)
closed the remaining P2.5.7 control evidence. Gate P2 still requires the
three-PC corpus and 72-hour physical soak. Independent WAN/NAT gates also
remain open.
