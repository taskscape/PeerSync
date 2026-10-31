# Windows service recovery after managed failure, 2026-10-27

During the [physical corpus run](P2-three-pc-9gib-corpus-2026-10-27.md),
the second PC's 1.0.31.0 PeerSync service stopped at 06:21:47 UTC after a
peer `AuthenticationException` escaped its folder scheduler. The SCM showed
`STOPPED`, PID 0 and exit code 0. `sc qfailure PeerSync` showed three
configured restart actions (60, 60 and 300 seconds), but
`sc qfailureflag PeerSync` showed
`FAILURE_ACTIONS_ON_NONCRASH_FAILURES: FALSE`. No automatic restart occurred;
the same service was started manually around 06:29 UTC without resetting the
test deadline. The source-side authentication retry repair is documented
separately in [P2's recovery note](P2-peer-authentication-recovery-2026-10-27.md).

The installer now applies `sc failureflag PeerSync 1` immediately after
configuring the failure actions, including on upgrades. The control host marks
its Windows service lifetime with a nonzero exit code before orderly disposal
when startup or a supervised component fails. It leaves normal operator stop
and control-host restart paths unchanged. Local `sc failureflag /?` confirms
that this flag makes configured recovery actions eligible when a service
enters `SERVICE_STOPPED` with a nonzero Win32 exit code. Microsoft documents
[`ServiceBase.ExitCode`](https://learn.microsoft.com/dotnet/api/system.serviceprocess.servicebase.exitcode)
as the code to set before a failed service stop. The
[`SERVICE_FAILURE_ACTIONS_FLAG` documentation](https://learn.microsoft.com/windows/win32/api/winsvc/ns-winsvc-service_failure_actions_flag)
says changing the flag takes effect at the next system start, so a controlled
post-reboot field/VM check is necessary; merely seeing `qfailureflag` return
true after setup does not prove recovery is active immediately.

`PeerSync.Service` built locally in Release with zero warnings and errors on
the changed source. The failed physical corpus used 1.0.31.0, which had
neither repair. The development-signed 1.0.32.0 package is installed on the
first two physical PCs; a separate controlled post-reboot VM check is below.
The physical corpus interruption remains a recorded availability failure.

## Post-reboot Server 2022 recovery check

On a disposable Hyper-V Windows Server 2022 guest, the signed 1.0.32.0
installer (SHA-256
`97CB272C9160548ED76EAE2E0423489D73E1546725A0F8CCA7641FE8A261CCAC`)
upgraded an installed 1.0.16.0 client with setup exit 0. The service was
Running, `qfailureflag` returned TRUE, and the original settings hash was
`F8A403CAD8C9ECCBCAF13B2AD08CE4038BB56688F5D23E46EF11276D313531D3`.
The guest rebooted at **08:19:19 UTC**, after the upgrade, and 1.0.32.0
started normally. Its recovery actions were three restarts delayed by 60,
60 and 300 seconds.

The controlled check first stopped the service normally, retained the exact
settings bytes, and temporarily pointed its device-certificate setting at an
unavailable thumbprint. The next SCM start attached the control host and then
failed during certificate loading. The 08:23:26 UTC service journal records
`service.control_listening`, `service.device_certificate_load_failed` and
`service.start_failed` in process 2196. SCM reported STOPPED with Win32 exit
code 1. System event **7031** stated that the service terminated unexpectedly
and would be restarted in 60,000 ms. The original settings bytes were restored
before that retry, with the same SHA-256.

Without a manual start, SCM launched process 920 at 08:24:26 UTC. Its service
journal records `service.control_ready`, System event **7036** records Running,
and the script observed it Running at 08:24:27 UTC. `manualRecoveryPerformed`
is false and `serviceRunningAtEnd` is true in the retained result. A later
read-only check confirmed that the service remained Running at 1.0.32.0 and
the settings hash was unchanged. The report, setup log, selected journal
events and SCM events are retained under ignored
`artifacts/field/current-head-20261027/server2022-recovery/`.

An initial test helper attempt stopped before failure injection because it
did not strip the settings file's UTF-8 BOM. It did not alter settings; the
original hash was checked and the service was manually restarted. That
attempt is retained separately as `preflight-bom-failure.json` and does not
count as the recovery result. The corrected attempt above is the sole
failure-injection run. This VM check verifies the post-reboot SCM recovery
mechanism; it does not convert the earlier physical availability failure or
the unfinished three-PC soak into passes.
