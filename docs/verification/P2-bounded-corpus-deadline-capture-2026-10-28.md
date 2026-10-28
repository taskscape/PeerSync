# Bounded physical corpus deadline capture

The physical 50,000-small-file, 24-level, eight-large-file corpus gate remains
open. A previous three-PC transfer reached the expected native file and byte
totals, but its mapped-drive monitor did not produce a common full-tree hash
before the original three-hour deadline. A hash collected after that deadline
cannot close the gate.

`build/Measure-PeerSyncCorpusRun.ps1` measures the exact ownership-marked
`PeerSync-GateCorpus/<run ID>` subtree using long Windows paths. It rejects
reparse points and emits a report with the native file count, directory count,
byte total, owner-marker hash, generator hash, collector hash, and UTC interval.
This distinguishes exact run convergence from another file in the joined root
having the same name or size. The full synchronized-root snapshot remains the
job of the existing `build/Measure-PeerSyncTree.ps1`.

`build/Compare-PeerSyncCorpusDeadline.ps1` accepts a completed generator
report, one run-subtree report and one full-tree snapshot from each PC, and
the recorded source-service resume time. It requires the exact 9 GiB generated
corpus, 50,033 native run files including `owner.json`, identical run and tree
counts and hashes on all three machines, and completed captures no later than
three hours after source resume. Capture starts may differ by at most five
minutes. The comparison writes a terminal JSON report and exits nonzero on
any mismatch or late capture. The full root must remain below 10 GiB of test
content during the run.

`build/Invoke-PeerSyncCorpusDeadlineCapture.ps1` is the per-PC launcher. It
requires the source resume timestamp, run ID, joined root and an existing
evidence directory outside that root. It measures the exact run subtree, then
starts a full native tree capture in the current UTC hour. It starts each
collector as an owned child process, records separate stdout and stderr, stops
that child if it reaches the three-hour deadline, and writes a terminal
capture receipt. The three PCs must launch this command close together after
native run convergence; the comparator checks that their tree capture starts
are within five minutes. No capture receipt or synthetic check substitutes
for the physical three-PC reports.

On 2026-10-28, Windows PowerShell 5.1 parsed both scripts. A deep-path fixture
with 24 nested levels produced a complete run report with the expected two
files and 176 bytes. The launcher completed both collectors against that
fixture in Windows PowerShell 5.1, with empty stderr and a complete terminal
receipt; a launch four hours after source resume exited nonzero with a failed
receipt. Synthetic comparison controls passed: a matching
three-PC set exited zero; altered full-tree hash, capture after the deadline,
and altered run marker each exited nonzero with the expected issue. These
controls verify the capture logic only. The next fresh physical transfer must
produce its own reports and meet the actual deadline before this gate can pass.

The scheduled three-hour soak is separate. These scripts do not change its
staged capture binaries or acceptance evidence.
