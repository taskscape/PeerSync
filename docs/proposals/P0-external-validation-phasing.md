# Approved P0 external-validation deferral

Status: approved by the user on 2026-10-19: "Defer external checks to P2/P4 and
continue implementation." The source design and checklist now record the
changes below. P0.2.1/P0.2.2 remain unchecked; P0 still requires its other
checks to pass and the code to be merged with green CI before P1 starts.

## Approved changes

1. Keep P0.2.1 and P0.2.2 unchecked and retain their existing IDs, original
   acceptance requirements and collected evidence. Add a deferred-validation
   annotation linking each outstanding check to its later mandatory gate.
2. Allow Gate P0 to accept only these two explicitly deferred environmental
   checks, once the remaining P0 work (including CI signing) is verified and
   merged. No unexecuted test is marked passed or dropped.
3. Require P0.2.1's physical 1-Gbit LAN benchmark before Gate P2. Preserve the
   1 GiB, ALPN psp/1, both certificate identities, matching hash, negotiated
   link speed, and measured throughput requirements. Tailscale does not count
   as this measurement.
4. Require P0.2.2's same-LAN, full-cone home NAT, port-restricted home NAT and
   mobile/CGNAT matrix before Gate P4. Retain the tested source-port binding
   alternatives and native error evidence. Neither Tailscale nor public
   Rendezvous99 reachability substitutes for this matrix.
5. Use the already documented direct QUIC/TCP plus relay-after-five-seconds
   path as the implementation baseline while the native NAT matrix is pending.
   Do not claim working coordinated hole punching. Gate P4 still requires
   the validated final transport decision, relay fallback and real-network
   evidence, with any design change recorded before checklist updates.
6. Keep every other phase acceptance check, soak, security, deployment,
   signing/installer and product requirement unchanged. In particular, this
   proposal does not make the service implemented or the full goal complete.

Implementation order: source design updated first, user's decision recorded
in plan.md section 2, then the narrowly enumerated gate/checklist changes.
No other acceptance requirement or phase gate is waived.
