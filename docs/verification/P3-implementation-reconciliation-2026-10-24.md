# P3 implementation reconciliation, 2026-10-24

This reconciles P3 code checkboxes that still carried their pre-merge WIP
labels. PR #47 was merged and its full ten-job main CI passed on attempt 2,
as recorded in [P3 main CI evidence](P3-main-ci-2026-10-22.md). Later local
consolidated verification passed the App 21/21 and Service 58/58 suites in
[P5 verification](P5-consolidated-local-2026-10-23.md). Source inspection below
is a code-completion audit, not an unassisted physical tester or clean-machine
acceptance test.

| Item | Current implementation evidence |
| --- | --- |
| P3.1.1 | `control.proto` declares the remaining archive, key, approval, settings, ignore, pause and authorization RPCs. `ControlStatusRpc` implements them; `ControlIdentityInterceptor` checks the caller before every unary and streaming dispatch. The real-pipe `EveryRpcIdentifiesAndRejectsAnUnauthorizedCallerBeforeReadingRuntime` test covers a broad set of calls. Full installed standard-user authorization remains Gate P3. |
| P3.1.2 | `WatchEvents` emits full snapshots containing folder, progress, peer, overwrite and error data; `WatchMetrics` writes one sample then delays one second. `DashboardController` reconnects and applies the next full snapshot. Control-pipe and App tests cover these paths. |
| P3.2.1 | `PeerSync.App/Program.cs` uses the generic host, a per-user named mutex and activation event, `ThemeMode.System`, `--tray`, and the reconnecting `DashboardController` with a service-unavailable banner. |
| P3.2.2 | The Folders page calls the folder ACL grant with progress and an elevated fallback before Add/Join; Join validates the parsed capability live. Share reveals a read-write key only after confirmation and provides visible-key copy and QR code through `QRCoder`. |
| P3.2.3 | The selected folder has Peers, Activity, Overwritten and Settings tabs. Overwritten provides restore and restore-as-copy; Settings loads and saves ignore rules. |
| P3.2.4 | The Pending page shows the full DeviceId in the confirmation and calls approve, deny or block. Preferences edits device settings through the verified control client. P5.6's physical approval outcome remains open. |
| P3.3.1 | `PeerSync.iss` creates the service and local group, initializes the service, installs the firewall rule, registers the signed-update helper and offers the autostart task. Local and CI installer builds include the Arm64 variant. |
| P3.3.2 | `InitializeSetup` refuses downgrade; `PrepareToInstall` stops the service before file replacement, and post-install `--init` and service start preserve settings. The installed first PC upgraded from 1.0.13 to 1.0.16 with its joined folder and files intact; see [physical two-PC evidence](P3-P4-physical-two-PC-2026-10-24.md). |
| P3.3.4 | Signed development x64/Arm64 setup builds, signed uninstaller and installed silent cycles are in [local installer evidence](P5-local-installer-ci-2026-10-23.md) and [Server 2022 cycles](P3-server2022-uninstall-cycle-2026-10-23.md). Public release signing is still open. |

P3.2.5 remains open. A [third-PC screenshot](P3-third-pc-error-toast-2026-10-24.png)
shows that an error notification from `NotifyIcon.ShowBalloonTip` renders in
Windows. That older build flooded the desktop with transient socket warnings;
the [notification repair](P4-third-pc-notification-flood-2026-10-24.md) has
not yet been checked in the rendered third-PC app. Pending-device and
overwritten-version notifications also lack rendered field evidence.

For P3.2.6, source inspection shows that the unelevated app passes its own
user SID to the elevated helper. The authorized control RPC persists that SID;
the helper restarts the service so the new pipe ACL includes it, and the app's
controller reconnects. Direct SID admission does not require an updated group
token. This explains the intended no-sign-out path, but an installed
unauthorized standard-user check remains open.

P3.3.3 remains open for its interactive uninstall prompt. All three Gate P3
boxes remain open: the unassisted ten-minute app-only flow, the clean Windows
11/Server 2022 installer matrix, and the installed standard-user check.
