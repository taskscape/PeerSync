# Resolve the P1 clock integration dependency cycle

Status: explicitly approved by the user on 2026-10-20: "Approve narrow phasing
(Recommended)". Apply only the validation split below; the deferred installed
service/production transport requirements remain mandatory at Gate P2.

`plan.md` section 0.1 requires phases to run strictly in order, and Gate P1
requires all P1 tasks ticked. P1.5.5b/P1.5.5c2 nevertheless require production
authenticated transport before closure. The actual QUIC/TCP transports and
direction-bound folder handshake are P2.2.1–3. Actual installed PeerSync host,
account/data initialization and settings are P2.5.1/2/4. P1.5.5c2a's remaining
fixed installed-service integration depends on that host. This creates a
forward dependency from the P1 gate into the phase it prohibits starting.

The existing P1 implementation verifies the identity-bound engine facade and
in-memory peer integration, real local control pipe authorization/owner/kernel
PID checks, typed runtime snapshots, and actual rendered WPF failure/recovery.
PR #37's final-head and main CI pass. Its merge report explicitly leaves the
installed PeerSync target and production network supervision unverified.
`ControlStatusRuntime` still reports production availability false; no test or
UI fixture is evidence of deployed authenticated peer synchronization.

Proposed narrow change:

| P1 requirement | Keep mandatory in P1 | Later mandatory integration |
| --- | --- | --- |
| P1.5.5b identity/clock index admission | Bound transport context, immutable Hello clock, transaction/handoff checks, skew rejection/recovery through public engine/in-memory transport | P2.2.1–3 actual certificate-bound QUIC/TCP/folder authentication and blocked index exchange |
| P1.5.5c1/c2 runtime status and UI | Real engine snapshots, authorized local pipe, owner/kernel PID, sequence/generation guards and inspected numeric/recovery WPF evidence | Runtime production sessions supervised by the real P2 host |
| P1.5.5c2a installed client | Fixed service/pipe lookup implementation, bounded SCM/account reads, actual pipe identity/lifecycle/cancellation checks, missing target handling | Actual installed fixed PeerSync service, configured account and running/stopped/missing lifecycle with the default client |

Mark only the last column `[DEFERRED validation to Gate P2: user approved
date]` in the affected P1 criteria. P1 boxes still require all retained P1
criteria, merged code and green CI. Do not mark production transport or real
service installation verified, relax pipe security or introduce a production
arbitrary endpoint override. Existing unavailable status remains explicit.

Add P2.5.7, "Installed authenticated-session clock/control integration", and
an explicit Gate P2 line. It depends on P2.2.1–3 and P2.5.1/2/4. Acceptance:
run the fixed PeerSync service under its configured account; connect with the
default owner/kernel-PID verified client; observe authenticated real-transport
clock skew blocking all index exchange, numeric reasons and reconnection
recovery through the real control UI. Cover stopped/missing/wrong owner/PID,
cancellation, caller authorization and service lifecycle with retained native
evidence. Use generated peers/fixtures and injected peer clock samples; do
not change the host's system clock or overwrite an existing installation.

Update the source design first, then the decision log and checklist. Existing
physical LAN and native NAT requirements remain mandatory at their already
approved P2/P4 gates. No scope, gate, benchmark, CI or external check is
silently treated as passed by this proposal.

Alternative: explicitly move the required production transports, handshake
and installed host implementation into P1 as prerequisites. That also changes
the phase plan; it should not happen through an undocumented early P2 start.
