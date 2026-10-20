# Directory integration review repairs

PR #38's four findings have executable regressions and a combined repair:

- Reconstruction queues retain observed raw native attributes alongside the
  exact original template. Causal RW rebase applies supported attributes/case
  spelling only after original membership proof; canonical ancestry and original
  envelope binding remain checked. Parent mtime is preserved.
- Legacy missing roots persist source retry before graph/native work; malformed
  node responses defer their source with conservative budget accounting. Missing
  snapshots still hold the original preservation barrier, while an unrelated
  valid file transfers.
- Signed-file tombstoning, incoming file deletion and publication over a former
  file tombstone request descendant needed repair. Cached NeedsRescan state must
  not hide historical suspension. Normal parent creation, sender-range guards
  and durable retry deadlines remain binding; no implicit directory creation is
  added to the file applier.
- Direct/session receipt freezes input before initialization, binds the root in
  a FULL storage transaction under folder/reference gates, rejects unbound
  recovery/foreign roots and holds a native root lease through page admission.
  It does not mark initial scanning complete. Clock/disconnect generation
  checks still reject a stale page after waiting for initial binding.

The original baseline had eleven new failures and three passing controls.
The rescheduling variants were strengthened to announce the sender's complete
deletion range and advance injected retry time. All three still fail with the
old hierarchy decisions restored, exit 2, 1.771 seconds. A further cache-invalid
variant reproduces one failure alongside three passing controls, exit 2,
1.965 seconds, before removing the unnecessary usable-cache restriction.

Final canonical local Release validation:

- `build/Verify.ps1 -Runtime win-x64 -SkipFuzz`: exit 0, 168.508 seconds.
- All 1497 canonical unit/native cases pass: 960 Engine and 285 Storage,
  zero failures/skips. Includes all review regressions, concurrent initialization,
  input freezing, queued-root pinning, stale session rejection, root replacement
  and unbound recovery rejection. Storage tests cover binding durability/input
  freezing, immutable identity, pending recovery and SQL rollback.
- Eight real-engine generated scenarios, diagnostics/control readiness,
  native/transport probes, QUIC timeout checks and win-x64 publishing pass.
- Compiler/configured analyzer checks for Engine, Storage and both test projects
  are clean at warning/error with no workspace warnings/truncation; Engine was
  checked again after the final cache-state refinement.
- `git diff --check` passes. Local boundary fuzz was explicitly skipped; required
  CI remains mandatory. gateP1Passed=false remains.

Evidence: `artifacts/p1-directory-review-final-canonical.log`,
`artifacts/verification-win-x64.json`, canonical TRX files in `artifacts/tests/`,
`artifacts/p1-directory-review-full-engine.log`,
`artifacts/p1-directory-review-storage.log`,
`artifacts/p1-directory-review-hierarchy-baseline.log`,
`artifacts/p1-directory-review-cache-baseline.log`, and
`artifacts/simulation/20261020T004844-284cb3b4ac924339924a75d24063d410/`.

The first all-959-Engine canonical checkpoint passed before the last added cache
regression; the final results above supersede it. Original submitted-head CI
passed all five jobs, but those results do not validate this repair. Updated
CI/main, final review resolution and complete phase/external gates remain open.
