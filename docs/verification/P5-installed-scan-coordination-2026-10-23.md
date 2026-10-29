# Installed scan coordination, 2026-10-23

The installed service previously created one scanner for its watcher/periodic
scheduler and another inside the transport pull coordinator. Watcher batches
from remotely installed files could each start a separate full native walk,
bypassing the coordinator's 10,000-file/five-minute publication-scan batch.
That independent walk also bypassed coordinator needed-work recomputation.

The installed host now registers the transport's scanner with status and the
scheduler. Scheduled work runs `TransportFolder.RunPassAsync`, which serializes
with supervised peer and resume passes, recovers pending native intents,
scans and repairs needed work, and publishes local index changes. The
coordinator acquires the process-wide scan permit inside its scan; the
scheduler does not take a second permit around that pass. A direct-scanner
scheduler remains available for its existing standalone callers.

Watcher batches that contain only paths written by the current remote-file
batch, including changed parent paths, request a coordinator pass without
forcing an extra scan. An unrelated path or watcher overflow requests an
immediate scan. The coordinator still scans when live work drains, before a
directory/deletion transition, after 10,000 installed files, or after five
minutes at a pass boundary. Native guarded apply detects a same-path user
edit during installation; a later same-path edit can wait until that bounded
publication scan. This is a latency tradeoff of batching, not a substitute
for native conflict preservation.

Resume and network-change rescans use the same coordinator. The resume log
reports a completed scan only when the pass actually completed one, rather
than when it merely attempted a backpressured scan. Scheduler diagnostics
record sanitized pass start/completion counts; watcher coalescing logs only
the number of paths, never their names, hashes or contents.

The focused native coordinated-watcher regression passes locally: it starts
the scheduler without a peer, confirms its initial coordinator scan, writes a
new file and confirms a later watcher-driven pass indexes the file without a
nested scan-permit deadlock. The extended remote-file batch regression covers
two coalesced 1,000-file passes and a foreign watcher path that forces a scan.
The installed-host startup test asserts that status and transport share the
scanner and that the scheduler logs a coordinator pass. On this working tree,
Release Engine passes 1,045/1,045 and Release Service passes 57/57, with zero
failures or skips. These suites exercise the coordinator and installed-host
paths locally; they do not represent a new installed package or physical peer.

This is local implementation evidence. The older live million-file benchmark
uses an earlier binary and cannot validate this change. A fresh committed-
source million-file peer transfer, deployed Windows service and physical
network checks remain open.
