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
share key.

P5.6 remains open until the repaired service is packaged and installed on
both physical PCs, Pending/approve/block are observed on a freshly added
folder, and native bytes are checked before and after the decisions. The
existing shared folder was not changed. The one-time remote join script has
an ACL restricted to SYSTEM and Administrators; automatic approval review
rejected deleting it and the local DPAPI-encrypted test key (`blocked by
policy`). Both should be removed through normal host administration after
the field run, along with the inactive one-time scheduled tasks.
