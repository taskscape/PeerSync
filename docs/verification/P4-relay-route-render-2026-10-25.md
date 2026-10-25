# Relay route dashboard rendering, 2026-10-25

`RelayRouteRenderTests.PeerRouteRendersRelayAndThenDirectQuic` supplies a
full control snapshot representing an approved relay peer to the real WPF
dashboard and checks the visible peer row. The row shows `via relay` for
`Relay`. When the same model receives a `Quic` snapshot, the already-open
window's row changes to
`direct QUIC`. Fresh render captures of both states are retained locally in
`artifacts/relay-route-ui/relay-route.png` and `direct-route.png`; reviewable
copies are [relay](relay-route-render-2026-10-25.png) and
[direct QUIC](direct-route-render-2026-10-25.png). The test checks that each
bitmap contains visible page pixels. Both captures were visually inspected.

Focused Release build: zero warnings/errors. Focused xUnit test: 1 passed,
0 failed, 0 skipped. Local TRX:
`artifacts/tests/relay-route-render.trx`.

This checks dashboard binding and rendered text under a controlled snapshot.
An installed application observed during a physical forced-relay session has
not yet been captured, so P4.2.4 and the remaining WAN/NAT gates stay open.
