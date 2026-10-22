# P2 authenticated QUIC transport — 2026-10-21

This is local implementation evidence for the P2.2 QUIC and handshake work in
the current working tree. It is not a hosted CI result, an installed-service
deployment, or the full Gate P2 result.

The listener and dialler require mutual TLS 1.3 with ALPN `psp/1`. DeviceIds
come from the certificate public keys, and the caller's pin policy must accept
the remote DeviceId before the control exchange begins. The control stream
exchanges correlated Hello messages and direction-bound folder proofs. A
folder is admitted only when both peers prove its key and both local policy
callbacks allow it.

Each admitted folder gets one bidirectional index stream. The dialler opens
the stream and primes it with a correlated IndexSummary request; the listener
accepts it and answers through the same stream. The stream reader routes
responses by request ID and handles incoming Hello, IndexSummary, and
IndexRequest calls concurrently, with at most 64 outstanding requests per
folder. Each connection shares a configurable bounded pool of reusable
bidirectional block streams (8 by default); every request is validated and
the returned bytes must match the requested BLAKE3 hash and length. Close
frames carry a reason and trigger session cleanup. Session disposal cancels
and drains stream work before disposing folder handlers.

Debug diagnostics record request start/completion, byte and item counts,
elapsed time, remote endpoint, authentication outcomes, safe error codes, and
session IDs. They do not record folder IDs, paths, keys, block hashes, or
payloads. The loopback test reads the resulting JSONL and checks these privacy
boundaries; the service host still needs to create and pass its production
`DiagnosticJournal` when the P2.5 host wiring lands.

| Local verification | Result |
| --- | --- |
| `PeerSync.Net` Release build and configured Roslyn analyzers | Passed; zero warnings/errors |
| `PeerSync.Protocol.Tests` Release | 77 passed, 0 failed |
| `PeerSync.Net.Tests` Release | 15 passed, 0 failed |
| `PeerSync.Engine.Tests` Release | 1013 passed, 0 failed |
| QUIC loopback | Mutual TLS and certificate identity; denied folder admission; pin mismatch rejected; concurrent correlated Hello/index requests; signed index page; two block reads over the pooled stream; close and disposal |
| Roslyn affected-symbols for changed public options/folder surfaces | No workspace diagnostics; 9 dependent projects returned for `QuicPeerOptions`, 9 for `AuthenticatedFolder` |

Remaining P2.2 work includes TCP/TLS fallback and the connection manager. The
current QUIC tests run on this Windows host only. Hosted CI for this working
tree, deployed Windows Server interoperability, and external network/NAT
validation remain unverified and are still required by their plan gates.
