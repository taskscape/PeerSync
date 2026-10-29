# Clean Windows 11 client lifecycle, 2026-10-26

The disposable Hyper-V guest `PeerSync-P3-Windows11-Eval-20261026` was installed
from the official Windows 11 Enterprise Evaluation ISO, rather than from the
earlier offline image whose boot chain failed. The ISO SHA-256 is
`BC3F24086EBADC94489066B5AD78089E2CF5C3491E90E790BB81A2B199C10E38`.
The guest reported `Microsoft Windows 11 Enterprise Evaluation`, build
`10.0.26300`; PowerShell Direct connected to its local administrator account.
It had no PeerSync installation before this lifecycle began.

The development-signed 1.0.26.0 installer was copied into the guest and its
SHA-256 verified there as
`BBB1C5C06F7ECB6D99140571C852E5EF9407F522CEB14FFE9514615CB0EF6041`.
Its unattended first installation exited 0 and started the service. The
development-signed 1.0.28.0 installer was separately verified inside the
guest: 166,905,112 bytes, SHA-256
`9B763AB40D19B7602EB6EEDA1E1C012AFC92806CF282CC264F14D09ADB3AB853`,
valid Authenticode signature from development signer
`6AE1CBFC31F1DB189AF03A82D60C535AE8E0A7BD`. The signer's public
certificate was trusted on this disposable guest for test-signature validation;
it is not a publicly trusted release certificate.

The guest then created a fresh registered test folder and an eight-byte native
sentinel with SHA-256
`FCF4E8D7D9F50F5618DFE6796E0DE44ECAAC06B2A60CEE1B1CCF097B6BE9E87D`.
The baseline settings SHA-256 was
`D445769CF7E9F0F3B5887F5ED7C5FD72BA26EC015D54CA4B53ABBCB8BBD8054D`.
The single bounded lifecycle script then passed these stages:

- Upgrade 1.0.26.0 to 1.0.28.0: setup exited 0, service Running, installed
  version correct, CLI status succeeded, settings and sentinel hashes unchanged.
- Silent keep-data uninstall: uninstaller exited 0, service/group/files were
  absent, settings and sentinel hashes unchanged.
- Reinstall 1.0.28.0: setup exited 0, service Running, prior folder still
  registered and shareable, settings and sentinel hashes unchanged.
- Explicit elevated `--remove-data` followed by silent uninstall: both exited
  0; ProgramData, device certificate, service, group, update task and installed
  files were absent. An independent final-state read reconfirmed those absences
  and the unchanged native sentinel.

The structured report, five installer/uninstaller logs, lifecycle script and
independent final-state JSON are retained in
`artifacts/vm/windows11-eval-20261026/lifecycle-1.0.28/` (script in its parent
directory). The run lasted under three active minutes and used less than the
10 GiB test-content cap. The guest remains disposable and is not a release
machine.

This covers the clean Windows 11 silent lifecycle and current-source upgrade.
It does **not** exercise the interactive uninstall remove-data prompt. The
Server 2022 lifecycle likewise used explicit `--remove-data`; the interactive
prompt and unassisted app-only tester check keep Gate P3 open.
