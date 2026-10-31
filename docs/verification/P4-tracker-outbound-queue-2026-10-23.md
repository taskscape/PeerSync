# Tracker outbound reliability, 2026-10-23

The tracker previously used a bounded channel with `DropWrite` and returned
the result of `TryWrite` as proof that a relay or punch request was queued.
When that channel was full, it could silently discard a request while the
caller waited up to 30 seconds for an offer that the server never received.
Folder and address refreshes also queued entire announcements, allowing
outdated snapshots to occupy the same bounded channel.

The channel now uses wait-on-full semantics for critical requests. A full
queue returns `false` and emits `tracker.outgoing_full` with only the request
kind. Refreshes set one pending flag and wake the writer; it reads the latest
announcement when it can send, including after a full queue drains. Reconnect
drains stale requests and starts with a fresh snapshot. A failed writer now
ends the tracker session instead of leaving an inbound stream reporting a
connected tracker with no working outbound path.

The focused queue tests cover full-queue refusal, latest-snapshot coalescing,
reconnect reset and an idle writer wake. The local Net suite passed 57/57;
the rendezvous suite passed 17/17, including the loopback mTLS tracker and
end-to-end relay flow. The Release solution build passed with zero warnings
and errors. These are local checks, not production-server or physical-WAN
evidence; the P4 gate remains open.
