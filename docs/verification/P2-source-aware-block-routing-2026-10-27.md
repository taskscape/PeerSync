# Source-aware block routing for a fresh third peer, 2026-10-27

During the installed 1.0.31.0 three-PC 9 GiB corpus run, the third PC's
05:53–05:59 UTC service-journal snapshot contained 4,406
`transfer.block_not_found` events and 4,406 corresponding
`transport.block_request_unavailable` events. Its native file count later
resumed increasing. This is evidence of wasted requests during that snapshot,
not yet proof that the physical run will miss its deadline. The live services
were left untouched while the current-source repair was built and checked.

The earlier `TransportFolder` deliberately moved a non-author peer ahead of
the signed record's author on each request, then fell back after `NotFound`.
It did this even when that other peer was still downloading the same fresh
corpus. `ChunkAssembler` also marked the sole batch of every one-chunk file as
endgame, so it raced both peers for each small file. The 100,000 unique small
files in the field corpus magnify both behaviors.

Current source now starts with the record author, makes a bounded first probe
of another authenticated peer, and uses that replica regularly only after it
has served the author's blocks. An availability miss reduces probing to one
selected request in 64; a later success restores peer spreading. This is a
routing hint only: all authenticated peers remain fallback candidates if the
author fails, each result retains its BLAKE3 verification, and coverage state
is discarded with the connection and capped at 1,024 authors per connected
peer. One-chunk files use the ordinary fetch path. Multi-chunk files retain
the bounded two-peer final-batch race and loser cancellation.

The new regressions transfer 32 unique small files with an unseeded peer,
require fewer than four futile requests to it, and verify all native bytes.
A 128-file case proves a replica that actually holds the author's blocks is
used again after an availability probe while the author still contributes.
The existing corrupt-peer fallback and multi-chunk endgame cancellation cases
also pass. The correct xUnit v3 in-process commands completed with exit 0:

```powershell
dotnet run --project tests/PeerSync.Engine.Tests -c Release --no-restore -- -class PeerSync.Engine.Tests.TransportFolderTests -reporter quiet
dotnet run --project tests/PeerSync.Engine.Tests -c Release --no-restore -- -reporter quiet
```

The complete Engine run preceded a final assertion-only strengthening in the
128-file test; the focused class was rerun after it and passed. `git diff
--check` also passed. No new signed package has been deployed, and this local
change does not reclassify the installed 1.0.31.0 corpus run or close the
physical throughput and three-hour gates.
