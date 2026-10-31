# P4 relay rate slices, 2026-10-24

The central relay accepts chunks up to 64 KiB. Its old token bucket raised
`TokenLimit` to 64 KiB even when an operator configured a lower bytes-per-second
rate. That allowed a full chunk immediately and defeated the configured cap's
initial burst boundary.

`RelayRegistry.ThrottleAsync` now acquires the per-device and total budgets in
slices no larger than the configured rate. Both token buckets have a one-second
capacity equal to their respective rates. A low-rate 64 KiB chunk waits for
successive replenishments and remains cancellable. Each acquired slice counts
as session activity for the five-minute idle timeout.

The focused relay-registry tests passed 6/6, including a 1 KiB/s 64 KiB chunk
that remains pending after its initial budget rather than passing immediately,
and two independent device pairs sharing the total server budget. The complete
Rendezvous suite then passed 18/18 with no skips. Its TRX is
under `artifacts/tests/relay-rate-slices/`. This is local quota
evidence; provider-uplink sizing and a physical WAN relay load check remain
open under Gate P4.
