# Selected peer route diagnostics, 2026-10-24

The installed 1.0.16 clients currently show authenticated peer clocks but no
selected route in `psp status`. A LAN discovery event alone does not prove that
the LAN socket carried the sync. This source change records the winner of both
directions of the connection manager:

- `connection.candidate_selected` now includes the selected outgoing
  candidate's exact remote address and port when the candidate has one.
- `connection.incoming_selected` records the authenticated incoming winner's
  session ID and QUIC/TCP transport with its actual remote socket address and
  port. A rejected duplicate emits no selected event. Relay reports the Relay
  transport without inventing a direct peer endpoint.

The local raw journal retains network addresses for at most 24 hours. The
existing diagnostic bundle sanitizer omits address and port by default and
includes them only with `--include-network-addresses`; it also omits folder
keys, FolderIds and native paths. Match a selected event to the transport and
folder-session events by session ID, then inspect the host route and interface
to establish whether the selected socket was on a physical LAN. A private IP
or a `public` candidate label alone does not prove the physical path.

Focused validation on current source:

- `ConnectionManagerTests`: 15/15 passed, including exact outgoing selected
  endpoint/transport correlation and an incoming duplicate-loser exclusion.
- `PeerSync.Net.Tests`: 58/58 passed, zero skipped.
- `PeerSync.Service` Release build: zero warnings and zero errors.
- `DiagnosticBundleCollectorTests`: 3/3 passed, including default address
  omission and explicit opt-in inclusion.
- The source `psp peers <folder> --json` CLI was built in Release with zero
  warnings/errors and queried the installed first-PC service. It reported
  `transport: Quic` for the enrolled second-PC peer. This wire field was
  already supplied by the service; the CLI now exposes it in JSON and text.
- `PeerSync.Cli.Tests`: 6/6 passed, zero skipped.

This change is local source only. The installed first and second PCs have not
been upgraded to a build containing these events, so their existing two-way
file transfers still do not establish which candidate was selected.

Later signed 1.0.25.0 installs on both physical PCs produced a [correlated
selected QUIC LAN session and verified native-byte transfer](P2-physical-selected-route-2026-10-25.md).
The source-only statement above describes the initial 2026-10-24 check.
