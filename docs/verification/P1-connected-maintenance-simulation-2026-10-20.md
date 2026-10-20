# Connected production maintenance simulation

The existing generated sync cases do not advance far enough to demonstrate
archive expiry/block eviction. This new acceptance scenario passes against the
existing production implementation; it does not repair or claim a reproduced
production data-loss bug.

Three public TransportFolder/IPeerTransport peers exchange the original version.
Two peers then apply the replacement; the third receives its signed index but
keeps the old current file and a pending update behind a partition. Actual public
BlockRetention sweeps bootstrap history, prune archives, refresh references and
evict eligible owned blocks. Only injected peer clocks advance (32 + 32 days).
The scenario observes two native archive files disappearing and two old block
files actually removed. Current bytes, the delayed needed row and unindexed
unknown cache bytes remain intact. After healing, the delayed transfer succeeds,
its fresh native archive retains the older version and every published payload
still survives through the unchanged global native loss oracle. Native trees,
needed/apply/case/publication/deletion queues settle.

The focused case passes three times, including explicit native-path assertions.
Evidence: `artifacts/p1-connected-maintenance-final-focused.log` and
`artifacts/simulation/connected-maintenance-final-focused/connected-maintenance.json`.
The report includes stage/trace, binary hash, actual days advanced and replay;
failed fixtures and zipped data would remain available. The fixture's test-only
key derives from `SHA256(UTF8("PeerSync connected retention simulation test keys v1"))`;
production key creation is unchanged. Roslyn Simulation diagnostics report zero warnings/errors and no
workspace-load warnings.

Canonical source `627d5420f0772c836d002d17808fb585c06d3490` passes all 1545
unit and eleven Simulation cases, native/probe/publishing and 600-second fuzz
(182768 seeds, zero crashes), exit 0 in 611.3623527 seconds. Every campaign
baseline includes this case, and validation requires actual archive/block counts
plus matching binary/source evidence. Full local shard and coordinated five-shard
preflight also pass. CI/main acceptance remains pending; no retention parent,
P1 gate or external network requirement is ticked from this local evidence.
