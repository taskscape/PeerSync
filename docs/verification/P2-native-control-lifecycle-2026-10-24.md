# P2 native control lifecycle matrix — 2026-10-24

P2.5.7 requires the default local control client to bind to the configured
Windows service identity and its live kernel PID. A read-only `service-state`
mode in the existing `ControlPipe` spike calls the product's
`InstalledControlEndpoint.ReadRunningService` against the temporary
`PeerSyncControlSpike` fixture. `build/Run-ServiceP0.ps1` now checks a uniquely
missing service, the running fixture's configured virtual-account SID and exact
readiness-file PID, the same service after SCM stop, and its new live PID after
SCM restart. It records each result separately and keeps its existing
standard-user unauthorized/authorized caller checks. No installed `PeerSync`
service or host clock is changed.

The focused Release build of `spikes/ControlPipe/ControlPipe.csproj` succeeded
with zero warnings/errors, and `Run-ServiceP0.ps1` exited zero at
2026-10-24T10:32:21Z. Its `artifacts/p0-services/summary.json` reports
`status=passed`, `missingServiceRejected`, `runningIdentityVerified`,
`stoppedServiceRejected`, `restartIdentityVerified`,
`unauthorizedPipeDenied`, `authorizedPipeCallerSidVerified`,
`privateKeyDenied`, and `virtualAccount` all true. Retained per-check logs
report native missing-service code 1060, stopped-service rejection, and
`ownerVerified=true`/`processVerified=true` on both running checks. The
temporary services and test user were removed; the installed `PeerSync`
service remained Running. The 24-hour fuzz process was still live afterward.

After the run, the probe's duplicate expected-PID parse was simplified with no
behavior change. The final Release build passed with zero warnings/errors.
The canonical .NET 10 `dotnet test --project` invocation passed all 39
`PeerSync.Control.Contracts.Tests` cases with no skips. An earlier attempt
using legacy positional `dotnet test <project>` syntax collected zero tests
and exited 5; it did not report a product-test failure.

The existing `ControlPipeIdentityTests` native fixtures reject a wrong pipe
owner or kernel server PID before any RPC bytes, release a cancelled pending
connection, bound a missing-pipe connection deadline, and accept the actual
stream. The installed-service lifecycle and fixed-client control path were
also exercised by the [native SCM smoke](P2-installed-scm-local-2026-10-22.md)
and [fixed-SCM authenticated peer/UI probe](P2-installed-service-peer-native-2026-10-22.md).
The [fixed-SCM TCP/QUIC probe](P2-fixed-scm-tcp-quic-2026-10-24.md) added the
remaining selected transport observation. These results close P2.5.7's
native/control matrix and the deferred P1.5.5 production integration.
They do not close the physical three-PC, 50 GiB corpus, 72-hour soak or WAN/NAT
gates.
