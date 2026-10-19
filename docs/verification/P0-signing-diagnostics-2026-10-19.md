# P0 signing and diagnostic follow-up — 2026-10-19

This follow-up publishes the real two-PC diagnostic repair and closes the
remaining CI signing implementation. It does not implement the production
sync service or replace the physical LAN/NAT requirements.

## Signing verification

`build/Test-Signing.ps1` creates a fresh RSA-3072/SHA-256, non-exportable
development certificate in `LocalMachine\My` for one day. A public-only copy
is temporarily trusted in the machine root store using the noninteractive
store API. It signs and RFC3161-timestamps a separate copy of the published
probe through the same `build/Sign.ps1` used for manually supplied identities.

The smoke requires a Valid Authenticode signature from the exact test identity
and a timestamp. It modifies a byte in the first PE section and requires
HashMismatch. Finally, it removes the changed EXE, its own trust entry and
the temporary signing certificate/private key. JSON evidence records these
checks. Only the signed smoke EXE and public CER can be uploaded; no private
key is exported. Published deployment probes remain separate from this test.

Local x64 result: passed; signature Valid; timestamp present; tamper status
HashMismatch; privateCertificateRemoved/testTrustRemoved true. Remote CI and
merge are pending; no acceptance box is ticked from this local result alone.

The first local smoke was interrupted while CurrentUser root import displayed
a trust-consent prompt. Its temporary signing certificate, thumbprint
`3DA2693E0CF783E8AD6CB632A078A622059C0023`, remains in LocalMachine\My with
no root trust entry. Automatic approval review rejected its explicit cleanup
with only "blocked by policy"; removal requires the user's interactive action.
It is non-exportable, expires after one day, and is separate from the user's
existing development signing certificate. Subsequent smoke cleanup succeeded.

## Diagnostics and external evidence

The QUIC probe now has independent wait/transfer deadlines, bounded progress,
specific QUIC errors, separate HRESULT/native/protocol numbers, phase/byte
counts, exact log paths and validated CLI arguments. The collector rejects
empty or expired/invalid-only event input and includes collection counts.
The local regression covers a delayed receiver start, transfer timeout,
progress, error numbers and malformed arguments.
Older Rendezvous99, QuicPunch, service and gRPC error paths also now retain
HRESULT separately from native socket errors. The P0 runner requests HTTP/2
after its server has stopped and requires a distinct native WSAECONNREFUSED
(10061), alongside the managed HRESULT and a failed mutual-TLS result.

See [the original failure investigation](P0-two-machine-troubleshooting-2026-10-18.md)
and [the subsequent cross-network results](P0-cross-network-2026-10-19.md).
The user has now completed 1 GiB over Tailscale, corroborated by the receiver
journal, and public Windows Server HTTP/2 and HTTP/3 mutual-TLS probes.

## Verification status

- Final local x64 `Verify.ps1 -Runtime win-x64 -SkipFuzz`: passed in 86.53 s.
  Core 4/4 and Crypto 5/5 cases, both 1 GiB QUIC address families, session/hash
  checks, HTTP/2 and HTTP/3, pipe checks, watcher stress, the timeout regression
  and self-contained publishing passed. Locale/empty-bundle collector checks
  passed. Local fuzz was explicitly skipped; it remains required in remote CI.
- Final x64 signing smoke: passed with timestamp, tamper rejection and cleanup.
- Local Arm64 compile/publish verification: passed in 76.14 s before the last
  error-label consistency edits; its signing/tamper/cleanup smoke also passed.
  Final-head Arm64 compilation will be repeated by CI. Arm64 execution remains
  unverified.
- Scoped Roslyn diagnostics for Rendezvous99, QuicPunch, DeviceCert and
  SpikeSupport: no warnings/errors and no workspace load diagnostics.
- Focused unreachable HTTP/2 request against a closed loopback port: exit 1,
  RpcException/Unavailable, managed HRESULT -2146233088 and native socket
  code 10061 retained separately. The new equivalent Run-P0 regression will
  run in CI; the local runner had loaded before that script addition.
- Workflow YAML/signing-step validation and PowerShell syntax checks passed.

Remote CI and merge remain pending. The required ten-minute fuzz remains a
separate mandatory CI job and has not been shortened or removed.
