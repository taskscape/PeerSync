# P5.3 scanner safety-state staging, 2026-10-23

The scanner's million-file happy path was already bounded, but an adversarial
tree could still fill memory with a directory walk stack, ignored/protected
prefixes, failed-path diagnostics, or native names that cannot be encoded as
UTF-8. These are now held in the private, root-bound scan-observation database.
Windows path keys that contain unpaired UTF-16 surrogates use exact UTF-16
blobs rather than SQLite text; these names remain unsyncable. An interrupted
walk discards the workspace and cannot authorize a deletion.

The public scan result keeps the exact `ProblemCount` and at most 1,000
individual problems plus one `problem-overflow` marker. The marker causes both
rescan consumers to treat the scan as incomplete even if a read or enumeration
failure occurred after the sampled entries. Directory-publication uncertainty
is derived from all failures in the workspace, including those beyond the
diagnostic sample. The service's resume diagnostic uses the exact count.

Local verification on this source:

| Check | Result |
| --- | --- |
| Release solution build | 0 warnings, 0 errors |
| Scanner class, including 1,100 invalid native names and bounded problem output | 20 passed, 0 failed/skipped |
| Complete Engine suite | 1,042 passed, 0 failed/skipped |
| Complete Service suite | 56 passed, 0 failed/skipped |

The Engine and Service TRX files are in `artifacts/tests/`. A full Simulation
run, a native corpus with many unreadable paths, and a million-file scale run
using this later source remain open. The completed `512091f` million-file run
belongs to the immediately preceding committed source; it establishes the
normal-tree memory baseline, not a measurement of this change. The earlier
24-hour fuzz and million-file fresh-peer processes are independent and were
not restarted for this work.
