# P5.3 concurrent fresh-peer harness, 2026-10-23

The earlier million-file benchmark invokes `source.RunPassAsync` and then
`peer.RunPassAsync` serially after each index exchange. Installed peers run
their own coordinators independently. The benchmark now awaits both passes
together after the existing bounded index exchange. Source publication still
completes before the fresh peer connects, as in the original acceptance
scenario. The same two-settled-round condition, expected row count, 48-hour
safety ceiling and exact native pathname/byte verification remain.

An isolated self-contained win-x64 benchmark publish from the working source
passed a retained 1,000-file control. The scanner indexed 1,000 one-byte files
and 1,001 rows, then the destination converged in four rounds and verified all
1,000 native bytes and names in 45.252 seconds. It ended with 1,001 destination
rows and `correct: true`. The run created a separate destination root and
database inside the retained 1,000-file fixture, leaving the active older
million-file process and its fixture untouched.

The benchmark executable SHA-256 is
`6B07092E5FDB193ECD290628C53A343887697F6AEC42AC8FD8FEC9C2C525B79D`;
the Engine DLL SHA-256 is
`1A34D7B591C15C9CCC5EEAD20D99C800B2ABAA128A44BC00A03CF2DBA05DF815`.
The control log is retained at `artifacts/scale/p5-parallel-harness/control.log`
with SHA-256
`45B2671624931DA8692DD88CD50B1A042CC75C156FC00E6CE0B0689DBF949A20`.
The publish preceded this commit, so the provenance is an uncommitted
working-source build at parent `2c24342`; the product Engine code was unchanged
in this harness block. A fresh committed-source million-file run remains
mandatory. The older active binary `79d4e46` cannot validate this scheduling
change or the later product scan repairs.
