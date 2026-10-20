# Clock acceptance under the approved validation split

The user explicitly approved the narrow clock phasing proposal on 2026-10-20.
The source design was updated first, then the decision log and affected P1
criteria. New P2.5.7 and an explicit Gate P2 line retain every actual installed
service/production authenticated transport requirement. Those checks are
unverified; the P1 fixtures and this audit do not substitute for them.

## Retained P1 criteria

P1.5.5a/b retain UTC Hello, both signed offsets, exact ten-minute boundary,
missing/invalid/extreme samples, monotonic elapsed checks and local clock jumps.
The peer-bound admission facade gets identity from its context, freezes input
pages, checks before reading/handoff and inside the writer transaction, and
rolls back records/needed/cursor/range work if admission changes. Public
identity-bound in-memory sessions exercise skew rejection, forged Hello,
disconnect/reset and fresh-Hello recovery. Raw local storage remains trusted
infrastructure, not a production network admission endpoint.

P1.5.5c1 retains immutable runtime snapshots, locally calculated signed offsets,
session/device separation, closed-reason preservation, reconnect clearing,
bounded history/truncation and no admission authority from display snapshots.
P1.5.5c2/a retain fixed production service/pipe names, read-only native SCM
configuration/status and account resolution, bounded native buffers and fail-
closed lookup. Actual pipe tests require matching owner and kernel server PID
before any RPC bytes, with mismatch, missing-target, cancellation/deadline,
caller authorization, streaming/backpressure/subscription and shutdown checks.
Raw fixture descriptors remain internal; no production endpoint override exists.

The same production WPF controller/window handles fixture GetStatus/WatchEvents,
numeric signed minutes/reasons, missing/invalid/disconnected states, truncation,
reconnect and cancellation. Generation/epoch/sequence guards reject stale
success. Empty status-only hosting explicitly reports production sync unavailable.

## Current authoritative evidence

PR #34's engine/in-memory admission and PR #37's clock/control implementation
have exact-tree green PR/main merge audits. The later exact green main #38
run 39374744845 includes the unchanged clock/control code. An exact method-name
audit of downloaded actual Engine/Core/Contracts/Service/App TRX matches all
66 attributed test declarations in ten clock/control files to 116 passing
parameterized results; zero methods are missing. The result is retained in
`artifacts/ci38-main-clock-method-coverage.json`. Native SCM verification uses
the read-only RpcSs seam and a unique missing-service name; it does not claim
the fixed PeerSync installation has been exercised.

All eight actual main WPF PNGs in `artifacts/ci38-main-x64/clock-ui/` were
visually re-inspected: runtime +11.00 minutes/exchange blocked, accepted 0.00
recovery, service-unavailable with stale rows cleared, negative skew, awaiting
Hello, invalid timestamp, invalid elapsed and truncation. Expected text is
readable; the real UI shows explicit production unavailability. Render tests
also assert control text/pixels in CI. These are native WPF fixture renders,
not deployed service screenshots.

Therefore P1.5.5b, c1, c2a, c2, c and their P1.5.5 parent satisfy all retained
P1 criteria after the explicit approval. The unchanged production semantics
need no new C# test run for this document-only phasing edit. P2.5.7 still
requires the actual fixed installed service/configured account/default client,
real certificate-bound QUIC/TCP/folder sessions, incoming/outgoing skew blocking,
numeric UI/reconnect recovery, running/stopped/missing/wrong owner/PID,
cancellation/caller authorization and service lifecycle evidence. Use generated
fixtures/injected peer clock samples and preserve host clock/existing installs.
All remaining P1 corpus/campaign criteria and later gates remain mandatory.
