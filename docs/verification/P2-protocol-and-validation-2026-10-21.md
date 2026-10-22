# P2 protocol and ingress validation

Committed implementation `5bc61be32238d51d9bf03f2431f334c75d72ad0e` passes the
canonical `./build/Verify.ps1` with its full default runtime scope and 600-second
fuzz duration. The actual run completes in 610.7394997 seconds, exit 0.

The new wire schema covers all twelve PSP/1 message families. Framing accepts
canonical body-length varints up to 16 MiB, handles segmented/incomplete input,
rejects overflow/truncated EOF, honors cancellation and preserves unread data
when an iterator is disposed early. Decoding rejects unknown types and
malformed Protobuf with safe codes. Original signed bodies remain exact bytes.

Ingress validation bounds records/requests before generated repeated-message
allocation. Folder-scoped traffic requires the supplied authenticated folder
context. Original record signatures are verified before opening metadata;
path, vector, author, time, attributes, type, chunk class/hash/length and total
checks precede engine use. Nested entries are preflighted after authentication,
deprecated groups are rejected and ordinary unknown fields remain supported.
Hello text/time/proof shapes, sequence ranges, bitmap/block sizes and BLAKE3
response digests are bounded. A rejected connection cannot admit later frames.

The violation guard binds a caller-supplied certificate-derived identity,
never Hello's identity. A real loopback TCP test observes a framed close reason
and EOF, with the strike attributed to the frozen supplied identity. Another
test proves strike-storage failure still closes the owned connection. The
current working tree now adds actual TLS certificate derivation, pin checks,
folder proof exchange and QUIC index/block streams; its separate local evidence
is in [the authenticated QUIC transport report](P2-authenticated-quic-transport-2026-10-21.md).
That report does not claim hosted CI, installed-service integration, or the
full P2 gate.

| Actual verification | Result |
| --- | --- |
| Release build and Roslyn Protocol analyzers | Zero warnings/errors |
| Core / Crypto / Protocol / Net | 144 / 15 / 71 / 9 passed |
| Engine / Storage | 1013 / 300 passed |
| Control.Contracts / Service / App | 39 / 8 / 13 passed |
| Total canonical unit results | 1612 passed, no skipped/failed results |
| Real-engine Simulation | 13 methods, 6 retained regressions, maintenance |
| Generated smoke | 8 flat / 4 hierarchy cases |
| Five-target fuzz | 214811 seeds, 600.0052941 s, zero crashes |
| Native diagnostics/probes and win-x64 probe publishing | Pass |

The framing target supplements all four existing fuzz targets. A prior focused
30-second run also passes 10751 seeds with zero crashes. No new package was
needed: System.IO.Pipelines is available in this .NET 10 reference pack.

Fresh source/run/counters/TRX and actual fuzz data are independently audited
and preserved in `artifacts/p2-protocol-canonical-5bc61be/`, with the original
console log `artifacts/p2-protocol-full-verify.log`. The canonical runner's
legacy `phase=P1` label is not a P2 gate claim; its recorded source and actual
test results above establish this scoped validation. The full P2 gate, real
service deployment, TLS/folder-auth integration and physical LAN check remain
open. The [approved hosted timing exception](../proposals/P1-correctness-first-implementation-phasing.md)
remains visible; no CI performance tuning is resumed.
