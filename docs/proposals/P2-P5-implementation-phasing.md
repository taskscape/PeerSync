# Proposal: continue implementation while physical acceptance remains open

Status: **approved by the user on 2026-10-22; applied in `plan.md`**.
Section 0.1 formerly required each phase gate to be fully checked before the
next phase starts. The approved exception changes that ordering rule for
implementation only. It does not mark an unrun test as passed or permit a
beta/release claim.

## Why this is needed

PeerSync P2 code runs as a local installed Windows service and has a native
authenticated peer/control/UI probe. Gate P2 additionally requires a physical
1-Gbit LAN transfer, a three-PC 50 GB corpus and a 72-hour soak. Those require
external PCs and elapsed time. Gate P3 then requires a new tester to install on
two PCs, and Gate P4 requires real home/mobile NATs and another 72-hour soak.
These checks cannot be completed by a local Docker substitute. Without a
phasing change, strict gate order prevents implementing the remaining app,
rendezvous and hardening work while those physical resources are arranged.
P2.2.4 also names punch/relay candidates whose real endpoint sources are
built with the P4 rendezvous and relay; direct LAN/public candidate racing is
implemented, but its future-source completion remains open.

## Proposed narrow rule

Allow P3, P4 and P5 **implementation** to start after the preceding phase's
code, locally executable checks and required CI pass, even if its physical,
network or elapsed-time acceptance checks are still open. Retain all original
checkboxes unchecked and visible. An incomplete gate forbids labeling that
phase accepted, publishing a beta under its name or shipping v1.0.
For P2.2.4 only, move the punch/relay source integration into P4.2 without
claiming it complete in P2; the P2 connection manager keeps its tested
ordering/duplicate/retry behavior. Do not remove the original task or its
acceptance criteria.

Before any beta or v1.0 release, execute every original physical check with
the specified hardware and duration: P2 installed two-PC clock/control UI,
1 GiB mutual-certificate QUIC over 1-Gbit LAN with link/throughput evidence,
three-PC 50 GB corpus and 72-hour LAN soak; P3 clean tester two-PC setup and
VM upgrade/uninstall checks; P4 native NAT matrix, direct/relay/UDP paths and
72-hour WAN soak; P5 24-hour fuzz, review, scale and release-candidate LAN/WAN
soak. The P0.2.1 and P0.2.2 deferred validations remain attached to P2 and
P4 respectively. CI MITM/reflection tests stay in Gate P2 and must pass before
P3 implementation starts because they require no external hardware.

Each implementation phase remains on a separate draft PR with honest
`gateP*Passed=false` package metadata and a verification document separating
local, hosted, simulated and physical evidence. A later physical failure is a
product defect to fix, not an exception to acceptance. Any change to a design
or safety requirement still follows the plan's normal decision-log process.

## Application

Add an explicit dated decision to `plan.md` section 2 and a narrow exception
to section 0.1's ordering rule. Annotate Gate P2/P3/P4 status as physical
validation pending without ticking their boxes. Keep phase task boxes subject
to the existing merged-main, green-CI and full `Done when` rule. Begin P3
only after the P2 current-head PR is merged and its locally executable/CI
requirements are green. Do not retroactively claim any physical result.
