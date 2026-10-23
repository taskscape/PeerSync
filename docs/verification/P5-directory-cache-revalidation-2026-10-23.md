# P5 directory cache revalidation experiment, 2026-10-23

The immutable million-file fresh-peer run exposed extra source-publication
work. Its folder database had 181 completed directory caches, 180 of whose
`input_epoch` values preceded the global capture epoch, and 819 queued
directories at one read-only observation. A later complete scan temporarily
restaged already-published siblings even though their direct children had not
changed. This suggested an opportunity to avoid some global epoch invalidation.
A later read-only sample showed 210 completed and 790 queued directories:
the backlog was decreasing, so this observation did not establish a loop.

An experimental scanner shortcut compared the cached signed-child manifest
against current indexed children when only the global epoch differed. It also
kept pending child-directory publication as a cache miss. Focused directory
snapshot tests passed (193/193), as did the full Engine suite (1037/1037).
The full Simulation run, however, failed multiple retained hierarchical seeds
with visible trees or pending work still divergent after 32 healing rounds.
Seed 1583987140 failed under the experiment and passed in 16 seconds when
rerun with the committed scanner. The experimental source and tests were
restored to HEAD and **were not committed or deployed**. A local patch was
retained in the ignored `artifacts` directory for follow-up analysis.

The global epoch evidently carries a causal dependency beyond direct signed
child-manifest equality in these scenarios. Any future optimization needs a
proof of those dependencies and the retained hierarchical simulation gate.
The still-running million-file process uses the committed scanner. Its
fresh-peer convergence, source publication efficiency, and the separate peak
memory target remain unverified.
