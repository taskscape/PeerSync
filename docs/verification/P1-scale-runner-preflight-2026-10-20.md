# Five-node corpus runner: preflight and intermediate evidence

This is local P1 validation, not Gate P1 completion or a deployed transport
test. The opt-in runner lives in the existing benchmark console and is
invoked by `build/Run-Scale.ps1`; see `docs/scale-validation.md`. No hosted job
or schedule is added for these corpus runs.

Prototype source 9809f55 passed native oracle controls and five-node corpora
of 32, 512 and 4096 actual files. Its 4096-file run completes in 627.2841511
seconds, 29 total rounds: initial publication 45.180 seconds, original
convergence 460.561 seconds, partitioned mutations 15.887 seconds and healed
convergence 104.559 seconds. All five peers have 4096 original files, then
4092 final files after four deliberate deletions. It preserves all 4120
expected original/new payload versions after 24 conflicting overwrites,
four renames and four deletions. Queue/reference/native/version oracles pass,
and the ownership-checked success fixture is removed. This run predates the
later guarded generation, binary provenance and reproducible test-key changes;
it is intermediate engine/transport evidence, not an exact final-runner pass.

Final runner source 699c0144dde6c27dbe17a00129649c54e52c10a8 adds pinned
roots and guarded generation/overwrite/rename/delete handles, rejects reparse
traversal and requires independent mutated files. Public TransportFolder APIs
still perform every replication/index admission. Original/generated payload
digests are independent of the engine's private block cache. The final loss
oracle checks all expected payloads across exact visible native files and
indexed native archives, after stable high waters, drained work and complete
authenticated references on all five peers.

On that final source:

| Corpus | Nodes | Seconds | Expected versions | Overwrites / renames / deletes | Result |
| --- | --- | --- | --- | --- | --- |
| 32 | 5 | 13.198287 | 48 | 16 / 4 / 4 | Pass, six rounds |
| 512 | 5 | 103.4621507 | 536 | 24 / 4 / 4 | Pass, seven rounds |

Both finish with zero needed/apply/case/publication work and native equality,
and remove their owned fixtures. Compiler/configured analyzer diagnostics are
clean at warning/error, with no workspace warning or truncation. The Release
build is clean. Native oracle controls execute before every run and reject
missing nested bytes, private-only unindexed bytes and reparse aliases.

The stale-provenance control supplies a mismatched Git revision, returns exit
2 with an actionable rebuild message and creates no evidence/fixture. A
one-second deadline control returns the expected failure, saves its summary
and source state, and preserves the actual fixture with no false gate pass.
Test folder keys derive from the documented public owner marker on this
final source, so retained generated metadata remains inspectable after exit;
production key creation is unchanged. Reports bind the claimed source to the
executable informational version, record component binary SHA-256 values and
working-tree state, and emit bounded progress/queue information every 30
seconds.

Evidence in the managed worktree:

- `artifacts/p1-scale-4096.log` and `artifacts/scale/20261020T030433-4dad1a5cd0d94c23a4e4aea7e98576a1/` (prototype intermediate)
- `artifacts/p1-scale-guarded-32.log` and `artifacts/scale/20261020T032105-99dbd3cb6ac84e26936888e6ecf90449/`
- `artifacts/p1-scale-guarded-512.log` and `artifacts/scale/20261020T032119-97dc9bcc4ae84228bb7efe37b8b2da6f/`
- `artifacts/p1-scale-stale-control.log` and `artifacts/p1-scale-guarded-deadline.log`

The 100000-file run, canonical/CI/main integration and the separate 1000-seed/
five-day gate remain pending. Every smaller report keeps
`corpusRequirementSatisfied=false` and `gateP1Passed=false`.
