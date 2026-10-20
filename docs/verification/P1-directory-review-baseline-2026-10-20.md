# PR 38 directory integration review baseline

All five original PR CI checks passed on submitted head
32d38920531e8bbe804eac21031bc084b5e7659f. Review nevertheless identified four
integration gaps; passing CI is not acceptance while they remain open.

Added regressions reproduce eleven failures:

- Hidden/native case edits before received-root reconstruction lose observed
  queue attributes or cannot finish publication (two variants).
- Valid legacy absence and corrupt snapshot responses abort the pass before an
  unrelated valid file can transfer (two variants).
- Local deletion/recreation, local deletion with absent parent, and incoming
  file deletion leave a retained child unscheduled (three variants).
- Direct/session snapshot pages and a real connected snapshot page received
  before the first coordinator pass fail the storage root binding guard (three
  variants).
- Receipt accepts a page after physical root replacement without checking the
  bound root identity (one variant).

The focused Review filter runs 14 tests: 11 new failures and three existing
passing controls, zero skips; exit 2, 2.753 seconds. Source remains unrepaired
at this checkpoint. Evidence: `artifacts/p1-directory-review-baseline.log` and
`artifacts/tests/directory-review-baseline/`.

Preserve these tests and follow the design proposal. Do not merge or weaken
root/signature/native/preservation guards to obtain a green result. Submit all
repairs together after focused/full/canonical local validation; the original
CI result is historical evidence for the prior head only.
