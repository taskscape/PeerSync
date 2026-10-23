# P4 idle diagnostic-log retention, 2026-10-23

The central `DiagnosticJournal` previously removed old `debug*.jsonl` files
only while writing a new event. A healthy but quiet rendezvous server could
therefore keep an IP-bearing raw file beyond the 24-hour limit. The journal now
checks its own files on construction, on fresh-file creation, on a write after
the next expiry, and from a timer scheduled no later than 15 minutes ahead. It deletes a file
when its oldest creation/last-write timestamp is 24 hours old. Only canonical
`debug.jsonl` and `debug.N.jsonl` names are managed; unrelated names and
reparse points are skipped. The timer is disposed with the journal.

The `IdleAddressLogExpiresWithoutAnotherWriteAndUnknownFilesSurvive` test uses
an injected clock. It writes an address-bearing event, verifies the file still
exists at 23 hours, advances to 24 hours and one second and fires the timer
without a write, then verifies the file is gone and an unrelated similarly
named file remains. A later new event creates a fresh file with no old address.

Local Release checks after the change:

| Check | Result |
| --- | --- |
| `dotnet test tests/PeerSync.Core.Tests/PeerSync.Core.Tests.csproj -c Release --no-restore` | 149 passed, zero failed/skipped |
| `dotnet test tests/PeerSync.Rendezvous.Tests/PeerSync.Rendezvous.Tests.csproj -c Release --no-restore` | 17 passed, zero failed/skipped |
| Three current million-file source `debug*.jsonl` files | 143,370 parseable events; no fixture path or forbidden identity/key JSON fields in this spot check |

This proves local timer behavior and bounds raw journal-file lifetime while
the process is running. It does not establish independent review, deployed
Windows Server retention behavior, crash-dump handling, or the required
two-PC forced-relay diagnostic bundles. Those P4/P5 gates remain open.
