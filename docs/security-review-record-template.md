# PeerSync independent security review record

Copy this file for the reviewer and fill it with the actual candidate and
evidence. The empty template is not a review or P5.2 acceptance. Link the
completed record from `plan.md` only after findings are resolved or given an
explicit rationale and the installed-host boundary has been checked.

| Review identity | Value |
| --- | --- |
| Reviewer and independent role | Pending |
| Review start/end UTC | Pending |
| Exact client/server source revisions, clean-tree provenance and signed package hashes | Pending |
| Installed client/server versions and Windows builds | Pending |
| Hosts and network topology actually examined | Pending |
| Evidence archive location and access owner | Pending |

For every row in the [design threat table](../Resilio-style%20P2P%20Sync%20for%20Windows%20Implementation%20Plan.md#threats-and-mitigations),
record a finding ID or a pass with the specific source revision, test setup,
observed result and evidence path. The [review package](security-review-package.md)
maps threats to implementation and prior local results; those results are
leads for inspection, not independent findings.

| Threat | Review result and evidence |
| --- | --- |
| Network eavesdropping | Pending |
| Tracker or relay operator | Pending |
| LAN observer | Pending |
| Man in the middle | Pending |
| Malicious read-only peer | Pending |
| Malicious encrypted peer | Pending |
| Leaked folder key | Pending |
| Compromised RW device | Pending |
| Hostile paths in records | Pending |
| Oversized or flooding input | Pending |
| Local control pipe abuse | Pending |
| Automatic crash reporting and privileged dumps | Pending |

The installed crash boundary needs its own host-by-host record. For every
client and Rendezvous host examined, retain the `Audit-CrashPolicy.ps1` JSON,
its hash and capture time; verify the installed WER exclusion and global plus
per-executable `LocalDumps` state. Record whether any policy or administrator
can create a full/live dump and how such a privileged secret is protected.
Inspect raw service, client, central and support-bundle output after actual
key-bearing operations, including the explicit audited `GetFolderKeys` call.
Do not include keys, folder capabilities or raw dumps in this shareable record.

| Host and component | WER/LocalDumps audit evidence | Raw-log/bundle inspection | Result |
| --- | --- | --- | --- |
| First physical client | Pending | Pending | Pending |
| Second physical client | Pending | Pending | Pending |
| Third physical client | Pending | Pending | Pending |
| Physical Rendezvous server | Pending | Pending | Pending |

Test signed-update trust with the actual release signer and installed helper:
current trusted signer, unrelated trusted signer, revoked non-root signer,
unreachable revocation service, changed publisher pin and safe rotation. Note
any test that cannot be performed and keep its release condition open.

For each reviewed installer, compare its manifest, source commit and payload
hashes with the installed files. If the manifest records a dirty working tree,
identify and retain that exact source delta before claiming the package is
reproducible. Review the client and Rendezvous builds as separate revisions;
the development-signed field builds in the [handoff snapshot](security-review-package.md#current-handoff-snapshot-2026-10-28)
are not release candidates.

| Finding ID | Severity | Reproduction and evidence | Fix commit or accepted rationale | Retest result |
| --- | --- | --- | --- | --- |
| Pending | Pending | Pending | Pending | Pending |

Final disposition: **Pending**. State the reviewed revision, any excluded
threat or host, unresolved findings, and whether the reviewer recommends
release. The release gate remains open until the actual reviewer signs this
record and the plan's separate physical, CI and signing checks pass.
