# Server 2022 interactive uninstall and running-tray upgrade

The disposable `PeerSync-P3-Server2022-Clean` VM is Windows Server 2022
Datacenter Evaluation, build 20348.5622. Its earlier headless service checks
had not completed Windows first-run setup. Hyper-V's actual console showed the
region/language page; after completing it, the guest reported
`IMAGE_STATE_COMPLETE` and an active Administrator console session 1. The
interactive checks below ran in that console session, rather than in the
PowerShell Direct service session.

The first current-build upgrade exposed an installer defect. The signed
1.0.28.0 setup (SHA-256
`9B763AB40D19B7602EB6EEDA1E1C012AFC92806CF282CC264F14D09ADB3AB853`)
exited 5 while the installed `PeerSync.exe --tray` remained open in console
session 1. Its log says `DeleteFile failed; code 5. Access is denied` for
`C:\Program Files\PeerSync\app\PeerSync.exe`; the service was stopped after
the partial replacement. The settings and 256-byte user-file hashes were
unchanged. `CloseApplications=force` alone did not close this instance.

The installer now runs `Stop-ClientAppAndWait.ps1` before replacing files and
before uninstall. It targets only processes whose executable path equals the
selected installed `PeerSync.exe`, requests a window close, then uses a
bounded force-stop if the tray process remains. An uninspectable same-name
process or a timeout aborts before file replacement. The signed development
1.0.29.0 setup (SHA-256
`D73DD40A9EA51A4A7AF34D952C9708E2861982E355A9EBBFA26B2F2496306C34`,
signer `6AE1CBFC31F1DB189AF03A82D60C535AE8E0A7BD`) was copied to the guest
and independently matched by hash and valid Authenticode signature. With the
same tray PID 7128 still open, its log recorded close request, bounded
force-stop, and process exit. Setup exited 0, installed 1.0.29.0, restarted
the service and required no reboot. The settings SHA-256 remained
`F8A403CAD8C9ECCBCAF13B2AD08CE4038BB56688F5D23E46EF11276D313531D3`;
the user file remained
`771395853747824E6E359F3FBAC2AA7ADDF33A19523F4DFF787F7CAA9C7B739F`.
This is a development-signed validation build from a working tree; public
release signing remains open.

The actual console uninstaller displayed the data-removal question with **No**
visibly focused by default. Two separate installed 1.0.29.0 flows then ran:

| Choice | Independent result after the completion dialog |
| --- | --- |
| Keep data, by pressing Enter on the default **No** | Uninstaller, service, local group and installed files absent. `C:\ProgramData\PeerSync` remained; settings and user-file hashes matched their pre-uninstall values. No `--remove-data` command in the Inno log. |
| Remove data, by selecting **Yes** | Log ran `PeerSync.Service.exe --remove-data`. Uninstaller, service, local group, ProgramData, device certificate, update task, installer firewall rule and installed files absent. The user-file hash still matched. |

Both Inno logs report successful uninstall and `Need to restart Windows? No`.
The keep and remove logs have SHA-256
`46164C997F23AFC0D8E0D79B4327D02AF1221A7892F274E1EA576E78969B443D`
and `48EA252932E008ED9E7682CFFEFE9072042B155F1ADACFFF1B2D3D09F322A2CE`.
The ignored local evidence directory
`artifacts/vm/server2022/interactive-uninstall/` retains rendered console
frames, the initial failed and repaired setup logs, both uninstall logs, and
independent guest final-state JSON. After copying that evidence to the host,
the exact disposable VM was powered off and restored to its precheck
checkpoint, reversing the temporary RDP and setup changes.

This closes the Server 2022 interactive part of P3.3.3 and, together with the
[clean Windows 11 interactive check](P3-clean-windows11-interactive-uninstall-2026-10-26.md),
the clean-VM upgrade/keep/remove item of Gate P3. The separate unassisted
app-only tester and rendered standard-user authorization items remain open.
