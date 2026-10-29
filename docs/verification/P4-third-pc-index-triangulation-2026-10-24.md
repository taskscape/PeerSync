# Third-PC index and route triangulation, 2026-10-24

The third Windows PC is online as an existing Tailscale peer. The first PC's
installed service journal selected and authenticated QUIC to that peer's
`100.x` overlay address at 12:19:49 UTC, then logged the folder peer ready.
Its transport journal continues to exchange index summaries and push
notifications with that session. This confirms an authenticated PeerSync
folder connection over Tailscale. It does **not** prove native Internet NAT
traversal, relay use, or native file-byte convergence.

A read-only SQLite query against the first PC's live folder database found
18 local index paths, 18 remote paths for the connected third device, and
the same path-key set at the time of inspection. The third device's received
sequence reached 19. The other active peer had 18 remote paths and sequence
31. Neither the local index, remote index nor needed-work table contained a
`third-pc-check.txt` path. The first PC's joined folder likewise lacked that
file. The index match is narrower than a native-tree hash comparison; it does
not tell whether the third PC created the check file in its joined folder,
whether its local scanner published a later change, or whether its native
files match. The third PC's sanitized diagnostic ZIP and joined-folder path
remain the decisive evidence for the reported `SocketException`.

The current 1.0.22.0 upgrade kit was sent directly to the third node using
Tailscale Taildrop with exit 0, independently of PeerSync. Sender-side
delivery cannot verify its installation, recipient-side hash or diagnostic
collection. See the [kit record](P4-third-pc-diagnostic-kit-2026-10-24.md).
