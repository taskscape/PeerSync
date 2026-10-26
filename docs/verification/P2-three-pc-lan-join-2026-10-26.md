# Third physical PC: installed-service LAN join and byte checks, 2026-10-26

`LARKBOX-X-1` is a fresh physical Windows 11 PC with PeerSync 1.0.28.0 running
as `NT SERVICE\PeerSync`. The first and second physical PCs also run 1.0.28.0.
The third PC has Ethernet address `192.168.0.164`; the first and second use
`192.168.0.176` and `192.168.0.183`. The first-to-third selected route is
Ethernet with source `192.168.0.176`; the third-to-first selected route is
Ethernet with source `192.168.0.164`. Tailscale remained connected for remote
administration and evidence transfer, so selected routes alone are not proof
of PeerSync's transport.

The operator ran the temporary WinRM bootstrap v3 on the third PC. Its
Taildrop report says `READY remoting=WinRM over Tailscale only`, restricted
to the first PC's Tailscale address and scheduled to clean up after two hours.
The encrypted credential was received on the first PC and used in memory for
an authenticated administrator remoting session. No credential or folder key
is retained in this document.

The first join attempt from the operator and the first direct remote retry
failed with `NotFound`. The installed service journal records
`control.folder_request_failed` with `DirectoryNotFoundException` even though
the empty target directory existed for the signed-in user. The service runs
as `NT SERVICE\PeerSync`, and the target's initial ACL gave access only to
SYSTEM, Administrators and that user. Granting **Modify only on the test
folder and its children** to `NT SERVICE\PeerSync` resolved the problem:

```powershell
icacls 'C:\Users\mzag\Documents\PeerSync-LAN-Test' /grant 'NT SERVICE\PeerSync:(OI)(CI)M'
```

The direct `psp join` retry exited 0 with the expected folder ID
`93478561F31FD97DB62EE633A31E5EC09BA88E812B1E4B72288FFAC3031E3BDC`,
`ReadWrite` access and `paused: false`. The third-PC service status then showed
`productionSyncAvailable: true` and connected accepted-clock sessions with
the first and second PCs. Its native index contained the first-PC test file.

| Check | Observed result |
| --- | --- |
| First PC to third PC | `larkbox-first-to-third-20261026.bin`, 4,096 bytes; both native SHA-256 values `BDD716A8D75E87B8C45A9BB9CF4FD3F6C37934283DB09A8B2AA8439AC4840C79`. |
| Third PC to first PC | `larkbox-third-to-first-20261026.bin`, 4,096 bytes; both native SHA-256 values `1C420C13384782A6B2AB762B48DC4CE4CC9E443DA9740B1AD03FA5BB74035E1F`. |
| Third PC to second PC | The second PC's joined folder had the same reverse file and SHA-256, read through its administrator share at `100.88.55.83`. This verifies convergence there, but does not identify which peer served its blocks. |
| Physical transport | The first PC's installed journal records `transport.request_start`, operation `quic_index_page`, transport `Quic`, remote address `192.168.0.164`, session `76f09bb4-35a2-22b7-fe7a-5f9dd69b5c33` at `2026-10-26T14:52:09.7619877Z`. A correlated `service.folder_peer_ready` for that session appears at `14:52:09.755006Z`. |

This closes the basic third-PC setup and small-file LAN byte check. It does
**not** close Gate P2: the 9 GiB / 100,000-file corpus, three-hour soak,
sleep/recovery and complete per-adapter multicast evidence remain open. The
manual ACL repair also means this was not the unassisted app-only Gate P3
Add/Join flow. The initial misleading `NotFound` should be fixed in the
product and checked with a fresh user-profile folder.

After the byte checks, the temporary third-PC remoting cleanup scheduled task
was started remotely. Its restricted WinRM rule was present immediately before
the task, and TCP 5985 from the first PC stopped accepting connections after
cleanup. The first PC's temporary TrustedHosts entry and WinRM service state
were restored. The cleanup script's on-host result was not read back, so the
TCP observation is the verified remote closure. Automatic approval review
rejected deletion of the encrypted credential copy in the first PC's
Downloads; that file still needs local removal.

Before the corpus run, the first PC had only 18.6 GB free, below the field
generator's 24,696,061,952-byte minimum. A historical untracked 10 GiB
fixture at `artifacts/hash-10gb.bin` was identified as a way to free space,
but automatic command review rejected deletion. The corpus was not started;
disk capacity and whole-tree content must be rechecked immediately before it.
After the disposable Windows 11 VM's cold-start check and offline VHD
compaction, the first PC had 12,271,849,472 bytes free. Deleting only the
historical 10 GiB fixture would still leave about 1.7 GB below the
24,696,061,952-byte generator preflight; additional free space is needed.
The fixture and encrypted credential were not deleted by Codex after the
automatic command-review rejection.
