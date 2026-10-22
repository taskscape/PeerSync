# P3 installed two-machine virtual sync smoke

This is supplemental validation of the guest's signed P3 setup from PR #47,
merged as `4879691`. The host used its pre-existing P2 local installation;
its exact source revision was not recorded. This also checks protocol
interoperability across those installed builds. It does
not satisfy Gate P3's physical two-PC tester flow, Windows 11/Server 2022
installer matrix, or rendered standard-user authorization check.

The host ran the installed `PeerSync` Windows service on Windows 11. The
Hyper-V Windows Server 2025 evaluation guest (build 26100), initially clean
before earlier installer smoke tests, reinstalled the signed development x64
setup silently after `--remove-data` and uninstall.
Setup exited 0 and its service reached Running. The guest's installed app
helper granted its service SID access to a new folder, and the installed CLI
joined the host's existing test share with a read-write key. The guest
configured the host's Hyper-V Default Switch address as a predefined host;
the host's TCP listener was reachable before joining. The share key is not
recorded in this document or diagnostic logs.

Through the installed services, a 76-byte host marker reached the guest with
SHA-256 `8A8745AA1647365F9C64DE2D129AF8C2C9F696BC1E7FEFD47B67DA100506D83A`.
A 52-byte guest marker then reached the host with SHA-256
`209F450383A2AAB4FDFE33E2FCB091C639AAD96684189D71E9A7BB23E1046AFA`.
A random 16 MiB host payload reached the guest in about 4.2 seconds with
SHA-256 `4DC1223C7FE0F4669EB6335B34104515EC2C581C80AED4FF4E841ECE73C2B5EF`.
Both services remained Running. Their `service.folder_peer_ready` and
`service.folder_sync_pass` journal entries recorded authenticated QUIC folder
sessions; the host journal recorded a changed item after the reverse transfer.
This verifies installed cross-machine data flow across a virtual LAN, not a
physical LAN or NAT path.

The three marker files remain in the host's `C:\PeerSync-Test\Share` and the
guest's `C:\PeerSync-P3-VM-Joined` for diagnosis. Automatic approval review
rejected their cleanup with `Remove-Item`, including a literal single-file
command, reporting only `blocked by policy`. No further deletion was attempted.
