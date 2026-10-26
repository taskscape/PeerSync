# Clean Windows 11 standard-account control check, 2026-10-26

The disposable Windows 11 Enterprise Evaluation guest
`PeerSync-P3-Windows11-Eval-20261026` ran the installed, development-signed
1.0.28.0 client after its [clean lifecycle check](P3-clean-windows11-lifecycle-2026-10-26.md).
The host connected through PowerShell Direct as a newly created standard
Windows account. `whoami /groups` showed `INTERACTIVE` and did not show an
enabled Administrators group. The same guest PowerShell process and SID were
kept across the before and after checks.

Before authorization, installed `psp status --json` exited 1. A separate
read-only exception probe on another unauthorized interactive standard account
traced the CLI's generic `RpcException` `Internal` status to
`HttpRequestException` and `UnauthorizedAccessException`, both with
`0x80070005`: the control pipe ACL denied the account. This is an authorization
denial, not proof that the service was down. The dashboard's
`IsAuthorizationFailure` walks the exception chain and recognizes that HRESULT
in source; its rendered banner/button were not checked here.

The installed elevated `PeerSync.exe --authorize-user <standard-user SID>`
helper exited 0 and restarted the service. Without ending or replacing the
standard user's PowerShell Direct session, `psp status --json` then exited 0
and returned a valid service status snapshot. Both checks report the same
PowerShell process ID and SID in the structured report at
`artifacts/vm/windows11-eval-20261026/interactive-user-report.json`.

An initial scheduled-task probe used a **batch** token rather than an
interactive token. This guest's default service DACL lets interactive users
query its status/configuration, while the batch token was denied at
`OpenService` before it reached the pipe. That fixture result is retained in
`artifacts/vm/windows11-eval-20261026/standard-user/` and was not treated as a
PeerSync defect. The temporary batch-logon grant was restored to its exact
original `SeBatchLogonRight` value, and the task was removed.

After collecting evidence, explicit device-data removal and silent uninstall
both exited 0. The service, installed files, ProgramData, device certificate,
probe task and all three temporary accounts were absent; the original batch
policy was restored. The same evidence directory contains the independent
final-state JSON and uninstall log.

This verifies the installed control boundary and same-logon authorization
behavior on Windows 11. A rendered click on **Authorize this account** and the
unassisted two-PC app-only flow still require their Gate P3 field checks.
