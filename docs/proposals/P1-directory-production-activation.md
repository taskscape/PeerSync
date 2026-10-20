# Activate directory snapshots in the public coordinator

The internal coordinator preview now passes the original directory-clash
baselines, four-node RW/RW and RW/RO gossip/reference coverage, offline-owner
forwarding, edited-copy forwarding with both authors offline, late child and
causal reconstruction rebase, kind transitions and bounded recovery failures.
All 941 Engine tests pass; the preceding canonical runner passed unit/native,
simulation, transport and publishing checks. CI/main acceptance is still open.

Enable snapshot staging/publication in the ordinary public Puller and
TransportFolder constructors. This uses the tested coordinator sequence and
does not change standalone Scanner's legacy behavior: that primitive has no
publication worker of its own. Keep internal explicit-mode constructors for
legacy/integration fixtures. Receiving legacy absent roots remains supported;
absence never authorizes a subtree copy. Metadata availability work and GC
barriers continue to apply in both modes.

Verify the public entry points directly: a normal coordinator publishes signed
nested roots, a normal connected receiver preserves concurrent directory trees,
and unchanged repeated scans/passes do not churn root versions or native mtimes.
Run the full Engine suite and canonical local checks, keeping genuine legacy
fixtures explicit rather than treating production output as legacy metadata.
Any new convergence, scheduling or native failure blocks activation acceptance
until repaired. No checklist completion precedes merged green CI.
