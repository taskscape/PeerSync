# Physical fresh Add/Join after connected-folder repair, 2026-10-25

Both physical LAN PCs were running the development-signed PeerSync 1.0.26.0
service under `NT SERVICE\PeerSync`. They already had an authenticated peer
session for an older shared folder. This check exercised **new folder membership
on those running services**, the case that failed before the membership refresh
and reconnect repair in `0153358`.

The first PC created a new, isolated folder under
`C:\PeerSync-Test\FreshJoin-3e316e834e6b-First` with one 4 KiB random file.
The installed `psp add --json` command succeeded. Its disposable share key was
passed to a short-lived process on the second PC through the existing DCOM
management session; the key was not printed or stored in the retained result
file. The second PC's installed `psp join --json` exited 0 for
`C:\Temp\PeerSync-FreshJoin-3e316e834e6b-Second`. Both test folders granted
only the service's required Modify access in addition to their inherited ACLs.
The remote join result contains only `exitCode=0` and `success=true`.

The first file arrived on the second PC with SHA-256
`2C101ADA3BA7C5006C051AFB6A26D66E5ABE9438F049305925AC84A5E03EC404`
on both native copies. A second 4 KiB random file was then created locally by
a process on the second PC. It was indexed there and arrived on the first PC;
both native copies have SHA-256
`C36DEC23F9ADB4D13E25EB01E7B8F0BC7B260A7F9A4292FEDF3ADEA2961CBD0A`.
The first PC's installed `psp peers` returned one peer for this new folder
using authenticated QUIC. Both services remained Running. The bounded local
[verification record](../../artifacts/field/fresh-add-join-20261025/verification.json)
retains versions, service states, transport and all four native hashes without
the capability key or full FolderId.

This closes the installed-service follow-up for adding and joining a fresh
folder while an older folder is already connected. It is a CLI-assisted two-PC
LAN check. It does not prove the app's unassisted ten-minute flow, approval UI,
WAN behavior, a third LAN PC, or the 72-hour soak. The disposable test folders
and their memberships remain available for follow-up; their contents are only
the two random test files. Windows process-creation telemetry may retain the
encoded short-lived join command, so its synthetic folder capability must not
be reused for private data.
