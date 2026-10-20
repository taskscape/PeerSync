# Native Server verification critical path

PR #41 run 39411518907 fails the Server 2025 runner's eleven-minute Engine
deadline. Its captured stdout contains 934 unique passing cases, no reported
assertion failure, and no final TRX. Comparison with the same run's complete
Server 2022 TRX identifies 68 unfinished required cases: 21 BlockRetention,
43 TypeTransition and four HistoryPolicy cases. All native/cross-account
probes pass. Do not qualify incomplete scope or retry unchanged source.

The Server script first builds the complete product solution, including WPF,
packaging and unrelated test projects. This build took approximately 126
seconds before the Engine child began; the fixed child deadline then ignores
that cost. Replace only this Server build with an explicit solution of
Engine.Tests plus every required P0 spike. Build these outputs before starting
the immutable no-build child. Keep all other canonical/build scopes unchanged.

Use one measured script budget starting before the build and derive remaining
Engine wait from it. Keep the existing fifteen-minute workflow limit with
setup/upload/cleanup headroom, all five jobs, every native/service probe, full
Server 2022 Engine scope and only the existing Server 2025 capacity duplicate
exclusion. Retain failure output/process termination and record actual build,
child and remaining-budget timings. No P1 validation gate is deferred or
closed by this runner change.

Before implementation, commit this design/decision/WIP and measure the current
native runner locally. Verify the focused solution's exact build closure,
run the complete revised native runner and keep real source/outcome/timings.
Wait for the existing simulation/fuzz job before batching any additional
findings into one updated PR head. Require actual five-job PR/main acceptance;
the previous run remains failed and its evidence is retained.
