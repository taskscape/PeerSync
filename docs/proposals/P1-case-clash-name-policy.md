# P1 case-clash original-name policy

Status: approved by the user on 2026-10-19: "Approve deterministic winner
(Recommended)". The source design and plan now record this ownership policy.
Implementation and recovery requirements below remain mandatory.

The conflict table requires a visible `.case-conflict` copy. Four connected
regressions now reproduce its absence. Two causal case-rename controls pass.
The unresolved point is which spelling/version owns the original canonical
name when peers observe concurrent creations in different orders.

## Recommended concrete behavior

Use the existing deterministic conflict winner for the original name. Preserve
every other causally maximal live version with a distinct leaf spelling under
a stable `.case-conflict` name before merging away its concurrency. This
clarifies “first” as first in the shared conflict ordering, rather than first
packet received on each computer.

For example, independently created `file.txt` containing A (mtime 10:00) and
`FILE.txt` containing B (mtime 10:01) settle to:

| Receiver/order | Original name | Preserved case version |
| --- | --- | --- |
| Empty, A then B | `FILE.txt`, B | A under `file.case-conflict-<source-id>.txt` |
| Empty, B then A | `FILE.txt`, B | The same A copy |
| Initially owns A | `FILE.txt`, B | The same A copy |
| Initially owns B | `FILE.txt`, B | The same A copy |

Equal mtimes use the existing device/body tie-break. A causal rename from
`file.txt` to `FILE.txt`, even with an edit, creates no case copy. Parent-only
case changes follow directory records and do not manufacture child conflicts.
Ordinary same-spelling content conflicts retain their current archive policy.

The source ID is a domain-separated digest of the original signed body and
signature, independent of the forwarding peer and native root. Root binding
is local durable state, not part of the portable name. Bound/truncate names
through the existing validated sibling-name rules and allocate a numeric
collision suffix without overwriting unknown files. Never adopt an unknown
object as owned merely because its name resembles this format.

## Implementation and recovery requirements

1. Atomically admit authenticated competing envelopes and pending preservation
   work with the accepted index update. Preserve the original proof when a
   source row changes or a later merged vector dominates it. Do not acknowledge
   lost obligations or perform filesystem work from the DB callback.
2. Keep pending source manifests in reference coverage and keep original-path
   merge/publication behind a preservation barrier. Bound batches/journal work
   and apply backpressure rather than silently dropping excess versions.
3. Fetch/verify chunks outside the reference gate, capture independent blocks,
   and install an owned GUID temporary at its reserved case destination through
   a distinct FULL root/source/destination/FID-bound recovery intent. Use native
   no-overwrite moves and preserve changed/foreign occupants. Staging expiry
   must also respect pending case work.
4. Recover this work before scanning. After installation, the ordinary RW
   scanner signs the new path; incoming signed paths are never rewritten. RO
   copies remain local. Complete the preservation obligation only after guarded
   visible bytes and required local publication/accounting are proven.
5. Then allow ordinary original-path resolution/vector merging. Keep completed
   source markers to suppress repeated copies; preserve safe reference coverage
   through interruptions and bound eventual metadata retirement.
6. Test restart at every boundary, remote replacement, forwarding/gossip,
   collisions, cancellation, references/expiry and causal rename controls.
   Directory and nested mappings need their own authenticated subtree handling
   and remain mandatory before the parent task or Gate P1 can close.

## Literal first-arrival alternative

Each computer keeps its own first observed spelling/version at the original
name and materializes later ones as case copies. Because two disconnected
computers can each be first, this requires a shared signed ownership/mapping
protocol, conflict reconciliation and durable generation rules beyond ordinary
file-vector selection. Otherwise the original path can hold different bytes on
different peers or repeatedly create case copies during gossip. That protocol
must be specified and tested before mutation; merely changing local loser fate
does not meet the requirement.

The recommendation changes no zero-loss, visible-copy, native-safety, RO/RW,
case-rename or phase-gate requirement. It does explicitly choose deterministic
original-name ownership instead of literal per-computer first arrival.
