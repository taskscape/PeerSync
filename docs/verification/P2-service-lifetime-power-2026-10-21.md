# P2 service lifetime and power notifications

The control host now registers a custom `WindowsServiceLifetime` only when
running under the Windows Service Control Manager. Its constructor enables
power notifications before startup. Suspend, automatic resume and resume
notifications are routed through `PowerEvents` to the host-owned runtime.
Callback failures are isolated and recorded as safe structured diagnostics.
The control host's stop path retains its five-second deadline, within P2.5.1's
15-second limit.

## Local verification

On 2026-10-21, the Service project Release build completed with zero warnings
and errors. All 22 `PeerSync.Service.Tests` passed. A real local named-pipe
host test raised suspend and resume through the host's registered event hub,
observed both runtime callbacks, and disposed the host within 15 seconds.

## Remaining service validation

This verifies the console/fixture host and the Windows lifetime's compile-time
registration path. It does not prove SCM startup or actual power broadcasts
from an installed service. The engine's production connection manager is not
yet hosted, so no live peer reconnect behavior is claimed. Those checks depend
on service initialization and the remaining P2.5 integration work.
