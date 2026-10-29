# Correlated selected-path diagnostics, 2026-10-24

The first installed PC's sanitized two-PC diagnostic bundle showed LAN QUIC,
public QUIC/TCP and relay attempts around the second-PC session, followed by
authenticated QUIC traffic. Its `connection.candidate_started` events have no
session ID because authentication has not happened yet. The bundle therefore
did not identify which candidate produced a connected status session. The
current installed 1.0.16 client does not contain this follow-up change.

`ConnectionManager` now writes `connection.candidate_authenticated` after a
candidate has completed certificate-bound peer authentication, and
`connection.candidate_selected` when that candidate remains the installed
outgoing session after losing attempts drain and caller cancellation is
checked. Both events carry its authenticated `sessionId`, bounded
`candidate.<source>[.<transport>]` operation and transport. Relay is labelled
`Relay` even when its candidate has no endpoint metadata. A source without a
declared transport derives the actual QUIC/TCP/relay type from its session.
The two events carry no address, port, device ID, folder ID, key or content.
The existing sanitized bundle allowlist retains their operation, session ID
and transport while continuing to omit address fields by default.

The selected event describes an outgoing choice at the time it returns; a
later incoming duplicate can replace it. The operator should match the
selected event to the connected status session ID and compare both endpoints'
logs. An authenticated event alone does not prove that candidate was selected.
The `public` label identifies a tracker/catalog candidate; it does not by
itself prove that packets crossed an outside network rather than a local
hairpin or overlay. Physical route evidence remains required for P4.

The focused `ConnectionManagerTests` passed 14/14, including an assertion
that authenticated and selected events identify the public TCP winner by the
same session ID without an endpoint address. The full Net suite passed 57/57,
and the Release service build passed with zero warnings and errors. This is
local source verification. The new event has not yet been deployed to the two
physical PCs, and their current path remains unverified until a subsequent
correlated field capture.
