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

## Superseding field guide revision

The initial archive's manual-operation example conflicted with the scheduled
launcher's scan window. Tracked revision
`d6ab32528b3b28184c5967fe43eebe1c364ecb7c` aligns the manual example
with the completed hourly snapshot and the minutes 35–50 operation window.
The rebuilt archive is `artifacts/deploy/PeerSync-physical-gates-d6ab3252.zip`,
SHA-256 `494736F215B1EE2B72F92BB42ED48896B2CCEBC49EB8EE83AE22286120424276`.
At 08:02 UTC on 2026-10-28, this archive and all 11 manifest file hashes
verified on the same three PCs at
`C:\PeerSync-Gate-Evidence\20261028-wan-kit-d6ab3252`. The manifest still
limits content to 10 GiB and active execution to three hours. The prior kit
remains untouched as historical evidence, and neither kit replaced the
scheduled LAN soak kit. The `d6ab3252` kit was prepared for the later WAN run;
it is superseded by the source guard below. Its
machine receipts are retained at
`artifacts/field/physical-50k-20261027/wan-kit-d6ab3252-stage.json`.

The tracked helper has since gained a stricter physical-path preflight:
only the expected physical uplink may be Up, and routes to the nominated peer
LAN addresses are checked before and after the PeerSync restart. The staged
`d6ab3252` archive predates this change and must not be used for the WAN gate.
After the live LAN run finishes, transfer and hash-check the replacement kit
below on the test PCs before any native-WAN run. Keep the old kit as historical
staging evidence.

The updated guard parsed under Windows PowerShell 5.1. A read-only live route
control on the first PC accepted its sole Ethernet hardware uplink, then
rejected its forbidden source prefix. Isolated controls rejected a second Up
physical adapter and a peer LAN route through an unapproved adapter. These
checks do not substitute for the outside-PC preflight or a physical WAN soak.

The replacement ZIP has now been built locally from tracked source revision
`12417b919a2cfb99e4f77bf312a8f3c37b90dd68` at
`artifacts/deploy/PeerSync-physical-gates-12417b91.zip` (39,127 bytes,
SHA-256 `8ED8348D45F2FFF063906C8B40E052A39780B401B3094E91F95919EE31B0B78B`).
The archive reopened with all 13 entries and all 11 manifest file hashes
matching; its manifest retains the 10 GiB content and three-hour active caps.
The package is local only. Copying it to the physical PCs and verifying its
external hash there remain pending until the active LAN run has finished.

A subsequent route inspection found that Windows `Find-NetRoute` emits the
selected source-address object and the route object separately. The native-WAN
helper now checks that both objects select the same interface and that the
actual route uses the expected uplink for rendezvous and the allowed uplink or
blocked overlay for nominated peer LAN addresses. Windows PowerShell 5.1
parsed the update and a read-only live route control passed. The local
`12417b91` ZIP predates this correction and is superseded before field use;
a new source-pinned ZIP must be built and verified after this source change is
committed.

The corrected, locally built package is
`artifacts/deploy/PeerSync-physical-gates-78bb4ae8.zip` from tracked
`78bb4ae8ecffcdb0b638154c42c686e449fa80e3`: 39,238 bytes, SHA-256
`06818BAF58D259F46A4D1924079CBA5A88A82D443C54850715BBBE3303CACF5F`.
It reopened with 13 entries and all 11 manifest hashes matching; the manifest
still caps content at 10 GiB and active execution at three hours. This is the
replacement to stage on the physical PCs after the live LAN soak. It has not
been transferred or executed, and the native-WAN gate remains open.
