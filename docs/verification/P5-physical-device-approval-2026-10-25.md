# Physical device-approval onboarding, 2026-10-25

The two signed 1.0.25.0 Windows clients remained connected on an existing
physical-LAN share. A separate new test folder was added on the first PC and
set to Require approval. The first PC indexed a 4,096-byte random seed with
SHA-256 `BCAB1D0858175650E9D43D252701B0326F4AD8B0F7CD839D50DD3CE9A9AB4DEB`.
The second PC then joined that folder through the installed service. Both
service listeners and the verified local control pipes remained available.
The new folder's fingerprint was `BD0AED114E533C45`.

The seed correctly did not arrive before approval, but the first PC showed
**zero Pending devices**. After both sides configured each other's actual LAN
listener as a predefined host, the first PC journal recorded host dial
attempts, yet the new folder still did not enter a folder-auth handshake.
Both PCs already had authenticated sessions for the older share. Source review
identified the cause: `InstalledFolderHost.AddFolderAsync` attached the new
folder without refreshing tracker announcements or resetting existing peer
sessions to renegotiate the folder list; a host dial reused an existing peer
connection and returned. The physical attempt is a failed Gate P5.6 check,
not evidence that Pending or block works in the installed build.

The repair notifies the network runtime whenever an additional folder is
added or a paused folder resumes. It refreshes tracker announcements and
requests a coalesced reconnect so the next authenticated handshake includes
the new membership. The add call does not wait for every established session
to drain. A new production-host regression first authenticates a baseline
folder, then adds an approval-controlled second folder. It requires Pending
with no destination file before approval, then exact native bytes after
approval. It also pauses that folder while the baseline peer remains
connected, then resumes it and verifies a second native file transfers.
The repaired source builds with zero warnings/errors and the focused case
passes in about eighteen seconds. The field-only control tool was
published self-contained and its read-only `inspect` command succeeded
against both installed services. Evidence is retained under the ignored
`artifacts/field/approval-physical-20261025/` directory without a plaintext
share key. After the field run, the complete Release win-x64 Service suite
passed 73/73 with zero skips in 70.324 seconds.

## Repaired signed deployment and approval result

Development-signed client setup 1.0.26.0 was built from committed revision
`0153358b5b41f8bb58bcd8334d65b616a839f51f`; its SHA-256 is
`BBB1C5C06F7ECB6D99140571C852E5EF9407F522CEB14FFE9514615CB0EF6041`.
The manifest hash and source revision match, and Authenticode reports Valid
with a timestamp and the established development signer. The manifest's
dirty flag reflects a separate untracked workspace file; tracked source was
clean at build. This is a development package, not a public release signer.

Both physical PCs upgraded from 1.0.25.0 to 1.0.26.0 with setup exit 0 and
no reboot. The second PC's copied setup matched the source hash; both logs
record `STOPPED_AND_EXITED` before replacement. Both services are Running
with Valid executable signatures, unchanged `settings.json` hashes, and the
same native sentinel SHA-256
`E881290DAB75A564F347C731338C3E2F1CC77C09C4B2E674EE041D2F7FFDD4E2`.

After the upgrade, the first PC's verified pipe showed the second device in
Pending for the isolated Require approval folder. The seed remained absent
on the second PC. The Pending device ID matched the second physical peer's
existing authenticated identity. Approving it through the verified pipe
delivered exactly 4,096 native bytes to the second PC; both SHA-256 values
were `BCAB1D0858175650E9D43D252701B0326F4AD8B0F7CD839D50DD3CE9A9AB4DEB`.

Blocking that device changed the first PC's record to Blocked and drained
the test folder's connected sessions. A new 4,096-byte file on the second
PC was indexed locally with SHA-256
`1E92A5773D3AB291BB75086EBB89F66DD8A3F02E5659464C7193F100570C40F2`.
The second service was stopped with the installed process-exit guard and
restarted; it returned Running. Both journals contain the same new QUIC
session `51d07862-a04f-c2d2-2aee-1ea943d38e02`: the first records
`local_policy_denied` and the second `remote_policy_denied` for the test
folder, while allowed folder proofs and a ready session keep the older share
connected. The blocked file remained absent on the first PC at the final
08:24:29 UTC check. The exact remote setup, service-restart and sanitized
decision evidence is retained under the ignored field-artifact directory.

This meets the P5.6 physical Pending/approve/block and native-byte criteria.
The **new-add renegotiation repair** itself passed the production-host
regression but was not separately exercised by adding another fresh folder
after the 1.0.26.0 installs; that installed Add/Join follow-up remains open
under P3.2.2. The existing shared folder was not changed. The one-time
remote join script has an ACL restricted to SYSTEM and Administrators;
automatic approval review rejected deleting it and the local DPAPI-encrypted
test key (`blocked by policy`). Both should be removed through normal host
administration after the field run, along with the inactive one-time tasks.
