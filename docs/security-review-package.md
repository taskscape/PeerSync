# P5 independent security review package

This is a handoff map for the P5.2 reviewer, not a completed independent
review. Review the [design threat table](../Resilio-style%20P2P%20Sync%20for%20Windows%20Implementation%20Plan.md#threats-and-mitigations)
against the implementation and record each finding, severity, reproduction,
fix or accepted rationale before closing P5.2.

| Threat or boundary | Primary implementation to inspect | Existing local evidence / open question |
| --- | --- | --- |
| Network eavesdropping and certificate substitution | `src/PeerSync.Net/Quic/`, `src/PeerSync.Net/Tcp/`, `src/PeerSync.Net/Security/PeerHandshake.cs` | P0 QUIC and installed-host peer tests cover authenticated sessions. Independently test a mismatched certificate and protocol downgrade on both transports. |
| Tracker/relay metadata and folder proof | `src/PeerSync.Rendezvous/`, `src/PeerSync.Net/Rendezvous/`, `src/PeerSync.Net/Relay/` | P4 local and installed tests cover presence and relay behavior. A certificate-bound oversized relay request produces a sanitized central ZIP with the session/status and without the relay token or network address; see [local evidence](verification/P4-probe-correlation-2026-10-23.md). Independently verify that neither raw server logs nor wire payloads carry folder keys or file bytes. |
| LAN observer | `src/PeerSync.Net/Discovery/LanDiscoveryPacket.cs` and `LanDiscovery.cs` | P2 tests cover rotating HMAC tags. Verify adjacent-hour handling and that raw FolderIds do not appear in announcements. |
| Read-only or malicious peer | `src/PeerSync.Protocol/Signing/SignedRecordFactory.cs`, `src/PeerSync.Engine/Sync/NeededSet.cs`, `src/PeerSync.Engine/Hashing/` | Boundary and Engine tests cover signatures and content hashes. Attempt forged records, malformed signed directory graphs and corrupt block responses. |
| Leaked key, unknown or blocked device | `src/PeerSync.Service/Hosting/PeerNetworkRuntime.cs`, `InstalledFolderHost.cs`, `src/PeerSync.Service/Control/ControlStatusRpc.Mutations.cs` | Installed-host pending/approve/block test passed locally. Repeat on two physical PCs with RequireApproval enabled and check reconnect after block. |
| Compromised writer and hostile paths | `src/PeerSync.Engine/Archive/`, `src/PeerSync.Engine/Native/SafeFileTree.cs`, `src/PeerSync.Core/Paths/PathRules.cs` | Engine and fuzz tests cover archive recovery and path rules. Exercise a destructive signed update and restore visible versions through the installed UI. |
| Oversized or flooding input | `src/PeerSync.Protocol/Framing/FrameCodec.cs`, `src/PeerSync.Protocol/Validation/`, `src/PeerSync.Rendezvous/Hosting/RendezvousHost.cs` | Ten-minute fuzz and current 24-hour campaign cover decoder/validation. A hosted 123-request burst hits the 120-token HTTP limit, yields three 429s and one sampled warning while counting each denial; inspect per-IP and per-peer limits under concurrent public-network load. |
| Local control and key disclosure | `src/PeerSync.Service/Control/ControlStatusRpc.cs`, `ControlIdentityInterceptor.cs`, `ControlApiHost.cs`, `src/PeerSync.Crypto/` | Caller identity is enforced before RPC dispatch; `GetFolderKeys` is the explicit audited key-return RPC. Review pipe ACLs and verify denied standard users cannot obtain the key through status, events, dump or error detail. |
| Logs, support bundles and dumps | `src/PeerSync.Core/Diagnostics/`, `installer/Set-WerPolicy.ps1`, `docs/diagnostics.md`, `src/PeerSync.Service/Control/ControlStatusRpc.cs` | Structured events accept metadata fields and the bundle collector excludes keys, FolderIds, arbitrary logs and crash dumps. A verified-pipe integration test audits raw debug output after AddFolder, GetFolderKeys, JoinFolder and DumpIndex; see [control-log evidence](verification/P5-control-key-log-audit-2026-10-23.md). A local scan of three million-file source debug logs found 143,370 parseable events and no fixture path or forbidden identity/key fields; this is a spot check, not proof of absence. A clock-controlled test confirms that the journal's timer removes an idle IP-bearing log after 24 hours without another write; see [local retention evidence](verification/P4-idle-log-retention-2026-10-23.md). Both installers package an owned WER exclusion policy; the [local policy controls](verification/P5-wer-policy-local-2026-10-23.md) and [signed local client upgrade audit](verification/P5-installed-client-archive-2026-10-23.md) pass. The installed probe also screened 843 raw SCM service events after key export and index read, requiring the key-read audit and rejecting key text, FolderId and native path. Server 2025 deployment and uninstall checks remain open. The earlier [Server 2025 policy snapshot](verification/P5-crash-report-policy-2026-10-23.md) predates this change and cannot prove the installed result. Independently inspect other component logs, LocalDumps policy and the [approved dump boundary](proposals/P5-crash-dump-boundary.md) before closing P5.2. |
| Signed update publisher | `installer/Update-Helper.ps1`, `installer/Assert-UpdateSetupSignature.ps1`, `src/PeerSync.Service/Update/` | The local [rotation guard](verification/P5-update-publisher-rotation-local-2026-10-23.md) and [online-chain check](verification/P5-update-signer-chain-local-2026-10-23.md) pass with development signatures. Verify a revoked non-root release certificate, unavailable revocation endpoint, unrelated trusted signer, actual installed update and safe publisher rotation independently before release. |

For the crash-report boundary, run the packaged
[`Audit-CrashPolicy.ps1`](../installer/Audit-CrashPolicy.ps1) on each installed
client and Rendezvous host and retain its JSON with the review. The
[local audit evidence](verification/P5-crash-policy-audit-2026-10-23.md)
verifies the parser and local client state; it does not replace the installed
Server 2025 check or test privileged dump capture.

The P5 fuzz campaign, million-file fresh-peer run, physical two-PC approval,
public update-feed signer and 72-hour soak are
separate open gates. A reviewer should use the exact candidate commit and
record whether those results were available at review time. Do not mark
P5.2 complete from this package alone.
