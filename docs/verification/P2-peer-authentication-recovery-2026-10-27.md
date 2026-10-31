# Transient peer authentication failure recovery, 2026-10-27

The live three-PC corpus run exposed an availability defect in installed
client 1.0.31.0. A TCP data attach to another authenticated folder peer
raised `AuthenticationException`. The transport block path did not classify
that exception as a peer availability failure. It escaped the file pull,
stopped the installed rescan scheduler and terminated the PeerSync service.
The exact event chain and manual restart are retained in the
[physical corpus record](P2-three-pc-9gib-corpus-2026-10-27.md).

Current source now handles a peer-session authentication exception at the
block fetch boundary like a transient unavailable peer: it records a failed
request, tries another authenticated peer where available, and otherwise
leaves the needed file pending for a bounded retry. The puller and installed
rescan scheduler also recognize this error as recoverable so it cannot stop
the whole service if it escapes a lower transport boundary. A failed TLS
handshake grants no access or index admission; this change only affects
retry behavior after a session fails.

Local focused verification passed on the repaired source: all 32
`TransportFolderTests` cases, including a new real folder transfer that
fails the first block request with `AuthenticationException` and succeeds
after retry, and all 25 `RescanSchedulerTests` cases, including a new
backoff/continued-scheduler case. Both were run through the xUnit v3
in-process runner with zero skips. The live physical run remains on 1.0.31.0;
it cannot validate the new binary until a later signed upgrade and fresh
physical check. The full local suite, hosted CI, and physical gate result
remain separate.
