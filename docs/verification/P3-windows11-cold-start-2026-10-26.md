# Windows 11 cold service start after installer upgrade, 2026-10-26

The disposable Windows 11 Enterprise Evaluation guest
`PeerSync-P3-Windows11-Eval-20261026` exposed a real 1.0.28.0 installation
failure. Setup created the service and ran `--init`, but `sc start PeerSync`
failed with SCM events 7000 and 7009: Windows waited 30 seconds for the
service to connect. Its journal started at 16:29:12 UTC and reached
`service.folder_host_ready` at 16:29:40, but the process was stopped before
`service.control_ready`. Starting the same installed service later succeeded
in 3.9 seconds. The cold path loaded folders and peer transports before
starting the control host that registers with SCM.

The installed service now starts its named-pipe host first. The control
interceptor checks the caller and holds authenticated RPCs until folder
recovery attaches the host, so an early status or folder request does not
read incomplete state. A named-pipe regression exercises status, folder list
and event streaming across that readiness boundary. The Release Service suite
passed 74/74 tests.

A development-signed 1.0.30.0 x64 setup was built from the modified working
tree and verified with SHA-256
`49560F2F0A1FC1FC8678085D197C32A31B0FC505694ABF9DB4D4B3FE135ACAA1`.
Its signer was the development certificate
`6AE1CBFC31F1DB189AF03A82D60C535AE8E0A7BD`; both the host and guest
verified the signature. The ordinary build script's locked restore could not
run because another process held a NuGet analyzer file open. The three
components were therefore published from existing restored assets with
`--no-restore` and compiled through the same Inno Setup script and signing
path. This is a local development build, not a release package or a complete
locked-restore verification.

The actual silent upgrade from installed 1.0.28.0 exited 0 in 64.27 seconds
and left the service at 1.0.30.0, Running. Its service journal records:

| UTC | Event |
| --- | --- |
| 16:49:51.922 | `service.starting` |
| 16:50:07.954 | `service.control_listening` |
| 16:50:25.341 | `service.quic_listener_ready` |
| 16:50:25.890 | `service.folder_host_ready` |
| 16:50:25.894 | `service.control_ready` |

Setup's `sc start` returned 0 at 16:50:07, while the folder was still
recovering. An authenticated installed CLI status request subsequently
returned a valid snapshot. After a Windows reboot, the service was Running
without manual intervention and its fresh journal recorded control listening,
folder ready and control ready at 16:51:56 UTC. There were no new SCM
7000/7009 events in the observed reboot. The guest was then powered off and
its guest file-copy integration disabled. Raw installer and event evidence is
retained under `artifacts/vm/windows11-eval-20261026/`.

For the separate P3.2.6 rendering check, the installed app was launched as a
disposable non-admin user in the interactive console. Windows UI Automation
found the visible `ServiceBanner` reading “This Windows account is not
authorized for PeerSync” and the visible `Authorize this account` button.
The screen capture of that WPF window did not show its content, and the
elevated button click was not completed. Thus the rendered elevation/reconnect
gate remains open. Automatic command review blocked cleanup of that guest
test account, its DPAPI credential file and scheduled tasks; the VM is off.

The current branch still requires its complete local verifier, hosted CI
when billing allows, physical soak/corpus gates and public release signing.

The subsequent no-restore Release build of the full solution reported zero
warnings and errors. Its first Service-suite pass exposed two independent
test-environment failures: Windows allocated 32 TCP ephemeral candidates
inside unavailable UDP ranges, and concurrent native MsQuic certificate
loading returned `0x8009030d` in the clock-skew handshake fixture. The latter
test passed alone. The bind fixture now selects a random port from the
product's configured listener range and verifies both protocols; the installed
host native-QUIC collection runs without other Service test collections.
The complete repaired Service suite passed 74/74 in 91.9 seconds, with zero
skips. Across the ten Release suites, the current source has 1,814 passing
tests, zero failures and zero skips in the retained TRX files. The failed and
passing logs are retained in `artifacts/verification-p3-*`. The simulation,
native probes, probe publication and boundary fuzz were not repeated for this
startup-only change; the previous clean-head canonical pass is recorded
separately in [P5 current-head local verification](P5-current-head-local-2026-10-26.md).
