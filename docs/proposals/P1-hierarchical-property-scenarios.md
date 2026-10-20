# Hierarchical real-engine property scenarios

The existing three-to-seven-node flat-path harness and loss oracle remain
valuable regression evidence, but cannot establish nested directory/case/type
convergence. Extend it with a separately versioned hierarchical mode; preserve
the old mode's seed/operation sequence and direct replay behavior.

Generate nested writes, file and directory renames, empty directories, file and
subtree deletes, directory/file replacement and independent case spellings while
transport links partition/drop/heal. Use the public TransportFolder path with
real NTFS roots, SQLite, snapshot publication and recoverable appliers. Record
actual mutations, including skips, so a seed is reproducible. Keep payloads
small and generated; never operate on an external user directory.

The oracle must enumerate the visible tree recursively, including empty
directories and exact portable spellings. Exclude internal/invalid paths and
never follow reparse points. Preservation searches only visible generated
files and indexed native archives/conflict copies; a private cache or an
unindexed file under the internal namespace cannot satisfy a lost-version
check. Add nested positive and private-cache negative controls first.

After healing, require identical native trees, stable signed high waters and
drained needed/recovery/case/publication work on all nodes. Verify complete
authenticated reference coverage separately; no sweeping relaxation can mask
a graph hold. Use injected UTC/monotonic deadlines and bounded rounds, with no
wall-clock sleeps to bypass retries. Save generated fixture, trace, seed/mode
and direct replay command on failure; retain every failure for promotion to a
checked-in regression.

Keep flat and hierarchical counts, summaries and replay variables distinct.
The runner verifies both requested batches and restores prior environment.
Bounded smoke remains separate from a measured 1000-seed CI configuration,
five consecutive days and the five-node/100000-file phase gate. Measure runtime
before selecting bounded CI shards; this proposal starts no scheduled or
additional remote runs. Plan checkpoints never claim those gates from smoke.

Submit the three retained regression seeds with the bounded generated smoke
in the same directory-repair PR, so CI exercises the defects that motivated
the repair. Locally the three fixed cases take 181 seconds and a fresh
20-case batch takes 363 seconds. The previous x64 packaging job took about
ten minutes before this added scope. Allow that existing build job a
20-minute ceiling to accommodate the additional real-engine work; add no
jobs, schedules or duplicate runs. Timeout is a failure ceiling, not an
instruction to consume the entire interval. Required boundary fuzz and both
native Server jobs retain their existing scope and limits.
