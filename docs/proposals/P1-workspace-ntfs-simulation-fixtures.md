# Verified workspace NTFS Simulation fixtures

Status: investigation stopped before implementation on 2026-10-21. The user
explicitly directs stopping performance tuning and completing the system.
The hypothesis and references below are retained as historical notes; no
storage change or performance improvement is claimed.

Normal PR run 39598885501 fails at the shared 2700-second script budget.
The full seven-method baseline TRX passes (generated hierarchy 34m57s).
Five retained result files pass; seed 1648781653 remains incomplete after
the other five consume about 39m40s. All owned workers stop, with no cleanup
errors. Fuzz passes 600.0013087 seconds/112086 seeds/zero crashes. The other
four jobs pass their unchanged complete scopes. Do not merge this result.

These timings show two cohorts alone are insufficient. Fixture storage is
the next measured hypothesis, not an established production defect. All
Simulation fixtures use Path.GetTempPath; the actual run records two virtual
disks and a D: checkout, but does not record the effective temporary path or
volume mapping. Do not invent that missing information.

The runner-image maintainers document [C:/D: performance differences](https://github.com/actions/runner-images/issues/8755)
and [restored D: access for performance](https://github.com/actions/runner-images/issues/12744).
Those historical image reports motivate measurement; they do not prove this
private runner's current storage layout or speed. Microsoft documents that
[non-SYSTEM temporary-path lookup uses TMP before TEMP](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-gettemppath2w).

Create a unique workspace-owned temporary root per cohort. Require an actual
NTFS volume and ordinary non-reparse root ancestry. Set TMP and TEMP only
in each owned child ProcessStartInfo. Verify fresh-child GetTempPath before
worker startup; preserve parent/global environment and default campaign
behavior. Record both original/selected paths, filesystem/capacity and small
4-KiB Flush(true) latency samples before running unchanged tests. Never
hardcode a drive letter, choose RAM/ReFS storage, suppress flushing, change
SQLite durability or disable security software. Do not claim a speedup until
actual hosted results prove it.

Keep thirteen unique methods/six retained seeds/eight flat/four hierarchy
cases/maintenance and 600-second four-target fuzz, five jobs, 2700-second
script and 50-minute job limits. Add meaningful storage and child/parent
environment guards, run complete local verification, then require actual
green PR/main and artifact/source/scope audits. No new thousand-seed campaign
or unchanged full-corpus replay is required for this runner-only change.
