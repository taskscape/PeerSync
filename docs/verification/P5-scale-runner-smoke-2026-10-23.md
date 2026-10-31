# P5.3 scale runner smoke, 2026-10-23

The new build/Run-P5-Scale.ps1 runner generates a new real NTFS corpus,
records source revision and binary SHA-256, and retains its fixture and
evidence. For one million files, the benchmark now reports corpus creation,
first index including database open, unchanged walk, a 30-second idle CPU
sample and both working-set and private bytes. It checks the P5 first-index
20-minute, idle 1% and 300 MiB limits. New-peer convergence is a separate
open requirement.

A 1,000-file smoke of the runner passed: it created 1,000 one-byte files,
indexed 1,001 rows including the root, hashed all 1,000 files, then found
all 1,001 rows unchanged with zero rehashes or updates and no sequence
change. Corpus creation took 0.223 seconds, first index 0.666 seconds and
unchanged scan 0.020 seconds. The local evidence is under
artifacts/scale/p5-20261022T223736-b20fccb7a74040e2a90c0d3710c22033.
That smoke ran before the harness commit, so provenance reports dirty source.

The one-million-file run and two-peer convergence remain open. The isolated
24-hour P5 fuzz campaign was still running during this smoke; avoid using a
contended host's timing as an uncontested performance claim.
