# Connected production maintenance acceptance

P1.2.4's open parents explicitly require production cleanup in simulation.
Generated cases currently advance by seconds/hours and invoke public sync;
they do not demonstrate archive expiry and owned-block eviction. Keep the
existing loss oracles unchanged and add a separate deterministic three-peer
scenario to P1.6.2.

Use real TransportFolder/IPeerTransport transfers and published native files.
Transfer version one to both receivers, then publish version two and apply it
to only one receiver. The other admits its signed index but keeps version one
current and version two needed. Invoke public BlockRetention.SweepAsync (which
owns archive pruning, history policy and reference refresh) on all three peers.
Advance only injected peer clocks: 32 days expire the two old archives and
observe unreferenced blocks; another 32 days satisfy default block age/grace.
Require actual old-block deletion on updated peers, unchanged current bytes and
pending needed work on the delayed peer, and preservation of unindexed unknown
cache bytes. Restore delivery and require the delayed transfer and fresh archive
of its former current version to succeed. All published payload versions must
still survive in native current/archived bytes across the peers.

No direct SQL/index injection, artificial capacity override, weakened loss
oracle, host clock change or production signing change is needed. Retain the
generated fixture, trace and deterministic replay instructions on failure.
Record archive/block/native/needed counts and source provenance on success.
This is local/CI acceptance evidence, not physical-network validation.

Add the new method to the campaign's exact baseline registry and control tests;
normal/default and preflight/full shards keep it. Recheck the source-bound
coordinator with actual TRX. Complete focused, canonical, CI/main evidence
before ticking the new task or any retention parent.
