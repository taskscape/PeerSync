# Native WAN field kit staged on three physical PCs

On 2026-10-28, the field kit built from tracked revision
`34d2585574040b0e930fba4d8d5988bed2c59c8b` was staged separately from
the active LAN soak kit. Its archive is
`artifacts/deploy/PeerSync-physical-gates-34d25855.zip`, SHA-256
`9ABDB7894C3066755A7E77E1ED4DD611906AA6BBF599831ED6F04F0B259F4008`.
The archive manifest limits test-tree content to 10 GiB, active execution to
three hours and requires three capture intervals. All 11 manifest file hashes
were verified after extraction on each host.

| Host | Verified PeerSync service | Kit destination |
| --- | --- | --- |
| `R5-7600X` | 1.0.43.0 Running | `C:\PeerSync-Gate-Evidence\20261028-wan-kit-34d25855` |
| `DESKTOP-BN535U0` | 1.0.43.0 Running | `C:\PeerSync-Gate-Evidence\20261028-wan-kit-34d25855` |
| `LARKBOX-X-1` | 1.0.43.0 Running | `C:\PeerSync-Gate-Evidence\20261028-wan-kit-34d25855` |

The native-WAN isolation helper in all three copies retains its PeerSync-only
overlay block for a 45-minute read-only capture grace after the active window.
This exceeds the observed 29-minute third-PC 50,000-file tree scan. The local
receipt is retained at
`artifacts/field/physical-50k-20261027/wan-kit-34d25855-stage.json`.

The second PC's Windows PowerShell archive module could not load; extraction
there used the built-in .NET ZIP API, followed by archive and per-file SHA-256
verification. The third PC used the same verified extraction path. The active
LAN soak kit at `C:\PeerSync-Gate-Evidence\20261028-soak-kit` was not replaced.

Staging is preparation only. No native-WAN isolation, NAT matrix, relay load
or WAN soak was run by this step, and Gate P4 remains open.
