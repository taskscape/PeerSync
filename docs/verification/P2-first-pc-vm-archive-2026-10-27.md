# First-PC space for the physical LAN corpus, 2026-10-27

The first PC's C: volume fell below the `New-PeerSyncGateCorpus.ps1` preflight
minimum of 24,696,061,952 bytes: 9 GiB native test content, an independent
9 GiB PeerSync block reserve, and 5 GiB of safety headroom. This is a disk
capacity requirement; the synchronized test-content ceiling remains 10 GiB.
No corpus was started while the volume was short of space.

With the operator's explicit approval, the powered-off
`PeerSync-P3-Windows11-Eval-20261026` evaluation VM's dynamic `system.vhdx`
was copied to LARKBOX-X-1 outside any synchronized folder. Before transfer,
Hyper-V reported `Off`, `Attached=False`, and a file length of 30,706,499,584
bytes. The source SHA-256 was freshly checked as
`528165AC8A2796D83E5A2C179FF7832929A1968864AC4904B7CD980FF0DD2F8D`.
The third PC reported 169,170,927,616 free bytes at transfer preflight.

The copy used a temporary SMB share with account-only share access and a
restricted archive-folder ACL. It was written as `system.vhdx.partial`;
LARKBOX-X-1 then computed the full SHA-256 locally, matched the source digest,
and renamed the file to
`C:\PeerSync-VM-Archive\windows11-eval-20261026\system.vhdx`. A separate
read confirmed that final path exists at 30,706,499,584 bytes, the partial
file is absent, and the temporary SMB share is gone. The allowlisted machine
report is retained at
`artifacts/field/third-pc-remoting-20261026/windows11-vm-archive-20261027.json`
(ignored by Git). The VM cannot boot locally after its system disk is removed
until that verified backup is restored.

The local source was **not** removed by Codex. Automatic command approval
review rejected the exact non-recursive `Remove-Item` attempt with reason
`blocked by policy`, despite the operator's archive approval. The operator
then removed only the local `system.vhdx` in File Explorer. A subsequent
`Test-Path -LiteralPath` returned `False`, C: reported 36,613,578,752 free
bytes (above the generator's 24,696,061,952-byte preflight requirement), and
the VM remained `Off`. A fresh three-PC content and disk preflight passed, and
the source corpus was generated under the [physical LAN run](P2-three-pc-9gib-corpus-2026-10-27.md).
This archive proves storage backup only; it does not close the three-PC
convergence or soak gates.
