# Status session projection on installed peers, 2026-10-25

An installed first-PC `psp status --json` snapshot after a service restart
listed two connected rows for each of two remote DeviceIds under the same
folder fingerprint. The rows had distinct session IDs, while the service
journal separately recorded `connection.incoming_selected`,
`connection.candidate_selected`, `service.folder_peer_ready` and
`connection.duplicate_resolved` events under transport session IDs.

Source tracing distinguishes these identities. `ConnectionManager` keeps
one selected transport connection per remote DeviceId in its active map and
closes a losing candidate. `TransportFolder` registers a `PeerIndexSession`
for its initiated stream and another for the authenticated request handler.
`ControlStatusRuntime.ReadStatus` returns the ledger's clock-session rows;
`StatusCommand.ToJson` labels that array `peers` and emits each local
index-session ID. The two status rows therefore do not, by themselves,
establish two winning transport connections. The selected transport ID in
the journal is distinct and is the one to correlate across endpoints.

The [diagnostics guide](../diagnostics.md) and [user guide](../user-guide.md)
now state that distinction and direct operators to `psp peers <folder>
--json` for the device roster. This is a source-and-field-output diagnosis,
not a new physical duplicate-resolution stress test. Gate P2's connection
manager and physical timing checks remain open.
