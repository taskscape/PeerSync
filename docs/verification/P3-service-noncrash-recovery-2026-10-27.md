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
enters `SERVICE_STOPPED` with a nonzero Win32 exit code.

`PeerSync.Service` built locally in Release with zero warnings and errors on
the changed source. The current installed 1.0.31.0 field build has neither
change. A fresh development-signed installer and a controlled SCM
managed-failure check are still required before claiming automatic recovery;
the physical corpus interruption remains a recorded availability failure.
