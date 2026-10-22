# P5 independent security review package

This is a handoff map for the P5.2 reviewer, not a completed independent
review. Review the [design threat table](../Resilio-style%20P2P%20Sync%20for%20Windows%20Implementation%20Plan.md#threats-and-mitigations)
against the implementation and record each finding, severity, reproduction,
fix or accepted rationale before closing P5.2.

| Threat or boundary | Primary implementation to inspect | Existing local evidence / open question |
| --- | --- | --- |
| Network eavesdropping and certificate substitution | `src/PeerSync.Net/Quic/`, `src/PeerSync.Net/Tcp/`, `src/PeerSync.Net/Security/PeerHandshake.cs` | P0 QUIC and installed-host peer tests cover authenticated sessions. Independently test a mismatched certificate and protocol downgrade on both transports. |
| Tracker/relay metadata and folder proof | `src/PeerSync.Rendezvous/`, `src/PeerSync.Net/Rendezvous/`, `src/PeerSync.Net/Relay/` | P4 local and installed tests cover presence and relay behavior. Verify that neither service logs nor wire payloads carry raw folder keys or file bytes. |
| LAN observer | `src/PeerSync.Net/Discovery/LanDiscoveryPacket.cs` and `LanDiscovery.cs` | P2 tests cover rotating HMAC tags. Verify adjacent-hour handling and that raw FolderIds do not appear in announcements. |
| Read-only or malicious peer | `src/PeerSync.Protocol/Signing/SignedRecordFactory.cs`, `src/PeerSync.Engine/Sync/NeededSet.cs`, `src/PeerSync.Engine/Hashing/` | Boundary and Engine tests cover signatures and content hashes. Attempt forged records, malformed signed directory graphs and corrupt block responses. |
| Leaked key, unknown or blocked device | `src/PeerSync.Service/Hosting/PeerNetworkRuntime.cs`, `InstalledFolderHost.cs`, `src/PeerSync.Service/Control/ControlStatusRpc.Mutations.cs` | Installed-host pending/approve/block test passed locally. Repeat on two physical PCs with RequireApproval enabled and check reconnect after block. |
| Compromised writer and hostile paths | `src/PeerSync.Engine/Archive/`, `src/PeerSync.Engine/Native/SafeFileTree.cs`, `src/PeerSync.Core/Paths/PathRules.cs` | Engine and fuzz tests cover archive recovery and path rules. Exercise a destructive signed update and restore visible versions through the installed UI. |
| Oversized or flooding input | `src/PeerSync.Protocol/Framing/FrameCodec.cs`, `src/PeerSync.Protocol/Validation/`, `src/PeerSync.Rendezvous/Hosting/RendezvousHost.cs` | Ten-minute fuzz and current 24-hour campaign cover decoder/validation; inspect per-IP and per-peer limits under concurrent requests. |
| Local control and key disclosure | `src/PeerSync.Service/Control/ControlStatusRpc.cs`, `ControlIdentityInterceptor.cs`, `ControlApiHost.cs`, `src/PeerSync.Crypto/` | Caller identity is enforced before RPC dispatch; `GetFolderKeys` is the explicit audited key-return RPC. Review pipe ACLs and verify denied standard users cannot obtain the key through status, events, dump or error detail. |
| Logs, support bundles and dumps | `src/PeerSync.Core/Diagnostics/`, `docs/diagnostics.md`, `src/PeerSync.Service/Control/ControlStatusRpc.cs` | Structured events accept metadata fields and the bundle collector excludes keys, FolderIds, arbitrary logs and crash dumps. One million-file source debug log had no matching fixture path, `folderId` or `key` JSON field. This is a spot check, not proof of absence. Inspect raw logs and OS crash-dump policy on an installed host; the latter has not been validated. |

The P5 fuzz campaign, million-file fresh-peer run, physical two-PC approval,
rendered Archive restore, public update-feed signer and 72-hour soak are
separate open gates. A reviewer should use the exact candidate commit and
record whether those results were available at review time. Do not mark
P5.2 complete from this package alone.
