# PeerSync: Technical Implementation Plan

Source design: [Resilio-style P2P Sync for Windows: Implementation Plan](https://claude.ai/code/artifact/f706953c-b344-4c83-ac3f-59adc658dbbc), called **the design doc** below.
Plan created 2026-10-18. Target: PeerSync v1.0 (phases P0 to P5), optional extras in P6.

---

## 0. How to use this document

### 0.1 How to read it

- **What this file is.** The working checklist for building PeerSync. The design doc explains *what* we build and *why*. This file says *how*, *in what order*, and *how far along* we are. If the two disagree about design, the design doc wins. On progress, this file wins.
- **Layout.** Sections 1 to 5 are reference material: status, decisions, conventions, configuration, permissions. Sections 6 to 12 are the phases P0 to P6. Appendix A collects constants.
- **Hierarchy.** A phase (`P2`) contains workstreams (`P2.3`), and a workstream contains tasks (`P2.3.1`). Every phase ends with a **Gate** checklist. Task IDs never change.
- **Task format.**

  ```text
  - [ ] **P1.2.1 Short title** (where it lives in the repo)
    - Done when: acceptance criteria. All of them must hold before the box is ticked.
    - Ref: the design doc section that explains why.
  ```

- **Order.** Phases run strictly in order: a phase starts only after the previous gate is fully ticked. Inside a phase, workstreams can run in parallel unless a task says `Depends on:`. Tasks inside a workstream are listed in build order.
- **Code examples.** C# 14 on .NET 10 unless stated otherwise. They cover the parts that are easy to get wrong and are marked **Tricky:** where a plausible-looking alternative is subtly broken. They are reference implementations to adapt, not files to paste blindly. Each one names the file it belongs in.
- **Markers.**

  | Marker | Meaning |
  | --- | --- |
  | `- [ ]` / `- [x]` | Open / done |
  | `[WIP @name 2026-10-20]` | Someone has been working on it since that date |
  | `[BLOCKED: reason, since 2026-10-20]` | Cannot proceed |
  | `[DEFERRED validation to Gate Pn: user approved date]` | Unverified requirement remains mandatory at that later gate; only an explicit user-approved phasing decision permits the earlier gate |
  | `~~task~~ [DROPPED 2026-10-20: reason]` | Deliberately not done |
  | `(#123)` | The pull request that completed the task |
  | `[VERIFY]` | An assumption in a code example that a spike or test must confirm |

### 0.2 How to update it

1. **Start a task:** append `[WIP @you YYYY-MM-DD]` to its title line and commit it.
2. **Finish a task:** tick `- [x]` only when every "Done when" item holds, the code is merged to `main`, and CI is green. Remove the WIP tag and append the PR number, for example `(#123)`.
3. **Plan changes travel with code:** edit `plan.md` in the same PR as the code it describes, so `git log -p plan.md` shows who ticked what, and why.
4. **Never delete or renumber tasks.**
   - Dropped: strike it through and add `[DROPPED date: reason]`.
   - Too big: keep it and add children `P2.3.4a`, `P2.3.4b`. Tick the parent when all children are ticked.
   - New: use the next free number in its workstream.
5. **Blocked:** add `[BLOCKED: reason, since date]` and raise it the same day. Remove the tag when unblocked.
6. **Design changes:** update the design doc first, then add a row to the decision log (section 2), then adjust the affected tasks. A design change never lives only in this file.
7. **Gates:** the person who ran the check ticks the gate box and links the evidence (CI run, soak report, spike write-up). Then update the status summary (section 1).
8. **Status summary:** update section 1 when a phase starts, when a gate passes, and at least weekly while a phase is active.
9. **Code examples:** if the real code diverges from an example here for a good reason, replace the example or add `Superseded by src/...` under it. A stale example is worse than none.
10. **Spikes:** each spike (`P0.2.x`) produces `docs/spikes/P0.2.x-<name>.md` with the question, what was tried, the numbers, and the decision. Link it from the task.

---

## 1. Status summary

| Phase | Weeks (1 developer) | Status | Gate passed |
| --- | --- | --- | --- |
| P0 Foundations and spikes | 1-3 | Passed; physical LAN/NAT validation remains mandatory at P2/P4 under the user's deferral | 2026-10-19 |
| P1 Local sync engine | 4-8 | In progress: core, hashing, storage/scanning, needed selection, recoverable application, archive/history/retention, ordered pulls, UTC clock foundations, in-memory transport, hierarchy/staging lifecycle, regular-file case preservation, runtime clock pipe/WPF status and shared debug-run retention are merged with green CI. Authenticated directory snapshot capture/reference work passes focused local checks; scanner/subtree integration, installed-client evidence, authenticated production transport and full phase-gate simulations remain open. | - |
| P2 LAN sync as a service | 9-13 | Not started | - |
| P3 WPF app and installer | 14-17 | Not started | - |
| P4 Internet sync | 18-22 | Not started | - |
| P5 Hardening and v1.0 | 23-26 | Not started | - |
| P6 After v1.0 (optional) | 2-8 per feature | Not started | - |

---

## 2. Decision log

Newest first. Every row is also reflected in the design doc.

| Date | Decision | Design doc section |
| --- | --- | --- |
| 2026-10-19 | Trusted capture needs a dedicated trigger-maintained local mutation epoch, because readiness/cache repairs can change logical input without advancing advertised sequence. FULL-bind revalidated inputs and compare the epoch before progress/publication; generic enumeration clears the witness and retains two-pass checks. Reject epoch overflow atomically. | Sync engine design: directory case snapshots |
| 2026-10-19 | Stage directory metadata privately while retaining its prior signed row; publish exact child versions bottom-up. Add indexed parent/UTF-16BE ordering with row/byte-bounded enumeration, then trusted local-index capture continuation between full scans, input-change witnesses and pending-publication barriers. Require convergence and unchanged-mtime checks before activation. | Sync engine design: directory case snapshots |
| 2026-10-19 | Bind persistent directory capture drafts to root, parent spelling/native identity, ordered original-envelope manifest and accepted prefix/cursor. Flush bytes before FULL CAS progress, recheck unstable inputs, capture at most 1000 new members, and withhold sweeping while unpublished. Bound abandoned drafts and atomically release only with validated directory publication. | Sync engine design: directory case snapshots |
| 2026-10-19 | Add bounded canonical capture-builder checkpoints with base-128 member/buffer consistency and strict link/size validation. Only checkpoint successful, durably captured additions; root/parent/input-manifest/cursor journal binding remains mandatory before scanner activation. | Sync engine design: directory case snapshots |
| 2026-10-19 | Retire graph cache metadata only after complete fresh reference activation and 24 hours without source use. Persist retirement, share a 1000-row mutation budget per refresh, resume cleanup before graph work and rebuild newly needed retiring graphs while withholding coverage. Cache expiry never authorizes native deletion. | Sync engine design: directory case snapshots |
| 2026-10-19 | Validate directory graphs with root/source-bound FULL SQLite continuation: bounded pending page/record work, seen structure, unique child keys and metadata/chunk references. Flush owned metadata before atomically admitting children/completing parent work; resume after restart and withhold incomplete coverage. Bound job retirement before completing the lifecycle task. | Sync engine design: directory case snapshots |
| 2026-10-19 | Build directory snapshots from strictly ordered canonical child keys with 128-record pages and bounded carry buffers, sorting hashes only within each page. This fixes deterministic grouping without a whole-directory RAM sort. Keep per-pass persistent continuation and complete graph references mandatory before activation. | Sync engine design: directory case snapshots |
| 2026-10-19 | Bind exact encoded root-node length alongside the optional directory snapshot hash. Existing block requests validate exact length, so a hash alone would require guessing or weakening that contract. Legacy pairs remain empty/zero; live snapshot roots require a valid bounded pair. | Sync engine design: directory case snapshots |
| 2026-10-19 | Nonempty directory case copies need authenticated descendants; metadata/spelling/forwarder hints do not prove a subtree. Begin optional signed directory snapshot hashes and bounded content-addressed node/envelope encoding, then bottom-up capture/reference coverage and guarded member recovery. Manifest changes advance directory versions; incidental native child mtimes remain ignored. Missing legacy hashes never prove emptiness; no directory mutations are enabled by the codec foundation. | Sync engine design: directory case snapshots |
| 2026-10-19 | PR #37 readiness fixes pass x64/ARM64/fuzz/Server 2022; Server 2025 passes every probe but Engine exceeds eleven minutes. Retain the full 1,001-conflict startup saturation method on required Server 2022/default local verification and omit only its duplicate on Server 2025, retaining every native recovery/collision/cancellation case there. Record exact scope and per-test/long-running diagnostics; keep five jobs, fuzz and deadlines. | Testing strategy: CI |
| 2026-10-19 | The overlapped Server 2022 job specifically times out its duplicate 100,000-operation watcher stress under Engine disk load. Retain that complete required check in x64 canonical CI and default local verification; omit only its duplicate execution from Server security jobs, record the scope, and retain complete Engine plus all other native/cross-account probes on both Servers. Keep original deadlines and five-job coverage. | Testing strategy: CI |
| 2026-10-19 | PR #37's overlapped Server check exposes the P0 control probe's fixed one-second startup wait: pipe-client exits 1 while the server eventually prints READY. Replace the sleep with fresh PID-bound readiness, keep three-second connect/ten-second RPC deadlines and caller/owner checks, reproduce with a delayed real server and add sanitized RPC cause output. Do not infer successful readiness from elapsed time or stale files. | Testing strategy: CI |
| 2026-10-19 | PR #37 review finds GUID log runs bypass per-directory retention. Add shared startup/periodic inactive-run age/count/byte policy, pinned relative native guards and ownership markers; protect active writers and unknown/linked/reparse data. Also diagnose main #36 Server 2025 timeout: probes/setup consumed eight minutes, leaving only seven for Engine (704 passed before cancellation). Overlap the complete immutable no-build Engine run with independent probes, capture both outcomes and keep five jobs/fuzz/15-minute limits. Batch both repairs into one locally verified head. | Logging; Testing strategy: CI |
| 2026-10-19 | Implement the initial WPF clock-status view using one cancellable verified-client reconnect loop, dispatcher-serialized model updates, new generation per attempt, first-snapshot epoch binding and increasing sequences. Clear stale success on failure, bound retries and release streams/channels on close. Render signed minutes/reasons, peer connection state, unavailable production and truncation; inspect actual WPF output without a production fixture endpoint override. | Control API: clock-status slice |
| 2026-10-19 | Continue the clock control slice with a fixed installed-service SCM resolver and owner plus kernel PID verified client, bounded native buffers/deadlines, and internal fixture endpoints. Then host runtime snapshots only over the narrow named pipe with per-RPC token checks and 32 bounded event subscriptions; retain honest unavailable production state. Rendered UI and P2 installation/engine supervision remain required. | Control API: clock-status slice |
| 2026-10-19 | Cover partially occupied case capacity during startup: a whole recomputation batch can otherwise roll back every newly admitted source indefinitely. Commit a valid FULL prefix only on typed capacity saturation, rolling back the failing path via savepoint; preserve whole-batch remote receipt/cursor atomicity and rollback on other failures/cancellation. | Sync engine design: case preservation integration |
| 2026-10-19 | PR #36 x64 passed all tests then hit the 15-minute limit; its Engine capacity test took 5m32s on the hosted disk. Keep full Engine coverage on both required Server security jobs and omit only its third duplicate execution from x64 packaging. Preserve five jobs, all native/boundary tests, 600-second fuzz and 15-minute limits; local canonical verification stays complete by default and records explicit CI partitioning. | Testing strategy: CI |
| 2026-10-19 | PR #36 review identifies saturated startup recomputation, stale forwarding hints, prepared reparse-destination recovery and RO completed-source rescheduling. Add coordinator/native regressions; separate admission from scheduling, drain admitted copies under recoverable capacity backpressure while blocking ordinary repair work, refresh authenticated advertisers and park/release colliding recovery. Submit the repairs together after local verification. | Sync engine design: case preservation integration |
| 2026-10-19 | Harden file-case gossip with guarded ready-publication observation, never filename-based ownership. Add root/source retries and coordinator deadlines, cancellation recovery, and 24-hour bounded envelope compaction retaining compact completed IDs. Preserve pending sources and independent progress; subtree mapping remains open. | Sync engine design: case materialization |
| 2026-10-19 | Begin regular-file case materialization with kind-7 authenticated-source/root/destination/native-ID recovery. FULL installed-but-pending accounting precedes normal scan; only guarded local-publication proof releases original-path merging. Park incomplete work, preserve foreign occupants and retain directory obligations for subtree integration. | Sync engine design: case materialization |
| 2026-10-19 | User approved deterministic original-name ownership for case clashes. Preserve other maximal distinct-leaf versions under portable signed-envelope-derived case-conflict names. Begin root-bound FULL atomic admission, immutable source retention, reference/expiry protection and pending-cap backpressure before materialization. Require original-path barriers and guarded recoverable copies with normal RW publication; directory/nested handling remains mandatory. | Sync engine design: case clashes |
| 2026-10-19 | Begin case-clash preservation with connected regressions for empty and existing receivers, both arrival orders and causal case-rename controls. Preserve competing authenticated envelopes before vector merging can hide concurrency. Require visible case-conflict copies, no-overwrite destinations, durable source/root/reference binding and ordinary RW publication; complete first-name/gossip/recovery admission design before enabling mutations. | Sync engine design: case clashes |
| 2026-10-19 | Keep control.proto, typed status validation and reusable authenticated pipe/SCM client helpers in Control.Contracts, the UI/CLI control dependency. It may reference Core status values and gRPC client packages, never Engine/Storage. This avoids duplicating local service identity checks between app and CLI. | Solution structure: Control.Contracts |
| 2026-10-19 | Specify the clock control slice: real runtime GetStatus/WatchEvents with service epoch/sequence, bounded frames/rows/subscriptions and explicit unavailable-production state. Use narrow pipe ACLs/caller identification and client SCM owner plus kernel server-PID verification, then render typed clock reasons/minutes with reconnect/generation protection. Keep remaining service/control/production transport obligations open. | Control API: clock-status slice |
| 2026-10-19 | Begin clock UI status integration with endpoint-local Hello measurements and immutable authenticated-session snapshots. Exchange valid Hello UTC in both directions even when index admission is blocked, preserve closed skew/invalid reasons separately from reset admission, clear old warnings on a fresh valid session, bound recent history/snapshots and expose truncation. Control-pipe and rendered UI wiring remain mandatory. | Sync engine design: clock status; Control API |
| 2026-10-19 | PR #35 review identifies an omitted held-temporary handle in concurrent-target recovery and old parent spelling in catalog case-alias reuse. Reproduce edited prepared staging plus a concurrent target and interrupted alias reuse; forward native ownership instead of reopening and bind new intents to the current parent spelling while preserving catalog age/identity. Keep both repairs in one verified head. | Applying remote changes: staging lifecycle |
| 2026-10-19 | Cold staging recovery cancels pending deletion permissions before apply recovery/scan, retaining witnesses for fresh post-recompute expiry. This prevents destructive replay against stale needed state. Persist bounded cursor pages across all parked entries; continue full pages immediately, retry transient deferrals and reconcile missing metadata only after current references are available. | Applying remote changes: staging expiry |
| 2026-10-19 | Staging expiry uses a persistent root-bound bounded cursor, counts skipped rows, recovers prepared retirements first and verifies bytes before taking the reference gate. FULL reservation rechecks current age/needs/witness/apply state. Inaccessible reservations may be safely canceled without deleting bytes to free journal capacity; preserve foreign replacements and retire only confirmed-missing metadata. | Applying remote changes: staging expiry |
| 2026-10-19 | A local QUIC smoke reads an older dial failure and a new listen timeout from the same reused-PID folder. Scope evidence to the exact process lifetime/role/run ID, preserve historical logs and keep every deadline/progress/error assertion. Add a synthetic replay of the mixed-run evidence before runtime validation. | Logging, metrics and upgrades |
| 2026-10-19 | Start stale-staging lifecycle: FULL intent-bound ownership/age/content catalog, exact guarded resume, conservative legacy compatibility and separately journaled 24-hour unneeded expiry. Keep verification outside the reference gate, recheck needs before deletion, protect changed/foreign objects and never delete recursively/by name. | Applying remote changes: staging lifecycle |
| 2026-10-19 | Two connected-node regressions strand a child tombstone when its parent is absent or a file. Add rooted no-reparse absence probes; only ordinary-file/missing ancestors authorize metadata-only FULL tombstone completion. Preserve range guards, retained-original proofs and concurrent targets, re-probe recovery and rescan afterward. Access/sharing/reparse errors never prove absence. | Applying remote changes: hierarchy integration |
| 2026-10-19 | Start parent hierarchy integration with connected-engine regressions for nested tombstones whose former parent is absent or now a file. Preserve ancestors/user objects and all prior versions; retain range barriers and durable metadata completion. Diagnose actual failures before changing guarded path/recovery behavior. | Applying remote changes: hierarchy integration |
| 2026-10-19 | PR #34 review identifies four connected-session holes: re-read sender high-water before committing a page beyond the old target; bind queued admission to a disconnect generation; normalize connection-local cancellation/disposal into per-item source failure; and share folder teardown completion among every caller. Add focused concurrency/range regressions before one updated CI head. | Testing strategy: connected-session invariants |
| 2026-10-19 | Begin reproducible real-engine property scenarios on 3–7 NTFS roots over the injected in-memory fabric. Record published payloads, heal and compare visible bytes, and require every version in current/archive/conflict bytes rather than private cache alone. Save replay cases/traces. Initial flat-path smoke is separate from full scenario coverage, 1,000 CI cases and five-day/100k-file gate evidence. | Testing strategy: property harness |
| 2026-10-19 | Start transport-backed engine simulation: per-connection identity-bound handlers, frozen bounded messages, independently scheduled requests and deterministic directional delay/drop/partition delivery. Drain cancellation before handler disposal. Route connected index/chunk exchanges through IPeerTransport and retain symmetric clock admission; trusted fabric identity does not prove real TLS/folder authentication. | Testing strategy: in-memory transport |
| 2026-10-19 | Ordered pull scheduling: transactionally cache authenticated work descriptors/fingerprints, persist monotonic sender-range targets and exact-work root-bound backoff, and query bounded priority work without loading every envelope. Guard destructive range rechecks, route downloads from the applier's actual winner, recover before scans and rescan preserved data outside mutation gates. Case/hierarchy coordination and production simulations remain required parent integration. | Applying remote changes |
| 2026-10-19 | Bounded historical metadata cleanup: FULL retiring-manifest state hides partial drains and resumes in batches of at most 1,000 mutations under fresh covered references/both gates. Reused history copies durably recapture exact signed chunks before renewed holds. Preserve attached/pending/foreign sources; drain detached released metadata and only aged unlinked completed witnesses. Do not change byte references, orphan clocks, needed work or deletion journals; report backlog for retries. | Detecting local changes: published block ownership |
| 2026-10-19 | Referenced partial duplicates may expire only when an independent, verified, durably cataloged complete replacement is bound by file ID and guarded through FULL retirement. Revalidate replacement after restart; loss preserves a present prefix or withholds readiness for missing bytes. This closes permanent maintenance deferral without releasing needed bytes. | Detecting local changes: published block ownership |
| 2026-10-19 | Capture retirement integration: recover old block deletion journals first. While captures defer, only cancel present block deletions or reconcile already missing data; do not delete present blocks. This prevents journal-capacity deadlock and stale readiness. Share the 1,000 pending-intent and per-pass budgets across both retirement kinds. | Detecting local changes: published block ownership |
| 2026-10-19 | Incomplete capture expiry uses its flushed root/proof/data identity proof, fresh authenticated coverage and 24-hour age, and requires an unreferenced strict partial prefix. FULL exact prefix-hash/length retirement precedes guarded data/proof removal. Ordinary recovery defers pending retirements; new references cancel present work or withhold missing readiness, while changed/unknown objects remain protected. Refresh again afterward and share the 1,000-operation sweep bound. | Detecting local changes: published block ownership |
| 2026-10-19 | Owned block sweeping: migration marks legacy ownership; initialize a root-bound ArchiveDays grace window once. FULL bounded orphan observations reset on fresh references/re-registration. Normal expiry follows ArchiveDays, capacity may retire unreferenced bytes only after 24 hours. Fresh fully covered references and both gates remain held through verified native deletion with exact FULL intents/completion/recovery; new references cancel present deletions or withhold missing readiness. Bound deletion passes/intents to 1,000. | Detecting local changes: published block ownership |
| 2026-10-19 | Policy reference retirement: FULL-remove exact accounted expired markers or completed source-bound private retirements; public copies require authenticated local observation. Release only unheld aged/completed sources with root-bound durable authorization, defer pending retirements, cancel release atomically on new reuse, and exclude released archive manifests from fresh snapshots. No filesystem deletion is enabled. | Detecting local changes: published block ownership |
| 2026-10-19 | Legacy reconstruction tests exposed duplicate archive rows when a missing file's indexed name was reused. Reserve indexed paths as well as native names, using an invariant-uppercase indexed key and bounded durable migration backfill before opening. Preserve existing source identities and original spelling. | Detecting local changes: published block ownership |
| 2026-10-19 | P1.2.4b2b2c legacy bootstrap: authenticate exact markers under the pinned root, FULL-account matching guarded copies, or reconstruct through existing kind-4 recovery with the original observation time. Preserve current user files and prior protection on failure; wait for pending applications. | Detecting local changes: published block ownership |
| 2026-10-19 | P1.2.4b2b2c: policy accounting must protect the observation time of a newer history snapshot reusing an older private archive, before age/cap pruning or reference release. Bind retirement authorization to stable source IDs; preserve newer reuse, legacy/pending/current/needed data and unknown objects through verified grace/cap sweeping and recovery. | Detecting local changes: published block ownership |
| 2026-10-19 | P1.2.4b2b2b: capture verified private archive bytes through ordinary block ownership, then stage bounded root/source/file-bound manifests and FULL-activate after complete hash/length verification. Include active manifests and stable provenance coverage in reference snapshots, retaining detached/unknown-source protection across row removal/reuse. Policy release and sweeping remain mandatory follow-up work. | Detecting local changes: published block ownership |
| 2026-10-19 | P1.2.4b2b2a: migration and database triggers preserve exact archive provenance across creation, metadata replacement and missing-row deletion, in the original transaction. Stable source IDs prevent archive-ID reuse from conflating versions. This is reference-protection metadata, not ownership/deletion authorization. | Detecting local changes: published block ownership |
| 2026-10-19 | Run 39224281962 passes all 633 tests and native probes, then x64 exceeds 15 minutes during repeated ReadyToRun compilation of P0 bundles. Publish these six self-contained diagnostic executables using JIT, preserving locked restore, outputs, signing and all checks. Production installer ReadyToRun remains required. | Testing strategy: CI; Packaging and deployment |
| 2026-10-19 | Billing investigation found 49 commits with duplicate feature-push/PR CI runs. Run the full unchanged matrix on PR updates, main pushes and manual dispatch only; batch related pending work for a single tested head. Preserve native checks, both builds, publishing and 600-second fuzz. | Testing strategy: CI |
| 2026-10-19 | PR #32 passes all tests but two jobs exceed 15 minutes during upload/cleanup. Initialize exclusively created, still-empty databases in one transaction; preserve backups before every existing-file upgrade step, full checks and the runtime limit. | Storage: migrations; Testing strategy: CI |
| 2026-10-19 | The host-owned pull coordinator shares the process-wide scan budget, recovers before startup scans, incrementally recomputes changed local paths, filters incomplete sender-range deletions before bounded selection, coalesces post-apply scans and waits on durable retries or host notifications without holding gates. | Sync engine: Applying remote changes |
| 2026-10-19 | PR #33 review identifies scheduled recovery losing deletion barriers and scan/admission edge cases. Persist scheduled-deletion recovery kinds 5/6, recheck current sender ranges under the reference gate, contain unrepresentable native timestamps, filter invalid local wire paths, include native-cache paths and force needed repair after partial failure. Green CI alone does not close these issues. | Applying remote changes: recovery and incremental scans |
| 2026-10-19 | Main run 39250207275 passes all 834 tests but fails the QUIC progress smoke: authenticated stream setup leaves under five payload seconds before its six-second deadline. Emit an initial data progress sample immediately, then keep the existing five-second cadence and unchanged deadline checks. | Observability: cross-machine diagnostics |
| 2026-10-19 | Add UTC ticks to Hello and freeze a peer clock sample at monotonic receipt time. Check advancing peer time against local UTC, permit the exact ten-minute boundary and block missing/invalid/backwards/overflowed samples. Keep index admission and rendered UI mandatory children of P1.5.5; foundations alone do not close it. | Protocol handshake; Sync engine: clock sanity |
| 2026-10-19 | Bind peer index admission to authenticated transport identity and recheck clocks under reference/writer gates, with pure callbacks before mutation/commit and rollback on clock changes. Check outgoing materialized pages before handoff. Transport wiring and UI remain mandatory integration work. | Sync engine: clock admission |
| 2026-10-19 | P1.2.4b2b1 baseline exposed a download deadlock when index receive took the filesystem gate. Use a separate per-index reference/index gate: maintenance acquires filesystem then index gate, receive/recompute takes only index gate and never waits for filesystem work. This preserves the existing newer-index-during-assembly behavior while excluding mutation during reference enumeration/sweep. | Detecting local changes: published block ownership |
| 2026-10-19 | P1.2.4b2b1: stage bounded persistent current/needed/history reference batches and FULL-activate only after root/authentication validation under the shared folder gate. Serialize index receive/recompute as reference-changing paths. Preserve the prior snapshot on failure/cancellation; unmanifested archives block future sweeping. Archive capture/policy release and verified eviction remain mandatory follow-up work. | Detecting local changes: published block ownership |
| 2026-10-19 | P1.4.4: reproduced a missed same-identity/length edit after restoring last-write time. Persist native change time for unchanged-file checks; hash on missing/different stamps, preserve signed versions/partitions on cache-only refresh, cache final apply stamps and verify rename bytes before partition reuse. Directory child timestamps remain excluded. | Detecting local changes: unchanged-file cache |
| 2026-10-19 | P1.2.4b2a: journal verified archive policy retirement with FULL root/file/row-bound witnesses before deletion; atomically complete witness and remove the unchanged row only after absence. Recover before daily scheduling, preserving unknown/replaced objects. Missing rows without witnesses never prove authorized history retirement. Reference sweeping remains a separate mandatory child. | Detecting local changes: published block ownership |
| 2026-10-19 | P1.2.4b1: flush root/data/proof-identity-bound capture metadata before staging writes, register verified installed block ownership in FULL transactions before readiness, and reconcile complete owned captures before application/history recovery. Preserve invalid/unknown/changed objects; remove proofs only after durable catalog registration. Reference snapshots/retention remain P1.2.4b2. | Detecting local changes: published block ownership |
| 2026-10-19 | P1.2.4a: reconstruct locally superseded authenticated file snapshots from immutable blocks before committing changed/deleted/type-replaced scan rows. Use archive/oversize/keep-both policy, kind-4 root/source/staging-bound intents and FULL-durable archive/source-marker completion; recover without mutating current user files and keep manifests for safe block retention. RW/RO conflict copies follow normal publication policy. | Detecting local changes: Local supersession |
| 2026-10-19 | P1.2.3/P1.2.4: capture immutable verified chunk bytes during hashing and before live record publication, persist readiness (withhold uncaptured legacy rows), and reuse/serve only verified root-bound blocks. Do not hardlink user originals. Local supersession archiving and reference-aware retention/cleanup remain mandatory follow-up work for the no-lost-version gate. | Detecting local changes: Published content |
| 2026-10-19 | P1.5.3d: use the guarded installer for file/directory type transitions. Files retain the configured archive/conflict policy; entire directory losers move to no-overwrite public conflict siblings so unknown children survive. Bind node kinds/identities, stage empty directories, and atomically withhold stale descendants/remove their needed work until rescan. RW publishes preserved copies normally; RO retains local copies. | Version vectors and conflicts; Applying remote changes |
| 2026-10-19 | Reproduced an own-write child version after a directory case change. For an already matched canonical path key, compare the entry's own leaf spelling; parent case follows directory records. Preserve descendant signed bytes/counters and still detect actual leaf case edits. | Applying remote changes, step 6 |
| 2026-10-19 | P1.5.3c: apply directory metadata/creation and nonrecursive deletion with a signed, root/directory-bound recovery intent. Process individual signed child tombstones before parents; a nonempty parent is physically retained, logically tombstoned and rescanned so unknown children survive and RW/RO behavior follows normal scanning. Preserve changed identities/types. | Applying remote changes, step 6 |
| 2026-10-19 | Reproduced reused staging retaining a former target's protected DACL. For a new destination, refresh staging DACL/inheritance and creation time from an exclusive, empty sibling metadata reference with native delete-on-close, for both remote installation and local restore. Existing targets still supply replacement metadata. | Archive, ignore rules and special files; Applying remote changes |
| 2026-10-19 | P1.5.4b: restore verified archive bytes through owned staging and the approved recoverable installer. Use a distinct unsigned local intent format, preserve current/unscanned targets, refuse occupied copy destinations, and complete via durable archive accounting plus stale-local suppression. The ordinary scanner creates the new RW version or RO local edit; archived signatures are never forwarded as a restoration. | Archive, ignore rules and special files |
| 2026-10-19 | P1.5.4: prune indexed, size/hash-verified archives oldest first for age and the actual hosting-volume cap. Share the folder gate, wait for recovery, pin ancestors and preserve changed/locked/reparse paths. Delete data before metadata, reconcile missing rows, persist daily success and retry deferrals at 10–600 s. Restore remains a local edit and is a separate child task. | Archive, ignore rules and special files |
| 2026-10-19 | P1.5.3b: assemble in exclusively created GUID sibling temporaries with persisted root/file ownership. Park staging ownership for verified restart reuse. Keep retained-file and ancestor guards through durable completion; concurrent targets trigger atomic archive accounting and stale-record suppression until rescan. | Sync engine design: Applying remote changes, steps 3 and 5 |
| 2026-10-19 | User approved recoverable handle moves: retain the guarded old version, durably journal before moving it, atomically install into the vacant target, and recover before scanning. External apps may briefly see the target missing. This supersedes the strict existing-target atomic replacement choice below; all preservation, retry, conflict and phase-gate requirements remain. | Sync engine design: Applying remote changes, step 5 |
| 2026-10-19 | P1 review repairs: select RO winners using signed vectors, merge cached knowledge afterward, preserve nested vector unknown fields, reject unsupported attributes and use safe conflict-name fallback stems. Engine diagnostics use best-effort writes so logging cannot change sync outcomes. | Sync engine design: Version vectors and conflicts; deployment diagnostics |
| 2026-10-19 | P1.5.3b: share a per-index asynchronous gate across scans and filesystem apply/recovery, and reject scans with pending apply intent before walking. Cancellation releases component gates; unrelated folder owners remain independent. | Sync engine design: Metadata database |
| 2026-10-19 | P1.5.3: preserve atomic existing-target installation and the held verified target guard; validate native replacement/POSIX semantics and no-overwrite old-object retention before coordinator integration. Unsupported behavior fails without substituting a two-rename gap. | Sync engine design: Applying remote changes |
| 2026-10-19 | P1.5.3a: persist bounded apply intents before destructive filesystem work; use serialized FULL-synchronous transactions for intent lifecycle and atomic local/chunk/sequence/archive/needed completion. Keep ordinary scan writes NORMAL and retain intents on failed commits or local-sequence mismatch. Add migration 4 through the existing backup path. | Sync engine design: Metadata database |
| 2026-10-19 | P1.5.3: prepare replacement DACL/inheritance protection and creation time through held handles before moving the original; new sibling files inherit the parent. Validate Windows timestamp representation before mutation and set synchronized read-only/hidden flags after times. Native helpers borrow guards without closing them. | Sync engine design: Applying remote changes |
| 2026-10-19 | P1.5.3: assemble only into caller-owned temporary streams; verify resumed/local/network chunks and the independent whole-file hash. Treat the local chunk index as a bounded hint, reject link traversal, own transport bytes before verification, and log correlated counts/source codes only. | Sync engine design: Applying remote changes |
| 2026-10-19 | P1.5.3/P1.5.4: introduce root-relative mutation primitives with pinned no-reparse directory leases, exclusive creates and handle-based no-overwrite archive/conflict moves. Validate private metadata/temp names separately from remote sync paths. | Sync engine design: Applying remote changes |
| 2026-10-19 | P1.5.1: add migration 3 indexing remote_files(path_key), so affected-path winner queries do not scan every device's complete index. | Sync engine design: Version vectors and conflicts |
| 2026-10-19 | P1.5.1: commit remote records, sender cursor and affected needed rows atomically; verify before submission and select from the writer transaction's current local/all-remote snapshot. Replays are idempotent. RO local changes remain blocked unless overwrite is enabled; local merged causal metadata never rewrites forwarded signatures. | Sync engine design: Version vectors and conflicts |
| 2026-10-19 | P1.5: follow the source design's live mtime winner rule; use signed deletion time for tombstones, then full DeviceId and exact-body tie-breaks. Remove causally dominated versions before deterministic selection and max-merge vectors without a new edit counter. This corrects the draft example's blanket changed_ticks comparison. | Sync engine design: Version vectors and conflicts |
| 2026-10-19 | P1.4.3: coalesce startup/periodic/watcher requests and use a process-wide single-folder scan budget to enforce the per-disk limit even across partitions/unknown volume mappings. Release it before 10-600 s retries. Set very-low I/O hints on scanner file handles; report rejected hints. | Sync engine design: Detecting local changes |
| 2026-10-19 | P1.4.2: bound watcher ingress and pending paths; saturation/native errors request a consumer-side full scan. Use monotonic 2 s quiet/30 s maximum debounce, include both rename paths and emit relative batches. Initially schedule full safe walks for ready batches; committed own-write metadata prevents version loops. | Sync engine design: Detecting local changes |
| 2026-10-19 | P1.4.1 benchmark exposed quadratic initial chunk indexing: add schema migration 2 with an index on chunks(path_key). Preserve v1 databases and use the existing backup/transactional migration path. | Sync engine design: Detecting local changes |
| 2026-10-19 | P1.4.1: use handle-based directory metadata with 128-bit IDs and root-relative no-reparse file opens; pin root identity and preserve failed subtrees. Directory child-mtime changes alone do not publish versions. RO scans track local state without signing. Correlate scan events and numeric counters without logging names/content. | Sync engine design: Detecting local changes |
| 2026-10-19 | P1.3.3: place the shared folder-key protector in Crypto, preserving fixed v1 app entropy and machine-scope DPAPI. Protect a versioned role/FolderId/material frame, verify context on unprotect and clear plaintext buffers. State writes protect internally and key reads are audited. | Security model: Secrets at rest |
| 2026-10-19 | P1.3.2: local sequence allocation, file-row storage and supplied chunk-index replacement share the batch transaction. Unsyncable local metadata has no signed record; remote paths remain strict. Batch at 1,000 rows and preserve envelope bytes verbatim. | Sync engine design: metadata database |
| 2026-10-19 | P1.3.1: one normalized-path writer owner and a bounded channel, transactional operations with rollback, independent read-only connections, and online SQLite backup before transactional user_version migrations. Run synchronous SQLite I/O on the writer worker; retain WAL/NORMAL/foreign_keys/busy_timeout/temp_store settings. | Sync engine design: metadata database |
| 2026-10-19 | P1.2.2: bound one-pass hashing to the original length and compare handle/path size, timestamps, attributes and 128-bit file identity. Detected mutation/removal/replacement retries; progress exposes counts only. This is metadata-based detection with shared writers, not an atomic snapshot. | Sync engine design: Detecting local changes |
| 2026-10-19 | P1.1.5: specify the ignore glob subset and ancestry/negation behavior, accept Windows pattern separators, and match Unicode scalars without regex backtracking. Local rule text is bounded at 64 KiB, 1,024 rules and 4,096 characters per line. Invalid paths and internal metadata remain excluded. | Sync engine design: Archive, ignore rules and special files |
| 2026-10-19 | P1.1.3/P1.1.4: Core owns the immutable signed envelope and pure path rules; the factory lives in Protocol (references Core/Crypto) to avoid a Core-Crypto cycle. Sign and forward exact body bytes with folder/domain binding; verify before parsing. Path containment is lexical; filesystem layers must also reject reparse traversal. | Protocol specification: FileRecord; solution structure |
| 2026-10-19 | P1.1.2: canonical version vectors omit zero counters and reject increment overflow. Compact storage is count + sorted fixed64 little-endian device IDs + unsigned LEB128 counters, rejecting noncanonical and malformed encodings. Protobuf remains the record wire representation. | Sync engine design: metadata and version vectors |
| 2026-10-19 | P1.1.1: use CRC-10/ATM for the two-character key checksum, preserving lengths and normalization, so every single Base32-symbol typo is detected. The draft's ten SHA bits admitted a reproduced single-symbol collision. HKDF/signing/security domains are unchanged; independent OpenSSL vectors and exhaustive typo tests cover the correction before any folder keys ship. | Protocol: Keys and identifiers |
| 2026-10-19 | User approved deferring only P0.2.1's physical LAN measurement to Gate P2 and P0.2.2's native NAT matrix/final decision to Gate P4. Keep both tasks unchecked, preserve all acceptance requirements, and permit Gate P0 after its other checks pass. Use direct QUIC/TCP plus relay after 5 s as the implementation baseline; no coordinated-punch success is claimed. | Phased roadmap; NAT traversal |
| 2026-10-18 | Shared diagnostic metadata/journaling lives in Core without upstream project dependencies. Domain models remain free of filesystem I/O; bounded log-file I/O is an explicit exception for the new deployment diagnostics requirement. Each concurrent process uses its own journal directory. | Solution structure and tech stack |
| 2026-10-18 | User requested deployment diagnostics: opt-in structured events, shared session correlation, native errors, safe log bundles, and a server/second-PC runbook. Windows-version and NAT evidence must be recorded separately; current Docker runs Linux containers. | Logging, metrics and upgrades |
| 2026-10-18 | Two-PC troubleshooting: separate HRESULT/native/QUIC error codes, record phase/bytes/progress, reject metadata-only diagnostic bundles, and give the P0 transfer its own timeout after authentication. Existing Tailscale evidence remains separate from physical LAN and native NAT validation. | Logging, metrics and upgrades |
| 2026-10-19 | P0 signing smoke uses a fresh non-exportable CI test certificate, a separately signed/timestamped EXE, a PE-tamper rejection check and cleanup of its temporary key/root trust. No user private key is exported and no public release-signing claim is made. | Installer: Signing |
| 2026-10-18 | P0.2.4: use `Blake3.Native` 3.0.2; a 10 GiB pass reached 1.051 GB/s after buffer-copy fixes. Keep the 1 GB/s reference target; other hardware remains unverified. | Cryptography; Testing strategy |
| 2026-10-18 | P0.2.2 provisional: same-port QUIC, native MsQuic shared binding and TCP reuse failed on the development PC. Keep reachable-endpoint QUIC/TCP and relay fallback for hard NATs; coordinated punching remains blocked on the physical NAT matrix. | NAT traversal |
| 2026-10-18 | Folder proofs put the sender first. DeviceIds come from the TLS certificates, never from `Hello`. | Protocol specification: Connection and handshake |
| 2026-10-18 | Downloads are assembled next to the target as `name.ext.!psp`, not in `.psp\tmp`. `File.Replace` keeps the target's ACL. | Sync engine design: Applying remote changes |
| 2026-10-18 | Configuration and secrets layout as in section 4 of this plan | Windows service: Account, permissions and paths |
| 2026-10-18 | Permissions model as in section 5. The device certificate lives in `LocalMachine\My` and is created by `PeerSync.Service.exe --init`. | Windows service; Security model |
| 2026-10-18 | Rendezvous host: Windows Server 2025 or the latest release, `sync.taskscape.com:99`. TCP 99 allowed by the provider, UDP 99 assumed and verified in P4. | Central rendezvous server |
| 2026-10-18 | One self-hosted rendezvous server (tracker plus relay) on the taskscape.com machine, port 99 | Central rendezvous server |
| 2026-10-18 | Conflicts: latest version wins, and the loser is archived. A conflict copy is made only above the archive size limit, or when "Keep both copies" is on. | Sync engine design: Version vectors and conflicts |
| 2026-10-18 | No Windows 10. Windows 11 and Server 2022 or later (`MinVersion=10.0.20348`). | Summary |
| 2026-10-18 | Installer: Inno Setup 7.x | Packaging and deployment |
| 2026-10-18 | Own protocol PSP/1, not Resilio-compatible. .NET 10. QUIC first, TCP plus TLS 1.3 fallback, relay last. | Scope and key design decisions |

---

## 3. Conventions

### 3.1 Repository layout

```text
PeerSync/
├─ plan.md                         this file
├─ docs/spikes/                    one write-up per spike
├─ src/
│  ├─ PeerSync.Core/               key text form, FileRecord, version vectors, path rules, ignore matcher
│  ├─ PeerSync.Crypto/             key chain, Ed25519, BLAKE3, FastCDC, encrypted-peer scheme (P6)
│  ├─ PeerSync.Protocol/           .proto files, frame codec, validation, limits
│  ├─ PeerSync.Storage/            SQLite schema, migrations, repositories
│  ├─ PeerSync.Engine/             scanner, watcher, hasher, puller, conflicts, archive, scheduler
│  ├─ PeerSync.Net/                QUIC/TCP transports, handshake, sessions, discovery, relay client, NAT mapping, in-memory transport
│  ├─ PeerSync.Service/            Windows service host, control API server, --init, --remove-data
│  ├─ PeerSync.Control.Contracts/  control.proto and the generated client
│  ├─ PeerSync.App/                WPF app
│  ├─ PeerSync.Rendezvous/         tracker and relay (Windows service on the taskscape.com machine)
│  └─ PeerSync.Cli/                psp.exe
├─ installer/                      PeerSync.iss, PeerSync.Rendezvous.iss
└─ tests/                          *.Tests, PeerSync.Simulation, PeerSync.IntegrationTests, PeerSync.Fuzz, PeerSync.Benchmarks
```

### 3.2 Build settings

`Directory.Build.props` at the repo root:

```xml
<Project>
  <PropertyGroup>
    <!-- Windows-only product. A versioned TFM is required: SupportedOSPlatformVersion
         may not be higher than the TFM's platform version (error NETSDK1135). -->
    <TargetFramework>net10.0-windows10.0.20348.0</TargetFramework>
    <SupportedOSPlatformVersion>10.0.20348.0</SupportedOSPlatformVersion>
    <RuntimeIdentifiers>win-x64;win-arm64</RuntimeIdentifiers>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
  </PropertyGroup>
</Project>
```

Package versions live in `Directory.Packages.props` (central package management). `PeerSync.Service` and `PeerSync.Rendezvous` use `Microsoft.NET.Sdk.Web`, because both host Kestrel. `PeerSync.App` sets `<UseWPF>true</UseWPF>`.

### 3.3 Branches, CI and definition of done

- Trunk-based development on `main`. Short-lived branches, squash merges, and PR titles that start with the task ID (`P1.2.1 FastCDC chunker`).
- CI on GitHub Actions `windows-latest`:
  - build x64 and Arm64
  - unit and simulation tests
  - loopback integration tests
  - a 10-minute fuzz smoke run
  - `dotnet publish`
  - ISCC for both installers (from P3)
  - signing (once a certificate exists)
- Trigger the full workflow on PR updates, pushes to `main` and manual dispatch. Feature-branch pushes use the PR run; do not launch an additional duplicate matrix. Batch related local work before submitting a new head.
- **Definition of done, for every task:**
  - Code and tests are merged, with no new analyzer warnings.
  - Logs contain no key material and no file contents.
  - `plan.md` is updated.
  - User-visible behaviour changes are reflected in the design doc or the README.

---

## 4. Configuration and secrets (decided)

The service is the only writer of machine-wide state. Everything lives under `%ProgramData%\PeerSync` behind a strict ACL, secrets are never stored in plain text, and per-user UI preferences are kept apart.

| What | Where | Format | Written by | Read by |
| --- | --- | --- | --- | --- |
| Device settings | `%ProgramData%\PeerSync\config\settings.json` | JSON with `schemaVersion` | Service only. The UI and CLI go through the control API. | Service |
| Folder list, per-folder settings, protected folder keys, known devices, pending approvals, audit log | `%ProgramData%\PeerSync\state\peersync.db` | SQLite | Service | Service |
| Per-folder index | `%ProgramData%\PeerSync\state\folders\<FolderIdHex>.db` | SQLite, WAL mode | Service | Service |
| Device certificate and private key | `LocalMachine\My`, subject `CN=PeerSync Device`. Thumbprint stored in `settings.json`. | CNG key, non-exportable | `--init` (installer, elevated) | Service |
| Logs | `%ProgramData%\PeerSync\logs\` | Serilog rolling files, 7 days, 100 MB | Service | Administrators, `PeerSync Users` |
| Per-folder metadata on disk | `<folder>\.psp\` (`id`, `ignore`, `archive\`) | Text and files | Service | Service. The ignore file is edited through the UI. |
| UI preferences | `%LocalAppData%\PeerSync\ui.json` | JSON | WPF app | WPF app |
| Rendezvous server settings | `%ProgramFiles%\PeerSync.Rendezvous\appsettings.json`, plus `appsettings.Production.json` | JSON | Administrator | Rendezvous |
| Rendezvous TLS certificate | `LocalMachine\My` | Let's Encrypt, via win-acme | win-acme | Rendezvous |

**Rules**

1. **One writer.** The UI and CLI never open these files. They call the control API, so validation lives in one place and two processes never race on one file.
2. **Atomic writes.** Write a temp file, flush it to disk, then `File.Replace` it over the old file, keeping a `.bak`. On start, a bad `settings.json` falls back to `.bak`, then to defaults. It is logged as an error and never causes a crash loop.
3. **Versioned.** `settings.json` carries `schemaVersion`, and each database uses `PRAGMA user_version`. Migrations run at start, after a backup copy.
4. **Secrets.**
   - Folder keys are stored in `peersync.db` as DPAPI blobs (machine scope plus a fixed entropy).
   - DPAPI machine scope only stops the database working on another machine. The real protection is the `state` folder ACL.
   - The device private key is a non-exportable CNG key.
   - Keys are never logged, and the control API only returns them from `GetFolderKeys`, which is audited.
5. **No registry settings.** The only registry entries are the HKLM `Run` value and Inno Setup's uninstall key.
6. **Defaults live in code** (the `DeviceSettings` record), so a missing JSON property means "use the default".
7. **No environment overrides on clients.** The rendezvous server uses standard ASP.NET Core configuration: `appsettings*.json` plus `PEERSYNC_`-prefixed environment variables.

### 4.1 Settings model

The folder settings portion is superseded by `src/PeerSync.Core/Settings/FolderSettings.cs`
with strict JSON/range/endpoint validation. Device settings and atomic file
recovery remain P2.5.4 work.

```csharp
// src/PeerSync.Core/Settings/DeviceSettings.cs
public sealed record DeviceSettings
{
    public int SchemaVersion { get; init; } = 1;
    public string DeviceName { get; init; } = Environment.MachineName;
    [Range(1024, 65535)] public int ListenPort { get; init; }          // chosen once by --init (20000-60000)
    public bool UsePortMapping { get; init; } = true;                   // UPnP / NAT-PMP
    public bool LanDiscovery { get; init; } = true;
    public string[] RendezvousServers { get; init; } = ["https://sync.taskscape.com:99"];
    public bool UseRelay { get; init; } = true;
    [Range(0, int.MaxValue)] public int UploadLimitBytesPerSec { get; init; }     // 0 = unlimited
    [Range(0, int.MaxValue)] public int DownloadLimitBytesPerSec { get; init; }   // 0 = unlimited
    public bool LimitLanTraffic { get; init; }
    public long PauseDownloadsBelowFreeBytes { get; init; } = 1L << 30;
    public bool PauseOnMeteredNetworks { get; init; }
    public int PeerExpiryDays { get; init; } = 7;
    public string DeviceCertificateThumbprint { get; init; } = "";
    public string[] ControlUserSids { get; init; } = [];               // accounts allowed on the control pipe (see 5)
}

// Stored as JSON in peersync.db (folders.settings_json), one per folder.
public sealed record FolderSettings
{
    public int RescanIntervalSeconds { get; init; } = 600;
    public bool UseLan { get; init; } = true;
    public bool UseTracker { get; init; } = true;
    public bool UseRelay { get; init; } = true;
    public string[] PredefinedHosts { get; init; } = [];               // "host:port"
    public bool RequireApproval { get; init; }
    public bool KeepBothCopies { get; init; }                          // default: latest version wins
    public bool OverwriteLocalChanges { get; init; }                   // read-only folders only
    public int ArchiveDays { get; init; } = 30;
    public long ArchiveMaxFileBytes { get; init; } = 1000L * 1024 * 1024;
    public int ArchiveMaxPercentOfVolume { get; init; } = 10;
    public TransferPriority Priority { get; init; } = TransferPriority.Default;
}
```

### 4.2 Atomic settings store

**Tricky:** `File.WriteAllText` over the live file can leave a truncated `settings.json` after a power cut, and the service then fails to start forever. Always write a temp file, flush it, then swap.

```csharp
// src/PeerSync.Service/Settings/SettingsStore.cs
public sealed class SettingsStore(string path, ILogger<SettingsStore> log)
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    readonly SemaphoreSlim gate = new(1, 1);

    public async Task SaveAsync(DeviceSettings settings, CancellationToken ct)
    {
        Validator.ValidateObject(settings, new ValidationContext(settings), validateAllProperties: true);
        await gate.WaitAsync(ct);
        try
        {
            string tmp = path + ".tmp";
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(fs, settings, Json, ct);
                fs.Flush(flushToDisk: true);
            }
            if (File.Exists(path))
                File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);  // atomic swap, keeps the ACL
            else
                File.Move(tmp, path);
        }
        finally { gate.Release(); }
    }

    /// Run before the host is built: guarantees AddJsonFile(optional: false) finds a valid file.
    public void EnsureValidOrRestore()
    {
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            try
            {
                var s = JsonSerializer.Deserialize<DeviceSettings>(File.ReadAllText(candidate), Json)!;
                Validator.ValidateObject(s, new ValidationContext(s), true);
                if (candidate != path) File.Copy(candidate, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or JsonException or ValidationException)
            {
                log.LogError(ex, "Settings file {File} unusable", candidate);
            }
        }
        throw new InvalidOperationException("No usable settings.json. Run PeerSync.Service.exe --init as administrator.");
    }
}
```

The service loads the file with `AddJsonFile(path, optional: false, reloadOnChange: true)` and binds it to `IOptionsMonitor<DeviceSettings>`, so a save is picked up live.

### 4.3 Folder-key protection

Superseded by `src/PeerSync.Crypto/FolderKeyProtector.cs`. Crypto owns the
shared helper to avoid Storage depending on Service. Fixed v1 entropy is
unchanged; a versioned binary payload binds the role and folder identity.

```csharp
// src/PeerSync.Service/Secrets/FolderKeyProtector.cs
public static class FolderKeyProtector
{
    // Changing this breaks every stored key: bump the version and migrate instead.
    static readonly byte[] Entropy = "PeerSync folder key v1"u8.ToArray();

    public static byte[] Protect(ReadOnlySpan<byte> keyBytes) =>
        ProtectedData.Protect(keyBytes.ToArray(), Entropy, DataProtectionScope.LocalMachine);

    public static byte[] Unprotect(byte[] blob) =>
        ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.LocalMachine);
}
// Callers wipe plaintext key buffers with CryptographicOperations.ZeroMemory as soon as they are done.
```

---

## 5. Permissions model (decided)

**Principals**

- `NT SERVICE\PeerSync`: the virtual account the service runs as. The installer switch `/SERVICEACCOUNT=LocalSystem` is the fallback.
- `PeerSync Users`: a local group whose members may use the control pipe. The installer creates it and adds the installing user.
- `ControlUserSids` in `settings.json`: individual accounts allowed on the pipe without signing out and back in (group membership only reaches a token at the next sign-in).
- SYSTEM and Administrators.

| Resource | SYSTEM | Administrators | `NT SERVICE\PeerSync` | `PeerSync Users` and `ControlUserSids` | Everyone else |
| --- | --- | --- | --- | --- | --- |
| `%ProgramFiles%\PeerSync` | Full | Full | Read and execute | Read and execute | Read and execute (inherited default) |
| `%ProgramData%\PeerSync\config` | Full | Full | Modify | - | - |
| `%ProgramData%\PeerSync\state` | Full | Full | Modify | - | - |
| `%ProgramData%\PeerSync\logs` | Full | Full | Modify | Read | - |
| Device private key (CNG) | Full | Full | Read | - | - |
| Control pipe `\\.\pipe\PeerSync.Control` | - | ReadWrite (elevated only) | ReadWrite, CreateNewInstance, owner | ReadWrite | - |
| Each synced folder | Unchanged | Unchanged | Modify, inherited by children | Unchanged | Unchanged |

**Rules**

1. Never run as LocalSystem by default. The service parses untrusted network input.
2. Use SIDs, never localized names: `WellKnownSidType.LocalSystemSid`, `BuiltinAdministratorsSid`, the service SID resolved from `NT SERVICE\PeerSync`, and the `PeerSync Users` SID resolved once (we created that name ourselves, so it is not localized).
3. `%ProgramData%` grants Users read by default. Our folders turn inheritance off (`SetAccessRuleProtection(true, false)`) and list their ACEs explicitly.
4. **Synced folders.**
   - The WPF app, running as the user, adds one inheritable Modify ACE for the service SID to the chosen folder. No elevation is needed when the user owns the folder.
   - Elevation is used only when that fails.
   - Windows propagates the ACE to every child, which can take minutes on large trees, so it runs in the background with progress.
   - Children with inheritance turned off are listed in the UI, never forced.
5. Files the service creates inherit the folder's ACL because the temp file is created next to the target. Existing files keep their ACL because they are swapped with `File.Replace`.
6. **Control pipe.**
   - `CurrentUserOnly = false` plus an explicit `PipeSecurity`. Only the service account gets `CreateNewInstance`.
   - The client checks that the pipe's owner is the service SID.
   - Sensitive RPCs (`GetFolderKeys`, `RemoveFolder` with data deletion, `UpdateSettings`, `AuthorizeUser`) also check the caller's identity per call and are audit-logged.
7. The WPF app offers **Authorize this account** for users who aren't allowed yet. It relaunches itself elevated with `--authorize-user <SID>`, calls `AuthorizeUser` as an administrator, and the service then restarts its pipe endpoint.

### 5.1 `--init`: data folders with explicit ACLs

```csharp
// src/PeerSync.Service/Init/SecureDirectories.cs
static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);   // not "System": that would shadow the namespace
static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);

public static void Create(string path, SecurityIdentifier service, FileSystemRights serviceRights, SecurityIdentifier? readers = null)
{
    const InheritanceFlags Inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
    var sec = new DirectorySecurity();
    sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);  // drop ProgramData's "Users: Read"
    sec.SetOwner(Admins);
    sec.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
    sec.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
    sec.AddAccessRule(new FileSystemAccessRule(service, serviceRights, Inherit, PropagationFlags.None, AccessControlType.Allow));
    if (readers is not null)
        sec.AddAccessRule(new FileSystemAccessRule(readers, FileSystemRights.ReadAndExecute, Inherit, PropagationFlags.None, AccessControlType.Allow));

    var dir = Directory.CreateDirectory(path);
    dir.SetAccessControl(sec);   // also corrects an existing folder on upgrade
}

// Usage inside InitCommand.Run():
var service = (SecurityIdentifier)new NTAccount(@"NT SERVICE\PeerSync").Translate(typeof(SecurityIdentifier)); // only resolves once `sc create` has run
Create(Paths.Config, service, FileSystemRights.Modify);
Create(Paths.State, service, FileSystemRights.Modify);
Create(Paths.Logs, service, FileSystemRights.Modify, readers: LocalGroups.PeerSyncUsersSid());
```

### 5.2 `--init`: device certificate usable only by the service

**Tricky:**

- `CertificateRequest.CreateSelfSigned` with an ephemeral key, or a PFX loaded with `EphemeralKeySet`, works in unit tests but fails when Schannel uses it for QUIC or `SslStream`: Schannel needs a persisted key.
- The service account also can't add to `LocalMachine\My`.
- So the elevated installer creates a persisted, non-exportable machine key and grants the service account read access to it, the same thing "Manage Private Keys" does in certlm.msc. `[VERIFY in P0.2.3]`

```csharp
// src/PeerSync.Service/Init/DeviceCertificate.cs
public static X509Certificate2 Ensure(SecurityIdentifier service, string? knownThumbprint)
{
    using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
    store.Open(OpenFlags.ReadWrite);
    if (!string.IsNullOrEmpty(knownThumbprint))
    {
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, knownThumbprint, validOnly: false);
        if (found.Count == 1 && found[0].HasPrivateKey) return found[0];
    }

    var keyParams = new CngKeyCreationParameters
    {
        Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
        KeyCreationOptions = CngKeyCreationOptions.MachineKey,   // machine key store, not the installer's profile
        ExportPolicy = CngExportPolicies.None,                    // the private key never leaves this machine
        KeyUsage = CngKeyUsages.Signing,
    };
    using var cngKey = CngKey.Create(CngAlgorithm.ECDsaP256, "PeerSync-Device-" + Guid.NewGuid().ToString("N"), keyParams);
    GrantKeyRead(cngKey, service);

    using var ecdsa = new ECDsaCng(cngKey);
    var req = new CertificateRequest("CN=PeerSync Device", ecdsa, HashAlgorithmName.SHA256);
    req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
    req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
        new OidCollection { new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2") }, critical: false)); // server + client auth
    var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
    cert.FriendlyName = "PeerSync Device";
    store.Add(cert);   // the certificate references the persisted named key [VERIFY]
    return cert;
}

static void GrantKeyRead(CngKey key, SecurityIdentifier sid)
{
    const string SecurityDescr = "Security Descr";               // NCRYPT_SECURITY_DESCR_PROPERTY
    const CngPropertyOptions Dacl = (CngPropertyOptions)0x4;     // DACL_SECURITY_INFORMATION
    byte[] raw = key.GetProperty(SecurityDescr, Dacl).GetValue()!;
    var sd = new CommonSecurityDescriptor(isContainer: false, isDS: false, raw, 0);
    sd.DiscretionaryAcl.AddAccess(AccessControlType.Allow, sid, unchecked((int)0x80000000) /* GENERIC_READ */,
        InheritanceFlags.None, PropagationFlags.None);
    byte[] updated = new byte[sd.BinaryLength];
    sd.GetBinaryForm(updated, 0);
    key.SetProperty(new CngProperty(SecurityDescr, updated, CngPropertyOptions.Persist | Dacl));
}
```

### 5.3 Granting the service access to a synced folder (WPF app)

**Tricky:**

- `SetAccessControl` on a directory propagates the new inheritable ACE to every descendant, which can take minutes on a million files, so never call it on the UI thread.
- Check for an existing ACE first, so re-adding a folder doesn't stack duplicates.

```csharp
// src/PeerSync.App/Security/FolderAccess.cs
public enum GrantResult { AlreadyGranted, Granted, NeedsElevation }

public static Task<GrantResult> GrantServiceAccessAsync(string folder, SecurityIdentifier service) => Task.Run(() =>
{
    const FileSystemRights Rights = FileSystemRights.Modify | FileSystemRights.Synchronize;
    const InheritanceFlags Inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
    var dir = new DirectoryInfo(folder);
    DirectorySecurity sec = dir.GetAccessControl(AccessControlSections.Access);

    bool present = sec.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
        .OfType<FileSystemAccessRule>()
        .Any(r => r.IdentityReference.Equals(service) && r.AccessControlType == AccessControlType.Allow
                  && (r.FileSystemRights & Rights) == Rights && r.InheritanceFlags == Inherit);
    if (present) return GrantResult.AlreadyGranted;

    sec.AddAccessRule(new FileSystemAccessRule(service, Rights, Inherit, PropagationFlags.None, AccessControlType.Allow));
    try { dir.SetAccessControl(sec); return GrantResult.Granted; }      // the owner has WRITE_DAC: no elevation
    catch (UnauthorizedAccessException) { return GrantResult.NeedsElevation; }
});

// NeedsElevation: run the same code elevated, then return to the normal flow.
public static async Task<bool> GrantElevatedAsync(string folder)
{
    var psi = new ProcessStartInfo(Environment.ProcessPath!, $"--grant-access \"{folder}\"") { UseShellExecute = true, Verb = "runas" };
    try { using var p = Process.Start(psi)!; await p.WaitForExitAsync(); return p.ExitCode == 0; }
    catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return false; }   // the user cancelled the UAC prompt
}
```

After granting, `AddFolder` asks the service to create `.psp\id` in the folder. That proves the service really has access before the folder is saved.

### 5.4 Control pipe: server ACL, client owner check, per-call identity

**Tricky:**

- `CurrentUserOnly` defaults to `true` and must be `false` when you supply `PipeSecurity`, otherwise Kestrel throws.
- Never give callers `CreateNewInstance` (or `FullControl`, which includes it). A process holding it can create a rogue pipe instance and impersonate the service.

```csharp
// src/PeerSync.Service/Control/ControlPipe.cs
builder.WebHost.UseNamedPipes(o =>
{
    o.CurrentUserOnly = false;
    var self = WindowsIdentity.GetCurrent().User!;                     // NT SERVICE\PeerSync when running as the service
    var ps = new PipeSecurity();
    ps.SetOwner(self);
    ps.AddAccessRule(new PipeAccessRule(self, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
    ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
    ps.AddAccessRule(new PipeAccessRule(LocalGroups.PeerSyncUsersSid(), PipeAccessRights.ReadWrite, AccessControlType.Allow));
    foreach (var sid in settings.ControlUserSids)
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid), PipeAccessRights.ReadWrite, AccessControlType.Allow));
    o.PipeSecurity = ps;
});
builder.WebHost.ConfigureKestrel(k => k.ListenNamedPipe("PeerSync.Control", l => l.Protocols = HttpProtocols.Http2));
```

```csharp
// src/PeerSync.Control.Contracts/ControlClient.cs (used by the WPF app and psp.exe)
static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext _, CancellationToken ct)
{
    var pipe = new NamedPipeClientStream(".", "PeerSync.Control", PipeDirection.InOut,
        PipeOptions.Asynchronous | PipeOptions.WriteThrough,
        TokenImpersonationLevel.Identification);       // lets the service read our identity, never act as us
    try
    {
        await pipe.ConnectAsync(TimeSpan.FromSeconds(2), ct);
        var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
        if (!ServiceSid.PeerSync.Equals(owner))
            throw new SecurityException("The control pipe is not owned by the PeerSync service.");
        return pipe;
    }
    catch { await pipe.DisposeAsync(); throw; }
}

public static GrpcChannel CreateChannel() => GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
{
    HttpHandler = new SocketsHttpHandler { ConnectCallback = ConnectAsync },
});
```

```csharp
// src/PeerSync.Service/Control/CallerIdentityInterceptor.cs
public sealed class CallerIdentityInterceptor(IAuthorizedUsers authorized, IAuditLog audit) : Interceptor
{
    static readonly HashSet<string> Sensitive =
    [
        "/peersync.control.Control/GetFolderKeys", "/peersync.control.Control/RemoveFolder",
        "/peersync.control.Control/UpdateSettings", "/peersync.control.Control/AuthorizeUser",
    ];

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        var pipe = context.GetHttpContext().Features.Get<IConnectionNamedPipeFeature>()?.NamedPipe
                   ?? throw new RpcException(new Status(StatusCode.PermissionDenied, "Named pipe only."));
        SecurityIdentifier? caller = null;
        pipe.RunAsClient(() => caller = WindowsIdentity.GetCurrent(ifImpersonating: true)?.User);  // [VERIFY in P0.2.5]
        if (Sensitive.Contains(context.Method) && (caller is null || !authorized.IsAllowed(caller)))
            throw new RpcException(new Status(StatusCode.PermissionDenied, "This account is not allowed to do that."));
        audit.Record(caller, context.Method);
        return await continuation(request, context);
    }
}
```

---

## 6. Phase 0: Foundations and spikes (weeks 1-3)

Goal: a building, testing repo, plus answers to every question that could change the architecture. The riskiest is QUIC hole punching.

### P0.1 Repository and build

- [x] **P0.1.1 Create the repo and solution** (repo root) (#1)
  - Done when: all 11 projects and the test projects from 3.1 exist, `Directory.Build.props` and `Directory.Packages.props` are in place, and `dotnet build` passes with warnings as errors.
  - Ref: Solution structure and tech stack.
- [x] **P0.1.2 CI pipeline** (`.github/workflows/ci.yml`) (#1, #4)
  - Done when: every main push and PR update builds x64 and Arm64, runs tests, and uploads published artifacts. Runtime is under 15 minutes. Feature-branch push duplication is excluded by the documented CI decision.
  - Repaired and merged in (#4): the unchanged required 600-second fuzz smoke runs in its own parallel job, with no check removed. [Green CI](https://github.com/taskscape/PeerSync/actions/runs/39144766855) completed in 11m32s, including uploads/cleanup; both architectures and Server variants passed.
  - [ ] **P0.1.2d Remove third duplicate native Engine CI execution** (`build/Verify.ps1`, `.github/workflows/ci.yml`) [WIP @codex 2026-10-19]
    - Done when: both required Server jobs still run every Engine case, x64 runs the other suites/simulation/probes/signing/publication, explicit partition evidence distinguishes it from default full local verification, and all five final-head checks pass within 15 minutes. Keep full boundary tests, durability and fuzz duration.
    - Local [partition and capacity verification](docs/verification/P1-case-capacity-ci-partition-2026-10-19.md): partition checks pass in 110.89 seconds; default full verification passes 1,134 cases/native/probe/publishing in 129.67 seconds, exit 0 (local boundary fuzz skipped). Final-head CI/main remain pending.
  - [ ] **P0.1.2e Overlap independent Server native/security checks** (`build/Verify-ServiceSecurity.ps1`, `.github/workflows/ci.yml`) [WIP @codex 2026-10-19]
    - Done when: complete no-build Engine tests overlap independent P0/cross-account probes after one immutable build; both outcomes/logs/TRX are retained, waits/child cleanup are bounded, full local and all final-head/main jobs pass within 15 minutes. No checks or durability guarantees are removed.
    - Local [review verification](docs/verification/P1-control-retention-review-2026-10-19.md): final parallel run passes 720 Engine cases and real virtual-account/private-key/standard-user probes in 94.99 seconds after build, exit 0; full canonical verification passes 1,176 cases in 167.36 seconds. Final-head CI/main remain pending; the cancelled old main run is not passing evidence.
  - [ ] **P0.1.2f Actual control-probe readiness under load** (`build/Run-P0.ps1`, `spikes/ControlPipe/`) [WIP @codex 2026-10-19]
    - Done when: a delayed real pipe server reproduces the old early-client failure, fresh PID-bound readiness precedes the good client, all owner/caller/negative checks and original deadlines remain, sanitized RPC causes are retained, and full local/CI/main checks pass.
    - Local [readiness/CI verification](docs/verification/P0-control-readiness-ci-2026-10-19.md): delayed real server reproduces early timeout; wrong-PID readiness is ignored and actual owner/caller RPC passes. All 1,176 canonical cases plus full watcher/native/probe/publishing pass in 167.41 seconds, exit 0; final parallel Server run passes 720 Engine and all non-watcher/cross-account probes in 38.39 seconds after build. CI/main remain pending; deadlines and required x64 watcher coverage are unchanged.
  - [x] **P0.1.2c Atomic fresh database initialization to reduce CI I/O** (`PeerSync.Storage/Db.cs`) (#32)
    - Done when: a CreateNew reservation plus an immediate-transaction empty-schema/version check permits atomic initial DDL without intermediate backups; existing-file upgrades retain every backup; failed/cancelled initial DDL exposes no partial schema; migration regressions and canonical local checks pass, and all required CI jobs finish under 15 minutes.
    - Local [verification](docs/verification/P0.1.2c-fresh-database-2026-10-19.md): 16 focused Db cases and all 834 canonical tests/native/probe/publishing checks pass in 108.98 seconds, exit 0 (separate CI fuzz explicitly skipped). Thirty fresh databases use zero intermediate backups; existing-file initialization retains all 540. Hosted results follow below.
    - Completed with [green CI/merge audit](docs/verification/P1-retention-ci-merge-2026-10-19.md): all five jobs pass below 15 minutes; x64 finishes in 10m39s and Server 2025 in 10m05s. Head/tested/prospective/actual trees match. The separate main workflow is follow-up evidence.
  - [x] **P0.1.2a Avoid duplicate feature-branch CI** (`.github/workflows/ci.yml`) (#30)
    - Done when: only main pushes, PR updates and manual dispatch start the full unchanged five-job workflow; a submitted feature head starts one PR run, with all checks green and merge audited. Record billing attribution and preserve evidence.
    - [Green CI and merge audit](docs/verification/P1-catalog-references-ci-merge-2026-10-19.md): exactly one PR workflow for the submitted head, all five jobs pass, and head/tested/prospective/actual trees match. See [billing attribution](docs/verification/CI-costs-2026-10-19.md).
  - [x] **P0.1.2b Avoid repeated ReadyToRun compilation of P0 probes** (`build/Publish.ps1`) (#30)
    - Done when: all six isolated x64/ARM64 diagnostic probes publish self-contained with locked restore and ordinary JIT assemblies; published x64 QUIC transfer and signing still work, required CI passes under 15 minutes, and production installer ReadyToRun remains documented separately.
    - Implemented with [verification](docs/verification/P0.1.2b-probe-publication-2026-10-19.md): all 633 canonical x64 tests/probes pass in 102.01 s, ARM64 publication passes, all 12 assemblies pass architecture/JIT-header inspection, and published x64 16 MiB QUIC transfer and existing-certificate timestamped signing pass. Merged in (#30): all five required jobs pass in under 15 minutes; tested/prospective/actual trees match.
  - [x] **P0.1.2d QUIC initial progress independent of stream setup duration** (`spikes/QuicBasics/Program.cs`, `build/Test-QuicDiagnostics.ps1`) (#33)
    - Done when: the first completed data unit is logged immediately with positive bytes, later samples retain the five-second cadence, the six-second deadline and delayed-start regression remain, and local/probe/CI checks pass with merge evidence.
    - Local [regression evidence](docs/verification/P0.1.2d-initial-quic-progress-2026-10-19.md): saved earlier probe fails the strengthened first-unit check; repaired probe and all 882 canonical cases/native/probe/publishing checks pass. The causal main-run diagnostic failure is covered by the completed repair/merge audit below.
    - Completed with [green CI/main merge audit](docs/verification/P1-pull-coordinator-ci-merge-2026-10-19.md): the unchanged deadline smoke and all five jobs pass at the final head, and all tested/main trees match. Automatic main follow-up is separately tracked.
  - [ ] **P0.1.2e Scope diagnostic evidence across PID reuse** (`build/Test-QuicDiagnostics.ps1`) [WIP @codex 2026-10-19]
    - Done when: a mixed-run fixture cannot poison or satisfy current-invocation assertions; select by process lifetime, role and run ID, retain all prior logs and all timeout/progress checks, and pass focused/canonical/CI validation with merge evidence.
    - Local [evidence](docs/verification/P0.1.2e-quic-pid-evidence-2026-10-19.md): saved real PID-reuse logs reproduce the assertion failure; scoped replay and typed-date/synthetic regressions pass, with the unchanged runtime smoke and all 1,003 canonical cases/native/probe/publishing in 120.87 seconds. CI/main remain pending.
- [x] **P0.1.3 Code-signing setup** (`build/sign.ps1`) (#5)
  - Done when: a test EXE is signed in CI, or the task is marked BLOCKED until a certificate exists (open question in the design doc).
  - Local update (2026-10-18): user-created development certificate signed the self-contained QuicBasics EXE with a SHA-256 RFC3161 timestamp. SignTool verification and PowerShell Authenticode status passed. SDK tool discovery is repaired locally; CI signing is still pending, so this task remains unchecked. Publicly trusted release signing remains separate.
  - 2026-10-19 implementation: `build/Test-Signing.ps1` and both CI build jobs now sign and verify a separate test EXE using runner-generated credentials, reject PE tampering and remove test trust/key material. Local x64 smoke passed; remote CI and merge are pending, so the task is not yet ticked.
  - Completed: merged in (#5). [Green PR CI](https://github.com/taskscape/PeerSync/actions/runs/39179220764) passed all five jobs in 13m44s; both architecture signing artifacts were downloaded and their signer/timestamp/hash matched the evidence. Tamper rejection and temporary key/trust cleanup passed. The merged tree exactly matches the tested PR head. This closes development signing-path verification, not publicly trusted release signing.
- [x] **P0.1.4 Reference hardware recorded** (`docs/reference-hardware.md`) (#1)
  - Done when: the CPU, disk and network of the two to three test PCs are written down, so all benchmarks are comparable.
  - Recorded: development PC plus version-stamped Server 2022/2025 hosted VM snapshots; VM adapter link rates are not measured LAN throughput. Merged in (#1); green CI is recorded in docs/verification/P0-2026-10-18.md.

New P0 foundation requirement from the user (2026-10-18):

- [x] **P0.1.5 Deployment diagnostic foundation** (`PeerSync.Core/Diagnostics/`, `build/Collect-Diagnostics.ps1`, `docs/diagnostics.md`) (#1)
  - Done when: structured opt-in debug events have shared session IDs, UTC/run/process metadata, transport/timings/errors; log size and address retention are bounded; collection excludes settings, databases, keys and dumps; rotation, filtering and correlation are verified; server and second-PC probe instructions exist.
  - Follow-up repair merged in (#5), [CI green](https://github.com/taskscape/PeerSync/actions/runs/39179220764): detailed QUIC failure stages/codes/progress, independent transfer timeout, and collector rejection of empty debug bundles. See `docs/verification/P0-two-machine-troubleshooting-2026-10-18.md`; this does not close P0.2.1 or P0.2.2.
  - Production service hooks are tracked by P2.5.6 and central hooks by P4.1.7.

### P0.2 Spikes

Each spike ends with a write-up in `docs/spikes/` and a decision row in section 2 when it changes the design.

- [ ] **P0.2.1 QUIC between two devices with self-signed certificates** (`spikes/QuicBasics`) [DEFERRED validation to Gate P2: user approved 2026-10-19]
  - Done when: two processes exchange 1 GB over one QUIC connection, ALPN `psp/1`, with both certificates required and the DeviceId read from `RemoteCertificate` on both sides. Throughput on 1 Gbit LAN is recorded.
  - 2026-10-19: two physical PCs on different LANs passed 1 GiB over Tailscale in 220.78 s at 4.64 MiB/s. Receiver journal confirms matching session and bytes. [Evidence](docs/verification/P0-cross-network-2026-10-19.md). This is overlay throughput; the physical LAN criterion remains open.
  - Code: see 6.1.
- [ ] **P0.2.2 QUIC hole punching: dial out from the listener's UDP port** (`spikes/QuicPunch`) [DEFERRED validation to Gate P4: user approved 2026-10-19]
  - Question: can a `QuicConnection` use `LocalEndPoint` = the `QuicListener`'s port, so both directions share one NAT mapping?
  - Try, in order:
    - (a) `System.Net.Quic` with `LocalEndPoint` set
    - (b) MsQuic called directly with a shared UDP binding
    - (c) TCP simultaneous open on the listening port with `SO_REUSEADDR`
  - Test matrix: same LAN; two home NATs (full cone and port-restricted); one side behind a mobile hotspot (CGNAT).
  - Done when: the write-up states which option works in which NAT case, and the decision is logged. If none works, P4 relies on the relay for hard NATs and the design doc's risk row is updated.
  - Ref: Protocol specification, NAT traversal.
- [x] **P0.2.3 Device certificate usable by the virtual service account** (`spikes/DeviceCert`) (#1)
  - Done when: 5.2 runs elevated, then a test service running as `NT SERVICE\PeerSyncSpike` serves QUIC and `SslStream` with that certificate. A standard user can't read the key.
- [x] **P0.2.4 Chunking and hashing throughput** (`tests/PeerSync.Benchmarks`) (#1)
  - Done when: FastCDC plus BLAKE3 on a 10 GB file is at least 1 GB/s on the reference NVMe, or the target is revised and logged. Boundary stability is checked: identical cut points whatever the read sizes.
- [x] **P0.2.5 Control pipe across accounts** (`spikes/ControlPipe`) (#1)
  - Done when: the service (virtual account) and the app (standard user) talk gRPC over the pipe. A non-member is refused. The owner check in 5.4 passes. `RunAsClient` returns the caller's SID.
- [x] **P0.2.6 Kestrel on port 99 with HTTP/2, HTTP/3 and client certificates** (`spikes/Rendezvous99`, on a Windows Server 2025 VM) (#1)
  - Done when: a gRPC bidirectional stream works over HTTP/2 (TCP 99) and HTTP/3 (UDP 99), with the client's self-signed certificate visible on the server for both.
  - If HTTP/3 can't carry client certificates, the fallback is logged: device proof inside the session stream (sign the server nonce with the device key).
  - Local and GitHub-hosted `windows-2025`/`windows-2022` VM probes passed. Version-stamped green CI is recorded in docs/verification/P0-2026-10-18.md. On 2026-10-19, the user-deployed public Server probe also passed exact HTTP/2 and HTTP/3, mutual TLS, device identity and three bidirectional messages; see [external evidence](docs/verification/P0-cross-network-2026-10-19.md). Production P4 deployment remains separate.
- [x] **P0.2.7 FileSystemWatcher under stress** (`spikes/WatcherStress`) (#1)
  - Done when: a burst of 100,000 creates and renames shows whether events are lost, the buffer-overflow behaviour is documented, and the rescan fallback is confirmed.

### 6.1 Code: QUIC listener and dialler (P0.2.1, reused in P2.2.1)

**Tricky:**

- `RemoteCertificateValidationCallback` must accept self-signed certificates: identity is checked later by DeviceId pinning and the folder proof, not by a CA.
- The server must set `ClientCertificateRequired = true`, or `RemoteCertificate` will be null.
- Read the DeviceId from the connection, never from a message.

```csharp
// src/PeerSync.Net/Quic/QuicTransport.cs
public static readonly SslApplicationProtocol Alpn = new("psp/1"u8.ToArray());

public static async Task<QuicListener> ListenAsync(int port, X509Certificate2 deviceCert, CancellationToken ct)
{
    if (!QuicListener.IsSupported) throw new PlatformNotSupportedException("QUIC requires Windows 11 or Server 2022+.");
    var serverOptions = new QuicServerConnectionOptions
    {
        DefaultStreamErrorCode = ErrorCodes.StreamAborted,
        DefaultCloseErrorCode = ErrorCodes.ConnectionClosed,
        MaxInboundBidirectionalStreams = 256,
        MaxInboundUnidirectionalStreams = 0,
        IdleTimeout = TimeSpan.FromSeconds(90),
        ServerAuthenticationOptions = new SslServerAuthenticationOptions
        {
            ApplicationProtocols = [Alpn],
            ServerCertificate = deviceCert,
            ClientCertificateRequired = true,
            RemoteCertificateValidationCallback = (_, cert, _, _) => cert is not null,   // self-signed by design
        },
    };
    return await QuicListener.ListenAsync(new QuicListenerOptions
    {
        ListenEndPoint = new IPEndPoint(IPAddress.IPv6Any, port),        // dual-stack [VERIFY]
        ApplicationProtocols = [Alpn],
        ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(serverOptions),
    }, ct);
}

public static ValueTask<QuicConnection> DialAsync(IPEndPoint remote, X509Certificate2 deviceCert, CancellationToken ct) =>
    QuicConnection.ConnectAsync(new QuicClientConnectionOptions
    {
        RemoteEndPoint = remote,
        DefaultStreamErrorCode = ErrorCodes.StreamAborted,
        DefaultCloseErrorCode = ErrorCodes.ConnectionClosed,
        IdleTimeout = TimeSpan.FromSeconds(90),
        ClientAuthenticationOptions = new SslClientAuthenticationOptions
        {
            ApplicationProtocols = [Alpn],
            TargetHost = "peersync",                               // SNI is ignored by peers
            ClientCertificates = new X509CertificateCollection { deviceCert },
            RemoteCertificateValidationCallback = (_, cert, _, _) => cert is not null,
        },
    }, ct);

// src/PeerSync.Core/Identity/DeviceId.cs
public static byte[] FromCertificate(X509Certificate cert)
{
    using var c2 = X509CertificateLoader.LoadCertificate(cert.GetRawCertData());
    return SHA256.HashData(c2.PublicKey.ExportSubjectPublicKeyInfo());
}
```

### Gate P0

- [x] All P0 tasks ticked or explicitly dropped, except the user-approved P0.2.1 validation deferral to Gate P2 and P0.2.2 validation deferral to Gate P4. Both remain unchecked and mandatory at those gates. Evidence: (#5), [green CI](https://github.com/taskscape/PeerSync/actions/runs/39179220764), and the approved phasing decision in section 2.
- [x] The direct QUIC/TCP plus relay-after-5-s implementation baseline is logged in section 2 and P4.2.3 matches. P0.2.2's final transport decision remains mandatory at Gate P4 under the user-approved deferral. Source design and checklist are merged in (#5).
- [x] Performance targets confirmed or revised (P0.2.4), and the design doc's Testing section updated. Evidence: [spike report](docs/spikes/P0.2.4-chunking-hashing.md), merged in (#1).
- [x] The device-certificate and control-pipe approaches work under the virtual account (P0.2.3, P0.2.5). Evidence: [green Server 2022/2025 CI](https://github.com/taskscape/PeerSync/actions/runs/39141978283), merged in (#1).

---

## 7. Phase 1: Local sync engine (weeks 4-8)

Goal: the whole sync engine, proven with simulated nodes in one process. No real network yet.

### P1.1 Core types

- [x] **P1.1.1 Folder keys and their text form** (`PeerSync.Crypto/FolderKeys.cs`, `PeerSync.Core/Keys/KeyText.cs`) (#6)
  - Done when: RW, RO and F keys round-trip through text. The checksum rejects any single-character typo. RO and F keys derived from an RW key match fixed test vectors. Keys are never printed by `ToString()`.
  - Ref: Protocol specification, Keys and identifiers.
  - Evidence: [key verification](docs/verification/P1.1.1-folder-keys-2026-10-19.md), [green five-job CI](https://github.com/taskscape/PeerSync/actions/runs/39181190139). Merged tree equals the tested head.
- [x] **P1.1.2 Version vector** (`PeerSync.Core/Sync/VersionVector.cs`) (#7)
  - Done when: compare, merge and increment are covered by property tests (compare is antisymmetric, merge is commutative and idempotent). Compact binary serialization is sorted by device.
- [x] **P1.1.3 FileRecord and signed envelope** (`PeerSync.Protocol/records.proto`, `PeerSync.Core/Sync/SignedRecord.cs`) (#7)
  - Done when: records are signed over the exact body bytes (see 7.3). Tampering with any byte fails verification. A forwarded record keeps its original signature.
- [x] **P1.1.4 Path rules** (`PeerSync.Core/Paths/PathRules.cs`) (#7)
  - Done when: all hostile cases in 7.4 are rejected, valid Unicode names pass, and a fuzz target exists.
  - Ref: Security model, hostile paths.
- [x] **P1.1.5 Ignore matcher** (`PeerSync.Core/Ignore/IgnoreMatcher.cs`) (#7)
  - Done when: it supports gitignore-style `*`, `?`, `**`, `#` comments, a leading `/` to anchor at the root, a trailing `/` for directories only, and case-insensitive matching. Default rules are included. Resilio's IgnoreList examples pass as tests.
  - P1.1.2–P1.1.5 evidence: [vectors](docs/verification/P1.1.2-version-vectors-2026-10-19.md), [records/paths](docs/verification/P1.1.3-4-records-paths-2026-10-19.md), [ignore rules](docs/verification/P1.1.5-ignore-rules-2026-10-19.md), [green five-job CI](https://github.com/taskscape/PeerSync/actions/runs/39182383820). Merged tree equals the tested head.

### 7.1 Code: key chain (P1.1.1)

The sketch below is superseded by `src/PeerSync.Crypto/FolderKeys.cs` and
`src/PeerSync.Core/Keys/KeyText.cs`: the implementation owns and clears secrets,
caches the signing key until disposal, returns defensive copies, and provides
explicit role-limited text exports. Public synthetic vectors live in
`tests/Fixtures/folder-key-vectors.json`.

```csharp
// src/PeerSync.Crypto/FolderKeys.cs
public sealed class FolderKeys
{
    public byte[]? ReadWriteSecret { get; }   // S_rw, 32 bytes. Null on RO and F peers.
    public byte[] PublicKey { get; }          // pk, 32 bytes. Every peer has it.
    public byte[]? ContentKey { get; }        // K_content, 32 bytes. Null on F peers.

    public static FolderKeys CreateNew() => FromReadWrite(RandomNumberGenerator.GetBytes(32));

    public static FolderKeys FromReadWrite(byte[] sRw)
    {
        byte[] seed = HKDF.DeriveKey(HashAlgorithmName.SHA256, sRw, 32, info: "psp1 sign"u8.ToArray());
        using var signing = Key.Import(SignatureAlgorithm.Ed25519, seed, KeyBlobFormat.RawPrivateKey);   // NSec
        byte[] pk = signing.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        byte[] kc = HKDF.DeriveKey(HashAlgorithmName.SHA256, sRw, 32, info: "psp1 content"u8.ToArray());
        CryptographicOperations.ZeroMemory(seed);
        return new FolderKeys(sRw, pk, kc);
    }

    public byte[] AuthKey => HKDF.DeriveKey(HashAlgorithmName.SHA256, PublicKey, 32, info: "psp1 auth"u8.ToArray());
    public byte[]? ReadOnlyAuthKey => ContentKey is null ? null
        : HKDF.DeriveKey(HashAlgorithmName.SHA256, ContentKey, 32, info: "psp1 ro-auth"u8.ToArray());

    public byte[] FolderId()
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData("psp1 folder"u8);
        h.AppendData(PublicKey);
        return h.GetHashAndReset();
    }

    // Only RW peers can sign. Ed25519 is deterministic: two RW peers signing the same body produce the same bytes.
    public byte[] Sign(ReadOnlySpan<byte> message)
    {
        if (ReadWriteSecret is null) throw new InvalidOperationException("Read-only peers cannot sign.");
        byte[] seed = HKDF.DeriveKey(HashAlgorithmName.SHA256, ReadWriteSecret, 32, info: "psp1 sign"u8.ToArray());
        try
        {
            using var key = Key.Import(SignatureAlgorithm.Ed25519, seed, KeyBlobFormat.RawPrivateKey);
            return SignatureAlgorithm.Ed25519.Sign(key, message);
        }
        finally { CryptographicOperations.ZeroMemory(seed); }
    }

    public bool Verify(ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature) =>
        SignatureAlgorithm.Ed25519.Verify(
            NSec.Cryptography.PublicKey.Import(SignatureAlgorithm.Ed25519, PublicKey, KeyBlobFormat.RawPublicKey),
            message, signature);

    private FolderKeys(byte[]? rw, byte[] pk, byte[]? kc) { ReadWriteSecret = rw; PublicKey = pk; ContentKey = kc; }
}
```

Key text: prefix letter (`A` RW, `B` RO, `F` encrypted), then RFC 4648 Base32 without padding, then a 2-character CRC-10/ATM checksum over canonical uppercase ASCII prefix followed by raw payload (poly 0x233, init/xorout 0, no reflection; high five bits then low five bits). Reject nonzero unused padding bits. Decode ASCII letters case-insensitively and ignore whitespace/dashes so keys survive copy-paste. This corrects the draft's truncated-SHA checksum, which could not guarantee P1.1.1's single-character typo rejection.

### 7.2 Code: version vector (P1.1.2)

Superseded by `src/PeerSync.Core/Sync/VersionVector.cs`, which additionally
normalizes zero entries, checks increment overflow, and implements strict,
canonical compact serialization (count + sorted fixed64-LE/ULEB128 entries).

```csharp
// src/PeerSync.Core/Sync/VersionVector.cs
public enum VvOrder { Equal, Dominates, DominatedBy, Concurrent }

public static class VersionVector
{
    // Keys are short device ids (first 8 bytes of DeviceId). Missing key = counter 0.
    public static VvOrder Compare(IReadOnlyDictionary<ulong, ulong> a, IReadOnlyDictionary<ulong, ulong> b)
    {
        bool aAhead = false, bAhead = false;
        foreach (var (dev, ca) in a)
        {
            ulong cb = b.GetValueOrDefault(dev);
            if (ca > cb) aAhead = true; else if (ca < cb) bAhead = true;
        }
        foreach (var (dev, cb) in b)
            if (cb > 0 && !a.ContainsKey(dev)) bAhead = true;
        return (aAhead, bAhead) switch
        {
            (false, false) => VvOrder.Equal,
            (true, false) => VvOrder.Dominates,
            (false, true) => VvOrder.DominatedBy,
            _ => VvOrder.Concurrent,
        };
    }

    public static Dictionary<ulong, ulong> Merge(IReadOnlyDictionary<ulong, ulong> a, IReadOnlyDictionary<ulong, ulong> b)
    {
        var m = new Dictionary<ulong, ulong>(a);
        foreach (var (dev, c) in b) m[dev] = Math.Max(m.GetValueOrDefault(dev), c);
        return m;
    }

    public static Dictionary<ulong, ulong> Increment(IReadOnlyDictionary<ulong, ulong> v, ulong self) =>
        new(v) { [self] = v.GetValueOrDefault(self) + 1 };
}
```

### 7.3 Code: signed record envelope (P1.1.3)

Superseded by `src/PeerSync.Core/Sync/SignedRecord.cs` and
`src/PeerSync.Protocol/Signing/SignedRecordFactory.cs`. The factory lives in
Protocol so Core stays independent of Crypto and generated Protobuf types.

**Tricky:** never verify or forward a record by re-serializing the parsed message. Protobuf doesn't guarantee byte-identical output (unknown fields, map ordering, library versions). Sign the body bytes once, then store and forward those exact bytes.

```protobuf
// src/PeerSync.Protocol/records.proto
message SignedRecord {
  bytes  body      = 1;  // serialized FileRecordBody, kept verbatim forever
  bytes  signature = 2;  // Ed25519 over "psp1 record" || FolderId || body
  uint64 local_seq = 3;  // NOT signed: each forwarding peer rewrites it
}

message FileRecordBody {
  string path          = 1;   // relative, '/'-separated, NFC
  uint32 type          = 2;   // 0 file, 1 directory, 2 symlink
  uint64 size          = 3;
  int64  mtime_ticks   = 4;   // 100 ns ticks, UTC
  uint32 attributes    = 5;   // FileAttributes subset: ReadOnly, Hidden
  bool   deleted       = 6;
  int64  changed_ticks = 7;   // mtime for files, detection time for deletes. "Latest wins" uses this.
  repeated VvEntry vv  = 8;   // sorted by device
  uint32 chunk_class   = 9;   // FastCDC size class, see 7.5
  repeated Chunk chunks = 10;
  bytes  file_hash     = 11;  // BLAKE3 of the whole file
  bytes  modified_by   = 12;  // DeviceId of the device that made this version
}
message VvEntry { fixed64 device = 1; uint64 counter = 2; }
message Chunk   { uint32 length = 1; bytes hash = 2; }
```

```csharp
// src/PeerSync.Core/Sync/SignedRecordFactory.cs
public static SignedRecord Create(FileRecordBody body, FolderKeys keys)
{
    byte[] bodyBytes = body.ToByteArray();                         // serialize ONCE
    return new SignedRecord
    {
        Body = ByteString.CopyFrom(bodyBytes),
        Signature = ByteString.CopyFrom(keys.Sign(SigningInput(keys.FolderId(), bodyBytes))),
    };
}

public static bool TryOpen(SignedRecord rec, FolderKeys keys, out FileRecordBody body)
{
    body = null!;
    ReadOnlySpan<byte> bodyBytes = rec.Body.Span;
    if (!keys.Verify(SigningInput(keys.FolderId(), bodyBytes), rec.Signature.Span)) return false;
    body = FileRecordBody.Parser.ParseFrom(bodyBytes);
    return PathRules.IsValid(body.Path);                            // a signed record can still carry a hostile path
}

static byte[] SigningInput(ReadOnlySpan<byte> folderId, ReadOnlySpan<byte> body) =>
    [.. "psp1 record"u8, .. folderId, .. body];                    // domain separation: no cross-folder replay
```

### 7.4 Code: path rules (P1.1.4)

Superseded by `src/PeerSync.Core/Paths/PathRules.cs`, which validates before
mapping a record to an absolute folder root and rejects malformed UTF-16.
This is a lexical guard; filesystem code also checks reparse points.

```csharp
// src/PeerSync.Core/Paths/PathRules.cs
public static class PathRules
{
    static readonly SearchValues<char> Forbidden = SearchValues.Create("<>:\"|?*\\");
    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    public static bool IsValid(string recordPath)
    {
        if (recordPath.Length is 0 or > 4096) return false;
        if (!recordPath.IsNormalized(NormalizationForm.FormC)) return false;
        var parts = recordPath.Split('/');
        for (int i = 0; i < parts.Length; i++)
        {
            string p = parts[i];
            if (p is "" or "." or "..") return false;                      // also rejects leading, trailing and double '/'
            if (p.Length > 255) return false;
            if (p.AsSpan().ContainsAny(Forbidden)) return false;           // ':' blocks alternate data streams
            foreach (char c in p) if (c < 32) return false;
            if (p.EndsWith('.') || p.EndsWith(' ')) return false;          // Windows silently strips these
            string stem = p.Split('.')[0].TrimEnd(' ');
            if (Reserved.Contains(stem)) return false;                     // "CON.txt" is reserved too
            if (i == 0 && p.Equals(".psp", StringComparison.OrdinalIgnoreCase)) return false;
            if (p.EndsWith(".!psp", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    /// Final guard before ANY file system call made on behalf of a remote record.
    public static string ToLocal(string folderRoot, string recordPath)
    {
        string root = Path.GetFullPath(folderRoot).TrimEnd('\\') + "\\";
        string full = Path.GetFullPath(Path.Combine(root, recordPath.Replace('/', '\\')));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new SecurityException($"Path escapes the folder root: {recordPath}");
        return full;
    }

    /// Database key: NTFS compares names case-insensitively, but SQLite NOCASE only folds ASCII.
    public static string Key(string recordPath) => recordPath.ToUpperInvariant();
}
```

### P1.2 Chunking and hashing

- [x] **P1.2.1 Gear table and FastCDC chunker** (`PeerSync.Crypto/Chunking/`) (#8)
  - Done when: boundaries match golden vectors checked into the repo, and boundaries are identical whatever buffer size the caller reads with. Speed meets the P0.2.4 target.
- [x] **P1.2.2 Streaming file hasher** (`PeerSync.Engine/Hashing/FileHasher.cs`) (#8)
  - Done when: one sequential read produces chunk hashes and the whole-file hash. A file modified during hashing returns "retry later" rather than a wrong record. Files locked by other apps are opened with `FileShare.ReadWrite | FileShare.Delete`.
  - P1.2 evidence: [golden vectors, 1.078 GB/s benchmark and mutation checks](docs/verification/P1.2-chunk-hashing-2026-10-19.md), [green five-job CI](https://github.com/taskscape/PeerSync/actions/runs/39183549325). Merged tree equals the tested head.
- [x] **P1.2.3 Immutable published content** (`PeerSync.Engine/Hashing/PublishedBlocks.cs`) (#27)
  - Done when: hashing captures independent verified chunks before publication; rooted no-overwrite installation flushes bytes, persisted readiness withholds uncaptured legacy live records, and serving/reuse verifies bytes under proper read guards. Incoming files capture before completion. Mutation, reopening, cancellation, corrupt/unknown/reparse cache entries, deduplication, readiness migration/backfill and unchanged-version preservation have integration tests. This is a prerequisite for P1.6's real engine simulations, not a simulation-only archive shortcut.
  - Merged with [verification](docs/verification/P1.2.3-published-blocks-2026-10-19.md): 22 content cases, readiness/v4 migration checks and all 493 local/CI cases pass; native Server 2022/2025 pass all 304 Engine cases. All five required jobs pass, including 125,340 boundary seeds; tested/prospective/actual merge trees match. The unchanged 100k-file scan passes in 0.61 s, with no hashes/updates. History/retention remains P1.2.4.
- [ ] **P1.2.4 Superseded published version history and bounded block retention** (`PeerSync.Engine/Hashing/`, `PeerSync.Engine/Archive/`) [WIP @Codex 2026-10-19]
  - Done when: locally superseded observed/published snapshots can be materialized under the normal archive/conflict policy from immutable blocks, preserving version bytes through crash/restart. Reference-aware retention/cleanup never removes current/needed/history blocks prematurely, safely handles unreferenced interrupted captures, and follows archive expiry/cap policy. Real simulation invariants exercise these production paths before Gate P1.
  - [x] **P1.2.4a Recoverable local supersession history** (`PeerSync.Engine/Archive/SnapshotHistory.cs`) (#28)
    - Done when: content-changing scans preserve the prior authenticated snapshot via verified blocks and normal archive/conflict policy before replacing its local row. Kind-4 recovery, exact-source markers and durable accounting prevent loss/duplicate copies after restart or later scan commit failure. Edits/deletes/type changes, RW/RO, metadata-only no-ops, collisions, corruption/missing blocks, cancellation, root/reparse changes and all durable boundaries have native/storage coverage. Current user files are never rolled back by history recovery.
    - Merged with [verification](docs/verification/P1.2.4a-local-history-2026-10-19.md): 35 history cases, two scheduler regressions and four storage cases cover reconstruction, reuse, durable failure/recovery and source withholding. All 534 local/CI cases and native Server 2022/2025 pass. All five required jobs pass, including 127,424 boundary seeds; tested/prospective/actual merge trees match. The 100k-file unchanged scan passes in 0.60 s. Reference-aware retention/catalog cleanup remains P1.2.4b.
  - [ ] **P1.2.4b Reference-aware block retention and orphan cleanup** (`PeerSync.Engine/Hashing/`) [WIP @Codex 2026-10-19]
    - Done when: a persistent ownership/reference catalog and verified cleanup preserve current/needed/pending/history data, reconcile interrupted captures and apply bounded retention after archive expiry/cap retirement. Unknown/changed/locked/reparse objects remain protected and failures retry visibly. Simulation invariants cover the production cleanup path.
    - [x] **P1.2.4b1 Durable block ownership and capture recovery** (`PeerSync.Engine/Hashing/`, `PeerSync.Storage/FolderDb/`) (#30)
      - Done when: ownership proofs precede data writes, FULL catalog registration precedes readiness/publication, and startup reconciles complete owned staging/installed objects without adopting or deleting unknown data. Root/identity/checksum/hash/length/link checks, migration, restart boundaries, cancellation and failed registration have native/storage tests.
      - Implemented with [verification](docs/verification/P1.2.4b1-block-ownership-2026-10-19.md): 25 native and 12 Storage cases; canonical runner passes all 571 cases plus native/probe/publishing in 99.18 s, exit 0, with local fuzz explicitly skipped. The 100k-file unchanged scan passes in 0.84 s. Merged in (#30) with all five required jobs green; no block eviction is enabled.
    - [ ] **P1.2.4b2 Reference snapshots and bounded retention** (`PeerSync.Engine/Hashing/`, `PeerSync.Engine/Archive/`) [WIP @Codex 2026-10-19]
      - Done when: serialized persistent reference snapshots and policy retirement protect current/needed/pending/history blocks, clean only verified owned expired/unreferenced objects, reconcile interruptions, and retain unknown/changed/locked/reparse data with visible retries. Production simulation invariants and capacity/age tests cover cleanup before Gate P1.
      - [x] **P1.2.4b2a Durable archive retirement witnesses** (`PeerSync.Engine/Archive/ArchiveManager.cs`, `PeerSync.Storage/FolderDb/`) (#30)
        - Done when: verified policy deletion has a FULL root/file/row-bound witness before data removal; completion atomically retires the unchanged archive row only after absence. Restart, all durable boundaries, failed preparation/completion, cancellation, replacement/hardlink/reparse/locks and unexpected missing rows have native/storage coverage. No history reference release relies on absence alone.
        - Implemented with [verification](docs/verification/P1.2.4b2a-archive-retirement-2026-10-19.md): 14 additional native and 11 Storage cases; canonical runner passes all 596 cases plus native/probe/publishing in 110.18 s, exit 0, explicitly skipping separate CI fuzz. Merged in (#30) with all five required jobs green. Completed witnesses currently remain durable; reference release/sweeping is still P1.2.4b2b.
      - [ ] **P1.2.4b2b Persistent references and verified block sweeping** (`PeerSync.Engine/Hashing/`) [WIP @Codex 2026-10-19]
        - Done when: current/needed/pending/retained history reference snapshots serialize with every reference-changing engine path, policy retirement and owned-object sweeps. Capacity/age/grace, archive/history integration, interrupted sweep recovery and production simulation invariants pass; unknown or unverifiable objects remain protected with visible retries.
        - [x] **P1.2.4b2b1 Persistent current/needed/history reference snapshots** (`PeerSync.Engine/Hashing/BlockReferences.cs`, `PeerSync.Storage/FolderDb/`) (#30)
          - Done when: bounded staged reference batches activate FULL-durably only after complete authenticated/root-bound enumeration; prior active snapshots survive failure/cancellation. Local hints/withheld records, all needed candidates and history manifests are protected. Receive/recompute and filesystem paths share serialization; native/storage/gate tests cover restart and failed activation. Unmanifested archives prevent any future sweep.
          - Implemented locally with [verification](docs/verification/P1.2.4b2b1-block-references-2026-10-19.md): 17 native and 14 Storage additions; all 633 canonical cases plus native/probe/publishing pass in 101.24 s, exit 0 (separate CI fuzz skipped locally). The 100k-file unchanged walk takes 0.89 s and authenticated reference refresh 11.00 s with 251 shared blocks. Merged in (#30) with all five required jobs green; no deletion is enabled.
        - [ ] **P1.2.4b2b2 Archive references, policy release and verified sweeping** (`PeerSync.Engine/Hashing/`, `PeerSync.Engine/Archive/`) [WIP @Codex 2026-10-19]
          - Done when: capture retained archive content references before cleanup, preserve archive-creation/missing-row/legacy-migration provenance, apply explicit policy retirement safely, expire eligible owned orphans with grace/cap limits, and recover interrupted verified sweeps. Preserve current/needed/pending/history and unknown/changed/locked/reparse data; production simulations exercise cleanup before Gate P1.
          - [x] **P1.2.4b2b2a Transactional archive provenance** (`PeerSync.Storage/FolderDb/`) (#31)
            - Done when: migrate existing archive rows and transactionally preserve exact source/root metadata on every insert, update and removal, including apply/history/restore completion and missing reconciliation. Stable IDs survive archive-ID reuse, cancellation/rollback and reopening; original archive writes fail if provenance cannot commit. Content manifests and policy release remain mandatory parent work.
            - Implemented locally with [verification](docs/verification/P1.2.4b2b2a-archive-provenance-2026-10-19.md): 14 Storage additions and native missing/retirement assertions pass with all 647 canonical cases plus native/probe/publishing in 110.14 s, exit 0 (separate CI fuzz skipped locally). Merged in (#31) with all five required jobs green; no content release/deletion is enabled.
          - [x] **P1.2.4b2b2b Verified archive content manifests** (`PeerSync.Engine/Archive/`, `PeerSync.Engine/Hashing/`, `PeerSync.Storage/FolderDb/`) (#31)
            - Done when: root/source/native-file-bound archive manifests stage in bounded batches and FULL-activate only after whole-file verification and block registration. References include verified content and stable provenance coverage after row removal/reuse; interrupted capture/activation retains prior protection. Missing/changed/locked/linked/reparse/oversized sources defer visibly, with native/storage/gate/restart tests. No policy release or deletion is enabled by this child.
            - Implemented locally with [verification](docs/verification/P1.2.4b2b2b-archive-content-2026-10-19.md): 25 native and 13 Storage additions pass with all 685 canonical cases plus native/probe/publishing in 107.07 s, exit 0 (separate CI fuzz skipped locally). The real 100k-file unchanged walk takes 0.61 s and reference refresh 7.87 s. Complete manifests recover from verified blocks without the original; staged prefixes remain protected. Merged in (#31) with all five required jobs green; policy release/deletion remain parent work.
          - [ ] **P1.2.4b2b2c Policy-aware history retirement and verified block sweeping** (`PeerSync.Engine/Archive/`, `PeerSync.Engine/Hashing/`, `PeerSync.Storage/FolderDb/`) [WIP @Codex 2026-10-19]
            - Retention implementation is merged in (#32) with [all five required jobs green and exact tree audit](docs/verification/P1-retention-ci-merge-2026-10-19.md), including all 834 canonical cases and 522 native Engine cases on each Server variant. The task and its parents remain WIP for production simulation invariants.
            - Done when: durable history reuse accounting prevents premature age/cap retirement; stable source IDs bind policy witnesses; legacy/current/needed/pending/private/public history protection and explicit policy release feed fresh serialized snapshots. Grace/cap cleanup removes only verified owned unreferenced objects through FULL deletion intents, handles restart/new references/cancellation safely and preserves unknown/changed/linked/locked/reparse data. Production simulation invariants remain mandatory before closing the parent and Gate P1.
            - Local first part with [evidence](docs/verification/P1.2.4b2b2c-history-accounting-2026-10-19.md): reproduced and repaired premature expiry of newer history reusing an older archive; added FULL retention accounting, logical age/cap ordering, pending-reservation fallback and stable witness/source binding. Authenticated legacy bootstrap now accounts guarded copies or reconstructs through ordinary application recovery, preserving original times and current data. Unicode indexed archive names prevent reconstruction collisions with missing indexed files. All 717 canonical cases and native/probe/publishing pass locally (separate CI fuzz skipped). Reference release, grace/sweep/recovery and simulations remain open; no block deletion is enabled. CI/merge remain pending.
            - Local policy release with [evidence](docs/verification/P1.2.4b2b2c-policy-release-2026-10-19.md): FULL exact-marker expiry/completed-source retirement, authenticated public observation, durable source releases and atomic reactivation now feed fresh reference snapshots. Current/needed protection, missing-row preservation, partial/active content, crash/reopen and SQLite/cancellation/privacy checks pass with all 738 canonical tests plus native/probe/publishing in 104.72 s, exit 0 (separate CI fuzz skipped locally). Grace/sweep/recovery, bounded cleanup and simulations remain open; no block deletion is enabled. CI/merge remain pending.
            - Local owned block sweeping with [evidence](docs/verification/P1.2.4b2b2c-block-sweeping-2026-10-19.md): legacy grace, bounded durable orphan observations, 24-hour capacity grace/ArchiveDays expiry, verified native deletion with FULL exact intents and fresh-reference restart recovery now work under both gates. Missing readiness is withheld; unknown/changed/locked/linked/reparse objects are preserved. All 767 canonical cases plus native/probe/publishing pass in 111.06 s, exit 0 (separate CI fuzz skipped locally); the 100k-file unchanged walk takes 0.68 s and reference refresh 8.01 s. Partial-capture expiry, bounded historical metadata cleanup and simulations remain open. CI/merge remain pending.
            - Local incomplete capture retirement with [evidence](docs/verification/P1.2.4b2b2c-capture-retirement-2026-10-19.md): aged strict prefixes retire under root/proof/data/hash-bound FULL intents and fresh coverage; referenced duplicates require a guarded, verified, cataloged complete replacement. Restart/new-demand/readiness, unknown/changed/locked/linked/reparse data, SQLite rollback/privacy, old-journal ordering and shared 1,000-operation bounds pass with all 815 canonical cases plus native/probe/publishing in 109.11 s, exit 0 (separate CI fuzz skipped locally). Bounded historical metadata cleanup and production simulations remain open. CI/merge remain pending.
            - Local historical metadata cleanup with [evidence](docs/verification/P1.2.4b2b2c-metadata-cleanup-2026-10-19.md): FULL retiring state and bounded worklists drain at most 1,000 mutations, preserve attached/pending/foreign sources and byte references, and expire only eligible unlinked witnesses. History renewal recaptures exact signed chunks before committing its hold; partial-drain reactivation, restart, actual SQLite failure and v17 migration preserve data and source identity. All 830 canonical cases plus native/probe/publishing pass in 108.10 s, exit 0 (separate CI fuzz skipped locally). Production simulations and CI/merge remain open.

### 7.5 Code: FastCDC (P1.2.1)

**Tricky:**

- The gear table must be identical on every peer forever. Don't build it with `System.Random`, whose algorithm isn't guaranteed stable across .NET versions. Derive it from SHA-256.
- Call `NextCut` only with at least `Max` bytes in the window, or at end of file. Otherwise boundaries depend on read sizes and peers disagree.

```csharp
// src/PeerSync.Crypto/Chunking/FastCdc.cs
public readonly record struct ChunkParams(int Min, int Avg, int Max, ulong MaskS, ulong MaskL)
{
    // Size classes are part of the protocol (FileRecordBody.chunk_class). Never change existing ones.
    public static ChunkParams ForClass(uint chunkClass) => chunkClass switch
    {
        0 => Make(16),   // ~64 KiB average, files under 256 MiB
        1 => Make(19),   // ~512 KiB average, files under 8 GiB
        2 => Make(21),   // ~2 MiB average, larger files
        _ => throw new ArgumentOutOfRangeException(nameof(chunkClass)),
    };

    public static uint ClassFor(long fileSize) => fileSize < (256L << 20) ? 0u : fileSize < (8L << 30) ? 1u : 2u;

    // Normalized chunking, level 2: harder cut before Avg, easier after. High bits see the widest window.
    static ChunkParams Make(int avgBits) => new(
        Min: 1 << (avgBits - 2), Avg: 1 << avgBits, Max: 1 << (avgBits + 2),
        MaskS: ulong.MaxValue << (64 - (avgBits + 2)),
        MaskL: ulong.MaxValue << (64 - (avgBits - 2)));
}

public static class FastCdc
{
    static readonly ulong[] Gear = BuildGear();

    static ulong[] BuildGear()
    {
        var table = new ulong[256];
        Span<byte> input = stackalloc byte[16];
        Span<byte> hash = stackalloc byte[32];
        for (int i = 0; i < 256; i++)
        {
            input.Clear();
            "psp1 gear"u8.CopyTo(input);
            input[15] = (byte)i;
            SHA256.HashData(input, hash);
            table[i] = BinaryPrimitives.ReadUInt64LittleEndian(hash);
        }
        return table;
    }

    /// Returns the length of the next chunk. buf must hold at least p.Max bytes unless at end of file.
    public static int NextCut(ReadOnlySpan<byte> buf, in ChunkParams p)
    {
        int n = buf.Length;
        if (n <= p.Min) return n;
        if (n > p.Max) n = p.Max;
        int normal = Math.Min(p.Avg, n);
        ulong fp = 0;
        int i = p.Min;                                     // cut-point skipping: nothing before Min
        for (; i < normal; i++) { fp = (fp << 1) + Gear[buf[i]]; if ((fp & p.MaskS) == 0) return i + 1; }
        for (; i < n; i++)      { fp = (fp << 1) + Gear[buf[i]]; if ((fp & p.MaskL) == 0) return i + 1; }
        return n;
    }
}
```

### 7.6 Code: streaming hasher (P1.2.2)

Superseded by `src/PeerSync.Engine/Hashing/FileHasher.cs`, which additionally
bounds the pass to the original length, compares handle/path identity and
change metadata, rejects reparse files, and reports count-only progress.

```csharp
// src/PeerSync.Engine/Hashing/FileHasher.cs
public sealed record ChunkInfo(long Offset, int Length, byte[] Hash);
public sealed record FileHash(long Length, DateTime MtimeUtc, uint ChunkClass, List<ChunkInfo> Chunks, byte[] WholeHash);

public static async Task<FileHash?> HashAsync(string path, CancellationToken ct)
{
    var before = new FileInfo(path);
    uint cls = ChunkParams.ClassFor(before.Length);
    var p = ChunkParams.ForClass(cls);
    var chunks = new List<ChunkInfo>();
    var whole = Blake3.Hasher.New();                                 // native state: always dispose
    byte[] buf = ArrayPool<byte>.Shared.Rent(p.Max * 2);
    long offset = 0;
    byte[] wholeHash;
    try
    {
        await using var fs = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open, Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,          // don't block the user's apps
            Options = FileOptions.SequentialScan | FileOptions.Asynchronous,
            BufferSize = 0,
        });
        int start = 0, end = 0;
        bool eof = false;
        while (true)
        {
            while (!eof && end - start < p.Max)                      // keep >= Max bytes in the window
            {
                if (start > 0) { Buffer.BlockCopy(buf, start, buf, 0, end - start); end -= start; start = 0; }
                int read = await fs.ReadAsync(buf.AsMemory(end, buf.Length - end), ct);
                if (read == 0) eof = true; else end += read;
            }
            if (start == end) break;
            int len = FastCdc.NextCut(buf.AsSpan(start, end - start), p);
            var chunk = buf.AsSpan(start, len);
            whole.Update(chunk);
            chunks.Add(new ChunkInfo(offset, len, Bytes(Blake3.Hasher.Hash(chunk))));
            offset += len;
            start += len;
        }
        wholeHash = Bytes(whole.Finalize());
    }
    finally
    {
        whole.Dispose();
        ArrayPool<byte>.Shared.Return(buf);
    }

    var after = new FileInfo(path);
    if (after.Length != before.Length || after.LastWriteTimeUtc != before.LastWriteTimeUtc || offset != before.Length)
        return null;                                                 // changed while we read it: debounce, retry
    return new FileHash(offset, before.LastWriteTimeUtc, cls, chunks, wholeHash);
}

// Copy a Blake3.Hash (a 32-byte struct) out through a local, never through a temporary.
static byte[] Bytes(Blake3.Hash h) => h.AsSpan().ToArray();
```

### P1.3 Storage

Review repairs for storage/settings, scanner role transitions, scheduling and
conflict handling are tracked in [verification](docs/verification/P1-review-repairs-2026-10-19.md).
Those repairs and foundations are now merged with green CI in PRs #11–#20.
See [the CI/merge audit](docs/verification/P1-foundations-ci-merge-2026-10-19.md).
Only tasks whose complete acceptance criteria hold are ticked below.

- [x] **P1.3.1 SQLite plumbing** (`PeerSync.Storage/Db.cs`) (#9)
  - Done when: connections open with the pragmas in 7.7. One writer per database (a single-reader channel), any number of readers. Migrations via `user_version` take a backup first.
  - Merged as `06c48f568566dc30ba03e204aa60655f1f016228`; [required CI attempt 4](https://github.com/taskscape/PeerSync/actions/runs/39184897814/attempts/4) passed all five jobs, including 600 s fuzz (126,489 seeds, zero crashes). See [verification](docs/verification/P1.3.1-sqlite-plumbing-2026-10-19.md).
- [x] **P1.3.2 Folder index schema and repositories** (`PeerSync.Storage/FolderDb/`) (#11, #20)
  - Done when: the schema in 7.7 is created by migration 1. Repositories cover `local_files`, `remote_files`, `needed`, `chunks`, `devices`, `archive` and `meta`. Bulk updates commit in batches of 1,000 rows per transaction. Indexing 1M rows takes under 60 s on the reference SSD.
- [x] **P1.3.3 Central state database** (`PeerSync.Storage/StateDb/`) (#11, #20)
  - Done when: `peersync.db` has `folders` (id, path, access level, protected key, `settings_json`, paused), `devices`, `pending_approvals` and `audit_log`. Folder keys are stored only through `FolderKeyProtector`.

### 7.7 Code: SQLite setup and folder schema (P1.3)

Connection/writer/migration plumbing is superseded by `src/PeerSync.Storage/Db.cs`.
It enforces one in-process writer per normalized path, uses separate read-only
connections, and backs up through SQLite before transactional migrations.

**Tricky:**

- SQLite allows one writer at a time. Many async writers cause `SQLITE_BUSY` storms. Funnel every write for a database through one writer task, and batch rows in transactions: per-row autocommit is about 100 times slower.
- Key paths on `path_key = ToUpperInvariant(path)`, because SQLite `NOCASE` only folds ASCII.

```csharp
// src/PeerSync.Storage/Db.cs
public static SqliteConnection Open(string file)
{
    var cs = new SqliteConnectionStringBuilder
    {
        DataSource = file,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Private,
        Pooling = true,
        DefaultTimeout = 30,
    }.ToString();
    var c = new SqliteConnection(cs);
    c.Open();
    using var cmd = c.CreateCommand();
    cmd.CommandText = """
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = NORMAL;   -- WAL + NORMAL: durable at checkpoints, no corruption on power loss
        PRAGMA foreign_keys = ON;
        PRAGMA busy_timeout = 5000;
        PRAGMA temp_store = MEMORY;
        """;
    cmd.ExecuteNonQuery();
    return c;
}
```

```sql
-- src/PeerSync.Storage/FolderDb/Migrations/0001_initial.sql
CREATE TABLE local_files (
  path_key      TEXT PRIMARY KEY,          -- upper-invariant record path
  path          TEXT NOT NULL,             -- original case, '/'-separated, NFC
  type          INTEGER NOT NULL,          -- 0 file, 1 dir, 2 symlink
  size          INTEGER NOT NULL,
  mtime_ticks   INTEGER NOT NULL,
  attributes    INTEGER NOT NULL,
  deleted       INTEGER NOT NULL DEFAULT 0,
  changed_ticks INTEGER NOT NULL,
  file_hash     BLOB,
  vv            BLOB NOT NULL,
  record_body   BLOB,                      -- SignedRecord.body, verbatim
  signature     BLOB,
  ntfs_file_id  BLOB,                      -- 128-bit FILE_ID_INFO, for rename detection
  local_seq     INTEGER NOT NULL UNIQUE,
  flags         INTEGER NOT NULL DEFAULT 0 -- 1 ignored, 2 unsyncable, 4 needs rescan
);
CREATE TABLE remote_files (
  device_id   BLOB NOT NULL,
  path_key    TEXT NOT NULL,
  vv          BLOB NOT NULL,
  record_body BLOB NOT NULL,
  signature   BLOB NOT NULL,
  PRIMARY KEY (device_id, path_key)
) WITHOUT ROWID;
CREATE TABLE needed (
  path_key    TEXT PRIMARY KEY,
  source_device BLOB NOT NULL,             -- a device holding the winning version
  priority    INTEGER NOT NULL
);
CREATE TABLE chunks (
  hash        BLOB NOT NULL,
  path_key    TEXT NOT NULL,
  file_offset INTEGER NOT NULL,            -- not "offset": that is an SQL keyword
  length      INTEGER NOT NULL,
  PRIMARY KEY (hash, path_key, file_offset)
) WITHOUT ROWID;
CREATE TABLE devices (
  device_id         BLOB PRIMARY KEY,
  name              TEXT,
  last_seen_ticks   INTEGER,
  last_seq_received INTEGER NOT NULL DEFAULT 0,
  approval          INTEGER NOT NULL DEFAULT 0,  -- 0 unknown, 1 approved, 2 blocked
  pinned            INTEGER NOT NULL DEFAULT 1
);
CREATE TABLE archive (
  id            INTEGER PRIMARY KEY,
  original_path TEXT NOT NULL,
  archive_path  TEXT NOT NULL,
  archived_ticks INTEGER NOT NULL,
  size          INTEGER NOT NULL,
  file_hash     BLOB
);
CREATE INDEX ix_archive_time ON archive(archived_ticks);
CREATE TABLE meta (key TEXT PRIMARY KEY, value BLOB);
PRAGMA user_version = 1;
```

### P1.4 Scanner and watcher

- [x] **P1.4.1 Scanner** (`PeerSync.Engine/Scanning/Scanner.cs`) (#12, #20)
  - Done when: a full walk skips `.psp`, `*.!psp`, ignored paths and reparse points (recorded, not followed). Unchanged size, mtime, attributes and FileId mean no hashing. A rescan of 100k unchanged files takes under 10 s.
  - Code: 7.8 for the NTFS file id.
  - Evidence: [scanner verification and 100k-file benchmark](docs/verification/P1.4.1-scanner-2026-10-19.md) and [green CI/merge audit](docs/verification/P1-foundations-ci-merge-2026-10-19.md), including role-transition and tombstone repairs.
- [x] **P1.4.2 Watcher with debounce** (`PeerSync.Engine/Scanning/FolderWatcher.cs`) (#13, #20)
  - Done when: events are coalesced per path (quiet 2 s, or 30 s maximum). A buffer overflow triggers a full scan. Our own writes don't create new versions.
  - Code: 7.9.
  - Evidence: [watcher/scheduler verification](docs/verification/P1.4.2-3-watcher-scheduler-2026-10-19.md) and [green CI/merge audit](docs/verification/P1-foundations-ci-merge-2026-10-19.md). The native watcher test commits own-write metadata before debounce; production puller integration remains in P1.5.3.
- [x] **P1.4.3 Rescan scheduler** (`PeerSync.Engine/Scanning/RescanScheduler.cs`) (#13, #20)
  - Done when: each folder rescans at start-up and every `RescanIntervalSeconds` (default 600), at most one folder hashes at a time per disk, and hashing runs at low I/O priority.
  - Local evidence: [watcher/scheduler verification](docs/verification/P1.4.2-3-watcher-scheduler-2026-10-19.md). The initial budget serializes folder scans process-wide, including across partitions and unknown mappings.

- [x] **P1.4.4 Detect edits with restored mtime** (`PeerSync.Engine/Scanning/`, `PeerSync.Storage/FolderDb/`) (#30)
  - Done when: a same-length edit which keeps file identity, attributes and original last-write time is detected and preserves the previously published snapshot. Native change metadata participates in the unchanged fast path, including restart/upgrade and own-write completion, without creating versions for directory child changes or metadata-only cache refresh. Native regression, signed RW/RO backfill, migration, applied-file no-loop checks and the real unchanged 100k-file benchmark pass.
  - Implemented locally with [verification](docs/verification/P1.4.4-native-change-stamp-2026-10-19.md): the prior implementation fails the exact native regression; five native and one Storage additions pass with all 602 canonical cases plus native/probe/publishing in 109.45 s, exit 0 (separate CI fuzz skipped locally). The unchanged 100k-file walk passes in 0.87 s. Merged in (#30) with all five required jobs green; restored-mtime and native-root regressions pass on both Server variants.

### 7.8 Code: NTFS file id (P1.4.1)

Superseded by `src/PeerSync.Engine/Native/NativeRoot.cs`, which enumerates
128-bit IDs with directory metadata and opens descendants relative to a pinned
root without reparse traversal. This also detects same-size/mtime replacements.

.NET doesn't expose the file id. Open a metadata handle only when size or mtime changed, or when looking for rename candidates.

```csharp
// src/PeerSync.Engine/Native/FileIdentity.cs
internal static partial class FileIdentity
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ID_INFO { public ulong VolumeSerialNumber; public Guid FileId; }   // FILE_ID_128 = 16 bytes
    private const int FileIdInfo = 18;   // FILE_INFO_BY_HANDLE_CLASS.FileIdInfo

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FILE_ID_INFO info, int size);

    public static (ulong Volume, Guid Id)? TryGet(string path)
    {
        try
        {
            using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return GetFileInformationByHandleEx(h, FileIdInfo, out var info, Marshal.SizeOf<FILE_ID_INFO>())
                ? (info.VolumeSerialNumber, info.FileId) : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
```

### 7.9 Code: watcher with debounce (P1.4.2)

Superseded by `src/PeerSync.Engine/Scanning/FolderWatcher.cs`: bounded ingress
and pending state, monotonic debounce and consumer-side full-scan recovery.

**Tricky:**

- Never do work in `FileSystemWatcher` callbacks. They run on a thread-pool thread, and slow handlers overflow the kernel buffer. Push the path into a channel and return.
- A `Renamed` event needs both the old and the new path.
- The `Error` event means events were lost: schedule a full scan.

```csharp
// src/PeerSync.Engine/Scanning/FolderWatcher.cs
public sealed class FolderWatcher : IDisposable
{
    readonly FileSystemWatcher fsw;
    readonly Channel<string> paths = Channel.CreateUnbounded<string>(new() { SingleReader = true });
    readonly Action requestFullScan;

    public FolderWatcher(string root, Action requestFullScan)
    {
        this.requestFullScan = requestFullScan;
        fsw = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,   // the documented maximum
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
                         | NotifyFilters.Size | NotifyFilters.Attributes | NotifyFilters.CreationTime,
        };
        fsw.Created += (_, e) => paths.Writer.TryWrite(e.FullPath);
        fsw.Changed += (_, e) => paths.Writer.TryWrite(e.FullPath);
        fsw.Deleted += (_, e) => paths.Writer.TryWrite(e.FullPath);
        fsw.Renamed += (_, e) => { paths.Writer.TryWrite(e.OldFullPath); paths.Writer.TryWrite(e.FullPath); };
        fsw.Error += (_, _) => this.requestFullScan();          // InternalBufferOverflowException: events were lost
        fsw.EnableRaisingEvents = true;
    }

    /// Yields batches of paths that have been quiet for 2 s, or pending for 30 s.
    public async IAsyncEnumerable<IReadOnlyCollection<string>> ReadyBatchesAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var pending = new Dictionary<string, (DateTime First, DateTime Last)>(StringComparer.OrdinalIgnoreCase);
        using var tick = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        // PeriodicTimer allows only ONE outstanding WaitForNextTickAsync: keep both tasks across iterations.
        var readTask = paths.Reader.WaitToReadAsync(ct).AsTask();
        var tickTask = tick.WaitForNextTickAsync(ct).AsTask();
        while (!ct.IsCancellationRequested)
        {
            await Task.WhenAny(readTask, tickTask);
            if (readTask.IsCompleted)
            {
                while (paths.Reader.TryRead(out var p))
                {
                    if (IsInternal(p)) continue;                     // .psp\..., *.!psp
                    var now = DateTime.UtcNow;
                    pending[p] = pending.TryGetValue(p, out var t) ? (t.First, now) : (now, now);
                }
                readTask = paths.Reader.WaitToReadAsync(ct).AsTask();
            }
            if (!tickTask.IsCompleted) continue;
            tickTask = tick.WaitForNextTickAsync(ct).AsTask();

            var cutoff = DateTime.UtcNow;
            var ready = pending.Where(kv => cutoff - kv.Value.Last > TimeSpan.FromSeconds(2)
                                         || cutoff - kv.Value.First > TimeSpan.FromSeconds(30))
                               .Select(kv => kv.Key).ToList();
            foreach (var p in ready) pending.Remove(p);
            if (ready.Count > 0) yield return ready;
        }
    }

    static bool IsInternal(string p) =>
        p.Contains(@"\.psp\", StringComparison.OrdinalIgnoreCase)
        || p.EndsWith(@"\.psp", StringComparison.OrdinalIgnoreCase)
        || p.EndsWith(".!psp", StringComparison.OrdinalIgnoreCase);

    public void Dispose() => fsw.Dispose();
}
```

Our own writes need no special suppression: the puller records the new state in the database before the debounced event is processed, so the scanner sees "unchanged".

### P1.5 Sync logic

- [x] **P1.5.1 Needed-set computation** (`PeerSync.Engine/Sync/NeededSet.cs`) (#14, #20)
  - Done when: for each path, the winning version among local and all remote records is computed with 7.10. `needed` contains exactly the paths where the winner isn't the local version. Incremental updates on each `IndexUpdate` are covered by tests.
  - Verified candidate selection, atomic remote/cursor/needed updates, replay/cancellation and RO-policy tests are merged. See [verification](docs/verification/P1.5.1-2-needed-conflicts-2026-10-19.md) and [green CI/merge audit](docs/verification/P1-foundations-ci-merge-2026-10-19.md), including RO causal-selection and unknown-field repairs.
- [ ] **P1.5.2 Conflict resolution: latest version wins** (`PeerSync.Engine/Sync/ConflictResolver.cs`) [WIP @Codex 2026-10-19]
  - Done when: every row of the design doc's conflict table has a test, and every peer picks the same winner (live `mtime_ticks`, deletion `changed_ticks`, tie broken by the higher DeviceId).
    - A loser above `ArchiveMaxFileBytes`, or with "Keep both copies" on, becomes `name.sync-conflict-YYYYMMDD-HHMMSS-XXXX.ext`.
    - Only RW peers publish the merged record. RO peers apply the same rule locally and never publish.
  - Ref: Sync engine design, Version vectors and conflicts.
  - Local foundation: deterministic causal/concurrent selection, signed vector merges, loser fate and bounded conflict names are tested. Directory application and file/directory preservation are merged. Full sender-index barriers/unknown-child coordination and case-clash preservation still require puller integration; this task remains incomplete.
- [ ] **P1.5.3 Puller** (`PeerSync.Engine/Sync/Puller.cs`) [WIP @Codex 2026-10-19]
  - Done when: directories are created first, then files in priority order, then deletions, deepest directory last. Files are assembled from local chunks before network chunks, verified, and swapped in with 7.11. A locked target retries with backoff (10 s up to 10 min).
  - Safe mutation foundation: [root-relative handle and no-overwrite move checks](docs/verification/P1.5.3-4-safe-mutations-2026-10-19.md). Durable file/directory application/recovery, archive accounting and pruning are merged; pull ordering, retries, case coordination and stale-temporary retention/cleanup remain incomplete.
  - [Verified chunk assembly](docs/verification/P1.5.3-chunk-assembly-2026-10-19.md) reuses verified temporary/local chunks before network fallback, checks each chunk and the independent whole-file hash, and records safe diagnostic progress. It does not install a target or publish index metadata.
  - [Handle-based metadata preparation](docs/verification/P1.5.3-file-metadata-2026-10-19.md) preserves DACL/protection and creation time, validates representable mtimes and handles read-only archive moves. Coordinator/recovery and retries remain pending.
  - [x] **P1.5.3a Durable apply intent and index completion** (`PeerSync.Storage/FolderDb/ApplyJournal.cs`) (#18)
    - Done when: migration 4 creates bounded per-path intents through the existing backup path; intent lifecycle and completion use FULL-synchronous serialized transactions. Completion atomically commits local/chunk/sequence/archive/needed state and removes the intent. Failure, cancellation and local-sequence races preserve recoverable intent; regression tests cover reopening and rollback.
    - Merged with [green CI/merge audit](docs/verification/P1-foundations-ci-merge-2026-10-19.md): 36 Storage cases at #18, atomic rollback/reopening/race checks and a 5.75 s million-row indexing regression. See [verification](docs/verification/P1.5.3a-apply-journal-2026-10-19.md). Filesystem recovery/coordinator remain part of P1.5.3.
  - [ ] **P1.5.3b Recoverable archival, atomic final installation and startup recovery** (`PeerSync.Engine/Sync/FileApplier.cs`) [WIP @Codex 2026-10-19]
    - Done when: verified assembly and metadata preparation feed a guarded old-handle move followed by atomic no-overwrite final installation, with durable root/file-bound recovery intent and safe restart recovery before scanning. The pair permits the user-approved brief missing-target interval visible to external apps. Unknown concurrent files are preserved; cancellation/failure and each mutation boundary have integration coverage. Parent scheduling/directory/conflict requirements remain mandatory.
    - [Shared folder gate and capability checks](docs/verification/P1.5.3b-folder-gate-2026-10-19.md) pass. Guarded POSIX replacement is unsupported on this local machine. The user approved [recoverable handle moves](docs/proposals/P1-atomic-replacement-recovery.md) on 2026-10-19.
    - Regular-file installation/deletion, verified staging reuse, metadata-only/case changes and startup recovery are merged in (#21) with [local and CI evidence](docs/verification/P1.5.3b-recoverable-install-2026-10-19.md). Directory/type/conflict coordination, scheduling and stale-temp retention/cleanup still belong to the parent integration; the task remains unchecked pending those checks.
  - [x] **P1.5.3c Directory application and nonrecursive deletion** (`PeerSync.Engine/Sync/DirectoryApplier.cs`) (#25)
    - Done when: signed directory versions create/update directories and remove only empty directories through pinned handles, durable identity-bound intents and unified recovery before scanning. Unknown/concurrent children survive and are rescanned with RW/RO policy. Failure/reopening, metadata, root/reparse and identity/type races are tested. File/directory transitions, case-clash preservation and ordering/retries remain parent/conflict requirements.
    - Merged with [local and CI verification](docs/verification/P1.5.3c-directory-application-2026-10-19.md): 27 directory cases, all 239 Engine cases on local/Server 2022/2025, all five required jobs and 115,434 boundary seeds pass. Includes a reproduced/fixed own-write case regression; tested/prospective/actual merge trees match.
  - [x] **P1.5.3d Recoverable file/directory transitions** (`PeerSync.Engine/Sync/FileApplier.cs`) (#26)
    - Done when: both type transitions preserve the old file/tree, including unknown children, through durable kind/identity-bound staging and no-overwrite moves. Completion withholds stale descendant publication/work until normal scanning. Restart, cancellation, metadata, collisions, changed nodes/reparse paths and RW/RO behavior have integration coverage. General case-clash mapping and pull scheduling remain parent requirements.
    - Merged with [verification](docs/verification/P1.5.3d-type-transitions-2026-10-19.md): 43 transition cases, three descendant transaction cases and all 469 unit cases pass locally. All five required CI jobs pass, including native Server 2022/2025 and 125,657 boundary seeds; tested/prospective/actual merge trees match. Canonical local native/probe/publishing runner passed in 102.14 s, exit 0, explicitly skipping separate CI fuzz.
  - [x] **P1.5.3e Ordered pull scheduling and sender-index barriers** (`PeerSync.Engine/Sync/Puller.cs`, `PeerSync.Storage/FolderDb/`) (#33)
    - Done when: bounded durable queue descriptors order parents/files/deletions; scheduled deletions wait for complete advertised sender ranges, locked work retries across restart without delaying new versions, source routing is rechecked inside application, recovery/scans and preserved unknown data integrate without deadlocks, and native/storage/coordinator tests pass. Case-clash/hierarchy and simulation requirements remain mandatory parent work.
    - Merged with [green CI/main merge audit](docs/verification/P1-pull-coordinator-ci-merge-2026-10-19.md): all 897 canonical cases, both native Server variants and required boundary fuzz pass; exact trees match. P1.5.3e1-e4 are included. Parent case/hierarchy, temporary cleanup and production simulation requirements remain open.
    - [x] **P1.5.3e1 Durable work, range and retry metadata** (`PeerSync.Storage/FolderDb/`, `PeerSync.Engine/Sync/NeededSet.cs`) (#33)
      - Done when: authenticated needed rewrites atomically cache bounded scheduling descriptors and stable work fingerprints; monotonic sender targets, bounded priority reads, exact-work root-bound durable backoff, restart/cancellation/rollback and migration checks pass. This metadata does not itself authorize native application or complete the parent coordinator.
      - Local [verification](docs/verification/P1.5.3e1-pull-work-2026-10-19.md): 15 Storage and six Engine additions pass with all 855 canonical cases/native/probe/publishing in 104.95 seconds, exit 0 (CI fuzz explicitly skipped locally). Migration 19 preserves legacy needed rows and original v18 backups. Main/CI acceptance is recorded in the merged audit above; parent integration remains open.
    - [x] **P1.5.3e2 Guarded scheduled application and source routing** (`PeerSync.Engine/Sync/FileApplier.cs`, `PeerSync.Engine/Sync/DirectoryApplier.cs`) (#33)
      - Done when: scheduled tombstones recheck range completion under the reference gate through bounded native mutation, live transfers keep index receipt available, fetch routing uses the actual selected source and scan requirements report preserved data without changing existing direct-applier contracts.
      - Local [verification](docs/verification/P1.5.3e2-scheduled-application-2026-10-19.md): ten native additions and privacy checks pass with all 865 canonical cases/native/probe/publishing in 118.38 seconds, exit 0 (CI fuzz skipped locally). Main/CI acceptance is recorded in the merged audit above; production host and parent integration remain open.
    - [x] **P1.5.3e3 Ordered coordinator and retry loop** (`PeerSync.Engine/Sync/Puller.cs`) (#33)
      - Done when: the host-owned coordinator recovers before startup scanning, processes bounded ordered work, integrates durable retries/notifications, rescans preserved/changed state outside mutation gates and demonstrates independent-path progress, cancellation and restart through real appliers. Parent case/hierarchy and production simulation requirements remain open until separately verified.
      - Local [verification](docs/verification/P1.5.3e3-pull-coordinator-2026-10-19.md): 15 native coordinator cases and two bounded scheduling/deadline Storage cases pass with all 882 canonical tests/native/probe/publishing in 108.19 seconds, exit 0 (CI fuzz explicitly skipped locally). Shared scan budget, incremental changed paths, coalesced rescans, restart/backoff, actual-source fetch and cancellation/timer wakeups use production components. Main/CI acceptance is recorded above; parent requirements remain open.
    - [x] **P1.5.3e4 Scheduled recovery and incremental scan hardening** (`PeerSync.Engine/Sync/`, `PeerSync.Engine/Scanning/`) (#33)
      - Done when: prepared scheduled file/directory deletion recovery retains its barrier across restart and new targets, direct/legacy recovery remains compatible, unsupported live native timestamps stay per-item, invalid wire names and cache-only edits do not strand work, and partial scan/recompute failures force repair. Native regressions, canonical checks and required CI/main merge evidence pass.
      - Local [review repair evidence](docs/verification/P1.5.3e4-recovery-scan-hardening-2026-10-19.md): all five confirmed review findings have native regressions; 15 additions pass with all 897 canonical tests/native/probe/publishing in 109.41 seconds, exit 0 (CI fuzz skipped locally). Final-head green CI, resolved review threads and the exact main tree are audited above.
  - [x] **P1.5.3f Hierarchy integration for absent descendants** (`PeerSync.Engine/Sync/`) (#35)
    - Done when: connected engines converge nested tombstones on fresh nodes with absent/file ancestors without creating parents, deleting user objects, stranding needed work or losing prior child bytes. Native/recovery/concurrent-parent regressions pass. General case mapping and stale-temp cleanup remain separate parent requirements.
    - Local [verification](docs/verification/P1.5.3f-absent-descendant-hierarchy-2026-10-19.md): two baseline failures are fixed; eleven additions and all 981 canonical cases/native/probe/publishing pass in 140.25 seconds, exit 0 (boundary fuzz skipped locally). Rooted absence, version archival, scheduled recovery barriers and concurrent targets are covered. Final-head main/CI evidence is recorded in the PR #35 audit below.
  - [x] **P1.5.3g Owned stale-staging lifecycle** (`PeerSync.Engine/Sync/`, `PeerSync.Storage/FolderDb/`) (#35)
    - Done when: still-needed verified prefixes remain reusable across restart; only proven owned unneeded staging older than 24 hours expires under exact native guards and FULL deletion/recovery state. Unknown, changed, foreign, linked, reparse, locked and nonempty-directory objects survive. Cleanup is bounded, cancellable and integrated with coordinator startup/retries; native/storage and crash/race regressions pass.
    - [x] **P1.5.3g1 Durable parked staging catalog and resume** (#35)
      - Done when: root/FID/target/GUID/kind/time/fingerprint metadata is FULL-durable under its exact apply intent; unchanged re-parking preserves age, changed settled bytes reset it, catalog resume verifies bytes, legacy pointers remain safe, migration/restart/cancellation/native regressions pass. This child does not authorize deletion.
      - Local [verification](docs/verification/P1.5.3g1-staging-catalog-2026-10-19.md): twelve Storage and ten native additions pass, including protected changed data, both hardlink paths, cancellation/restart and long GUID siblings. All 1,003 canonical cases/native/probe/publishing pass in 120.87 seconds, exit 0 (boundary fuzz skipped locally). That prerequisite checkpoint enabled no deletion; g2/g3 and final CI/main acceptance are recorded below.
    - [x] **P1.5.3g2 Guarded expiry, recovery and coordinator integration** (#35)
      - Done when: age/need/intent checks, FULL retirement/completion and exact native deletion preserve newly needed/changed data through races/restart, skip unknown legacy names and bound work/diagnostics. All parent lifecycle criteria pass before its box is ticked.
      - Implementation constraints: persist a root-bound enumeration cursor; count all visited witnesses toward the 1,000-row budget so locked/needed entries do not starve others. Hash under native/filesystem guards before taking the reference gate; FULL-reserve only after fresh need/age/witness/no-apply checks. Recover journals first, durably cancel inaccessible work without deleting data, and distinguish missing metadata from preserved foreign replacements.
      - Local [verification](docs/verification/P1.5.3g2-staging-expiry-2026-10-19.md): six Storage and seventeen native additions cover exact expiry, fresh needs, protected/foreign data, restart before/after removal, cancellation, redacted failures and a 1,001-object fairness case. All 1,026 canonical cases/native/probe/publishing pass in 116.42 seconds, exit 0 (boundary fuzz skipped locally). FULL retirement/cursor migration and coordinator recovery/backoff are implemented; final main/CI acceptance is recorded below.
    - [x] **P1.5.3g3 Recovery handle and case-alias hardening** (#35)
      - Done when: concurrent-target recovery captures edited prepared bytes as protected using its already-held handle; all reused case-alias temporaries produce strictly decodable new intents. Interrupted native regression tests reproduce the baseline failures, guards/age/identity remain safe, and full local/CI verification passes before merge.
      - Local [review repair evidence](docs/verification/P1.5.3g3-staging-review-hardening-2026-10-19.md): three added native cases reproduce both review defects before repair and now pass with all 106 focused and 1,029 canonical cases/native/probe/publishing checks in 121.65 seconds, exit 0 (local boundary fuzz skipped). Both fixes merged together with green CI and resolved review threads, audited below.
    - Main/CI acceptance for P1.5.3f and P1.5.3g1-g3 is recorded in the [PR #35 exact-tree merge audit](docs/verification/P1-staging-ci-merge-2026-10-19.md). All five required checks passed before merge; general case coordination remains open.
  - [ ] **P1.5.3h Case-clash preservation coordination** (`PeerSync.Engine/Sync/`, `PeerSync.Storage/FolderDb/`) [WIP @codex 2026-10-19]
    - Done when: concurrent live versions whose canonical key matches but leaf spelling differs remain visible through deterministic-winner/case-conflict mapping, independent of arrival order and local winner. Preserve all versions before vector merging hides concurrency; survive restart, source replacement, gossip and occupied destinations with authenticated reference coverage and no overwritten data. Ordinary causal case renames do not produce conflict copies. File/directory and nested mappings remain mandatory parent criteria.
    - [ ] **P1.5.3h4 Authenticated directory/subtree case copies** [WIP @codex 2026-10-19]
      - Done when: empty/nonempty/nested and file/directory clashes preserve every authenticated subtree member under stable case-copy destinations, with complete references, bounded recovery and normal RW/RO publication. Legacy absence never authorizes guessing a tree. Follow the [snapshot protocol design](docs/proposals/P1-directory-snapshot-protocol.md).
      - [ ] **P1.5.3h4a Directory baselines and snapshot wire foundations** [WIP @codex 2026-10-19]
        - Done when: connected directory failure baselines are retained; optional signed directory roots and canonical bounded node/envelope codecs preserve exact bytes, reject malformed inputs and pass focused/full verification. This foundation enables no directory copy mutations.
      - [ ] **P1.5.3h4b Bottom-up capture and graph reference lifecycle** [WIP @codex 2026-10-19]
        - Done when: exact child publications produce durable bounded snapshots without native-mtime feedback, metadata/file references survive replacements and restarts, incomplete graphs block sweeping, and nested convergence/streaming/cancellation checks pass.
        - Local [metadata/building](docs/verification/P1.5.3h4b-directory-metadata-2026-10-19.md) and [durable graph/reference/retirement](docs/verification/P1.5.3h4b-directory-graph-storage-2026-10-19.md) checkpoints: bounded canonical captures, restartable authenticated traversal, descendant references and fail-closed sweeping pass focused checks; all 224 Storage and 71 focused Engine lifecycle cases pass. Persistent capture continuation, scanner publication, native subtree materialization and full/CI acceptance remain open; the five directory case baseline failures are retained.
      - [ ] **P1.5.3h4c Guarded directory/member application and recovery**
        - Done when: root/source/member/native-bound no-overwrite journals preserve every visible version, complete only after publication and pass arrival-order/gossip/late-page/crash/reference/collision/native regressions plus the full parent criteria.
    - [ ] **P1.5.3h1 Connected regression baseline and admission design** [WIP @codex 2026-10-19]
      - Done when: baseline reproduces empty/existing-receiver failures under both arrival orders and proves causal rename controls; a concrete durable source/root/reference and first-name/gossip/recovery design is recorded before mutation implementation.
      - Local [baseline](docs/verification/P1.5.3h1-case-clash-baseline-2026-10-19.md): four connected preservation cases fail because only one visible version is present; two causal case-rename controls pass. Probes use isolated build outputs. At that baseline checkpoint, admission and recovery/reference design were incomplete and case mutations were disabled; the approved policy and later implementation checkpoints follow.
      - User [approved the deterministic winner policy](docs/proposals/P1-case-clash-name-policy.md) on 2026-10-19. The source design now specifies FULL atomic source admission, portable names, pending barriers, reference/expiry coverage and recoverable publication; implementation remains WIP.
    - [ ] **P1.5.3h2 Durable case-source admission and policy** [WIP @codex 2026-10-19]
      - Done when: verified maximal distinct-leaf sources have portable stable IDs/names, frozen root-bound ledger entries commit atomically with remote rows/needed/cursor, restart and row replacement preserve sources, replay is idempotent and a 1,000-pending bound fails the whole update without data loss. Pure policy, storage rollback/migration and expiry/reference checks pass. This child alone does not enable filesystem case mutations or close the parent.
      - Local [admission checkpoint](docs/verification/P1.5.3h2-case-admission-2026-10-19.md): all 211 Storage and 26 focused Engine policy/reference cases pass, with exact pending-source retention and transactional rollback. This prerequisite checkpoint preceded h3 admission/materialization; its four expected connected failures are repaired in the h3 checkpoints below. Main/CI and parent acceptance remain pending.
    - [ ] **P1.5.3h3 Guarded case materialization and publication barriers** [WIP @codex 2026-10-19]
      - Done when: original-path resolution waits through concurrent receipt, verified no-overwrite copies recover at every boundary, changed/foreign occupants survive, RW scanning publishes new paths and RO copies remain local. Complete source markers prevent gossip duplication; connected baseline plus restart/collision/reference/causal controls pass. Directory/nested integration remains mandatory before parent closure.
      - First slice: regular-file kind-7 source/root/destination/GUID/FID recovery; FULL installed-but-pending accounting permits scanning, then guarded local-publication proof completes the obligation. Directory sources stay pending until subtree integration; no parent closure from file-only coverage.
      - Local [regular-file checkpoint](docs/verification/P1.5.3h3-file-case-copies-2026-10-19.md): four baseline failures now pass with two causal controls and fifteen native additions. All 1,113 canonical cases/native/probe/publishing pass in 123.20 seconds, exit 0 (boundary fuzz skipped). Gossip/retry/fairness/cancellation, completed metadata retirement, directory/subtree integration and CI/main remain open.
      - [ ] **P1.5.3h3a File-case gossip, retries and completed metadata** [WIP @codex 2026-10-19]
        - Done when: an already published authenticated copy satisfies guarded visible evidence without claiming ownership; unknown/changed objects remain collisions. Per-source root-bound retry deadlines survive restart/clock rollback, independent paths progress and cancellation recovers safely. Bounded 24-hour metadata compaction preserves compact completed IDs, pending envelopes and foreign/future records. Gossip/retry/restart/native/storage regressions and canonical checks pass; main/CI remain mandatory.
        - Local [lifecycle verification](docs/verification/P1.5.3h3a-case-gossip-lifecycle-2026-10-19.md): gossip/reparse baselines fail before repair; all 30 focused Engine and nine Storage cases pass, including case-only hosted timer wake, cancellation and hidden/read-only recovery. All 1,125 canonical cases/native/probe/publishing pass in 120.11 seconds, exit 0 (boundary fuzz skipped). Directory/subtree mapping, full phase-gate simulations and CI/main remain open.
      - [ ] **P1.5.3h3b Capacity, forwarding, recovery and RO review hardening** [WIP @codex 2026-10-19]
        - Include partially occupied capacity: reconstruction commits a valid prefix before reporting saturation, failing-path partial sources/needed changes roll back, and ordinary application stays blocked until repair finishes. Remote receipt remains atomic. Add focused storage and real coordinator regressions.
        - [Partial-capacity follow-up](docs/verification/P1-case-capacity-ci-partition-2026-10-19.md): both added baselines fail before repair, then 13 focused Storage/27 native case-recovery cases and 1,134 full canonical cases pass. Valid signed unavailable sources remain retained while new work drains; noncapacity failures/cancellation still roll back the current batch.
        - [PR #36 merge audit](docs/verification/P1-case-ci-merge-2026-10-19.md): all five final-head checks pass within 15 minutes, four review threads are resolved and tested/main trees match. Main-push checks and complete parent acceptance remain pending at this checkpoint.
        - Done when: a 1,001-path startup case backlog drains through the coordinator without losing obligations or applying unrepaired originals; current authenticated forwarding peers serve retained sources after hint disconnection; prepared reparse destinations preserve occupants and release recovery; settled RO case work and timers drain. Reproduce all four baselines and pass focused/canonical/CI checks before merge.
        - Local [review verification](docs/verification/P1.5.3h3b-case-review-hardening-2026-10-19.md): all four findings fail before repair and pass afterward. All 57 focused Engine, 215 Storage and 1,130 canonical cases/native/probe/publishing pass in 134.67 seconds, exit 0 (boundary fuzz skipped). Final-head CI/main and thread resolution remain pending; directory/subtree and phase-gate work stay open.
- [x] **P1.5.4 Archive manager** (`PeerSync.Engine/Archive/ArchiveManager.cs`) (#23, #24)
  - Done when: archived files go to `.psp\archive\<relative dir>\<name>~20261018-142233.<ext>`, with a `-2` suffix on collisions. Daily pruning applies the age limit (30 days) and the volume cap (10%). Restores go through the normal "local change" path.
  - [x] **P1.5.4a Archive placement and retention** (`PeerSync.Engine/Archive/ArchiveManager.cs`) (#23)
    - Done when: the file applier uses the shared mirrored archive naming/collision policy; guarded indexed cleanup applies age and hosting-volume cap, reconciles interrupted missing rows, preserves unknown/changed/locked/reparse files, waits for recovery and schedules daily success with bounded failure retries. A blocked oldest version defers cap-only eviction of newer versions. Tests exercise native deletion, interruption and restart, capacity accounting, time boundaries and redacted diagnostics.
    - Merged with [local and CI verification](docs/verification/P1.5.4a-archive-retention-2026-10-19.md): 27 archive cases and all 180 Engine cases pass, plus all five required CI jobs. Restores remain a separate incomplete requirement.
  - [x] **P1.5.4b Restore through the local-change path** (`PeerSync.Engine/Archive/ArchiveRestorer.cs`, shared `Sync/FileApplier.cs`) (#24)
    - Done when: restore and restore-as-copy preserve current versions, use recoverable no-overwrite installation and re-enter local version generation, including RO policy; archived signatures are not republished as a fresh local edit. Actual UI remains P5.5. Failure/restart and concurrent target changes are covered by integration tests.
    - Merged with [local and CI verification](docs/verification/P1.5.4b-archive-restore-2026-10-19.md): 32 restore cases, normal RW/RO scanning, shared recovery and reused-staging inheritance regression pass, along with all five required CI jobs. Actual UI remains P5.5.
- [ ] **P1.5.5 Clock-skew guard** (`PeerSync.Engine/Sync/ClockGuard.cs`) [WIP @Codex 2026-10-19]
  - Done when: `Hello` carries UTC time. A peer more than 10 minutes off exchanges no index updates and shows "clock differs by N minutes" in the UI.
  - [x] **P1.5.5a UTC Hello and clock decision/status model** (`PeerSync.Protocol/`, `PeerSync.Engine/Sync/ClockGuard.cs`) (#34)
    - Done when: UTC Hello round-trips; missing/invalid/extreme samples, both offset signs, exact limit, elapsed time, local clock jumps, reset, frozen inputs and safe transition diagnostics are tested. This foundation alone does not authorize peer exchange or close the parent.
    - Foundation [verification](docs/verification/P1.5.5a-clock-foundation-2026-10-19.md) and [final merge audit](docs/verification/P1-clock-transport-ci-merge-2026-10-19.md): all 970 final cases, both Server variants and required boundary fuzz pass, with identical submitted/tested/main trees. Real authenticated transport/UI integration remains required by the parent.
  - [ ] **P1.5.5b Guard peer index admission** (`PeerSync.Engine/`, `PeerSync.Net/`) [WIP @Codex 2026-10-19]
    - Done when: authenticated peer sessions check current clock admission before incoming and outgoing index exchange; skewed or missing samples exchange no index data and recovery after a valid Hello is tested using production engine/transport integration.
    - Local [facade/transaction evidence](docs/verification/P1.5.5b-peer-index-admission-2026-10-19.md): eleven Engine, four Storage and three Core additions pass with all 935 canonical tests/native/probe/publishing in 110.20 seconds, exit 0 (CI fuzz skipped locally). Pure checks under writer/reference gates, rollback, identity binding, immutable pages and private trace/session diagnostics are covered. Production transport wiring, UI and main/CI remain pending.
  - [ ] **P1.5.5c Present clock status in the control UI** (`PeerSync.App/`) [WIP @codex 2026-10-19]
    - Done when: the real control UI presents the peer clock reason and numeric minute difference from service status, and clears it on recovery. Remains required before the parent closes.
    - [ ] **P1.5.5c1 Typed session status source and lifecycle** (`PeerSync.Core/`, `PeerSync.Engine/`) [WIP @codex 2026-10-19]
      - Done when: both endpoints calculate their own signed Hello offset, status snapshots bind authenticated peer/session identity, disconnect resets admission while preserving the display reason, reconnect clears old closed warnings, and bounded history/truncation/immutability and connected regressions pass. This source alone does not close the rendered UI requirement.
      - Local [source verification](docs/verification/P1.5.5c1-clock-status-source-2026-10-19.md): both baseline offset-sign cases fail before repair; five Core and fifteen Engine additions now pass with all 1,049 canonical cases/native/probe/publishing in 113.88 seconds, exit 0 (boundary fuzz skipped locally). Closed reasons, immutable identity, forged Hello rejection, reconnect/polling, history/page bounds and admission separation are covered. Control delivery, rendered UI, production transport and CI/main remain pending.
    - [ ] **P1.5.5c2 Control status delivery and rendered UI** (`PeerSync.Control.Contracts/`, `PeerSync.Service/`, `PeerSync.App/`) [WIP @codex 2026-10-19]
      - [PR #37 merge audit](docs/verification/P1-control-ci-merge-2026-10-19.md): all five final-head and main-push checks pass; the retention review is resolved and tested/main trees match. Runtime server, rendered UI and shared retention children meet acceptance. Actual installed PeerSync integration and production authenticated transport keep the client child and parent open.
      - Done when: the real control pipe delivers the runtime status source and the app renders reason/minute difference, missing/invalid/disconnected states and recovery without stale updates. Verify real pipe lifecycle/authentication and rendered controls; integrate with production authenticated transport before the parent closes.
      - Implementation constraints: service-instance/sequence and client-generation guards; at most 1,000 rows/256 KiB and bounded subscriptions; real runtime snapshots with production availability explicit. Only the server gets pipe-instance creation rights; identify callers and verify both configured service owner SID and kernel server PID. Test real pipe cancellation/reconnect/identity failures and retain actual WPF render evidence. Full service installation/configuration/control operations remain later mandatory tasks.
      - Local [contract checkpoint](docs/verification/P1.5.5c2-control-contract-2026-10-19.md): 28 protobuf/mapping cases and all 1,077 canonical cases/native/probe/publishing pass in 131.56 seconds, exit 0 (boundary fuzz skipped). Real pipe/client authentication, rendered UI, production transport and main/CI remain pending.
      - [ ] **P1.5.5c2a Installed endpoint and verified pipe client** [WIP @codex 2026-10-19]
        - Done when: fixed service/pipe SCM lookup reads a running PID and configured account SID with bounded native buffers; every connection checks owner and kernel server PID before RPC bytes. Missing/stopped/wrong-owner/wrong-PID/cancellation and actual pipe lifecycle tests pass, with internal fixture seams and no production arbitrary endpoint override.
        - Local [client checkpoint](docs/verification/P1.5.5c2a-control-client-2026-10-19.md): eleven new native checks and all 39 contract cases pass; positive owner/PID bytes, zero-byte rejection, SCM reads and bounded cancellation/deadlines are verified. Actual service-host integration, UI and canonical/CI/main acceptance remain pending.
      - [x] **P1.5.5c2b Runtime status server and event lifecycle** (#37)
        - Done when: actual engine snapshots bind derived folder IDs, empty hosting stays explicitly unavailable for production sync, only server identity creates instances and each RPC checks caller token membership. Named-pipe-only GetStatus/WatchEvents enforce frame/row/subscription bounds, backpressure and cancellation; native runtime/stream/authentication tests pass.
        - Local [server checkpoint](docs/verification/P1.5.5c2b-control-server-2026-10-19.md): eight native service tests, all 39 contract tests and 1,149 canonical cases/native/probe/publishing pass in 139.00 seconds, exit 0 (boundary fuzz skipped). Live runtime skew/recovery, ACL/caller verification, truncation, subscription capacity and shutdown are verified. Installed production supervision, rendered UI and CI/main remain pending.
      - [x] **P1.5.5c2c Rendered clock status and reconnect protection** (#37)
        - Done when: the app uses the verified installed-service client, displays numeric signed minutes/reasons and connection/truncation/unavailable states, clears stale success on failure/recovery and rejects stale generations/epochs/sequences. Retain inspected real WPF render evidence and test reconnect/cancellation before parent closure.
        - Local [rendered UI checkpoint](docs/verification/P1.5.5c2c-clock-ui-2026-10-19.md): all 13 App cases and 1,166 canonical cases/native/probe/publishing pass in 139.56 seconds, exit 0 (local boundary fuzz skipped). Actual engine/pipe/client/WPF skew/recovery/failure, replay guards, bounded reconnect, read cancellation/drain and sanitized identity diagnostics pass; eight actual WPF PNGs were inspected. Installed production transport/supervision and CI/main acceptance remain pending.
      - [x] **P1.5.5c2d Shared debug-run retention** (`PeerSync.Core/Diagnostics/`, app/service startup) (#37)
        - Done when: repeated app/service runs obey shared eligible inactive age/count/byte limits while active writers remain usable; native pinned directory identities and no-reparse/concurrent guards protect unknown/locked/linked entries and block guarded ancestor/run replacement. Reproduce the run-directory leak, verify startup/periodic/close cleanup and sanitized pressure reporting, then pass full local/CI/main checks before merging.
        - Local [retention review checkpoint](docs/verification/P1-control-retention-review-2026-10-19.md): baseline leaves twelve directories; ten native repairs cover finished-run count, shared bytes/expiry, active writers, real cross-process lease protection, concurrent cleanup, unknown/locked/reparse/hardlink preservation and pinned directory replacement. All 1,176 canonical cases/native/probe/publishing pass in 167.36 seconds, exit 0 (local boundary fuzz skipped); final-head CI/main and review resolution remain pending.

### 7.10 Code: resolving concurrent versions (P1.5.1, P1.5.2)

The real resolver supersedes the timestamp comparison below: it follows the
source design's live mtime rule and uses changed_ticks for tombstones. Maximal
candidate selection, stable metadata ties and exact-body preservation are also
required for multi-peer convergence.

**Tricky:**

- Each peer acts only on the copy it holds. When the remote version wins, we archive our local copy. When ours wins, there's nothing to archive here: the other peer archives its own copy when our winner reaches it.
- The merged record must be deterministic (same body on every RW peer), so the deterministic Ed25519 signature is also identical and the vectors converge instead of ping-ponging.

```csharp
// src/PeerSync.Engine/Sync/ConflictResolver.cs
public enum Outcome { Ignore, ApplyRemote, KeepLocal, MergeVectorsOnly }
public enum LoserFate { None, Archive, ConflictCopy }
public sealed record Resolution(Outcome Outcome, LoserFate LocalLoserFate, Dictionary<ulong, ulong> Vv);

public static Resolution Resolve(VersionView local, VersionView remote, FolderSettings settings)
{
    switch (VersionVector.Compare(remote.Vv, local.Vv))
    {
        case VvOrder.Dominates:
            return new(Outcome.ApplyRemote, FateOf(local, settings), remote.Vv);
        case VvOrder.DominatedBy:
        case VvOrder.Equal:
            return new(Outcome.Ignore, LoserFate.None, local.Vv);
    }

    var merged = VersionVector.Merge(local.Vv, remote.Vv);   // concurrent from here on
    if (local.Deleted == remote.Deleted && local.FileHash.AsSpan().SequenceEqual(remote.FileHash))
        return new(Outcome.MergeVectorsOnly, LoserFate.None, merged);

    bool remoteWins = remote.ChangedTicks != local.ChangedTicks
        ? remote.ChangedTicks > local.ChangedTicks
        : remote.ModifiedBy.AsSpan().SequenceCompareTo(local.ModifiedBy) > 0;

    return remoteWins
        ? new(Outcome.ApplyRemote, FateOf(local, settings), merged)
        : new(Outcome.KeepLocal, LoserFate.None, merged);
}

static LoserFate FateOf(VersionView loser, FolderSettings s) =>
    loser.Deleted ? LoserFate.None
    : s.KeepBothCopies || loser.Size > s.ArchiveMaxFileBytes ? LoserFate.ConflictCopy
    : LoserFate.Archive;
```

### 7.11 Code: applying a downloaded file safely (P1.5.3)

The example below is superseded by the user-approved recoverable protocol in
source design apply step 5 (2026-10-19). It is historical pseudocode, not the
implementation contract. Use root-relative guarded handles and FULL-durable
intents, retain the old version without overwriting, then atomically install
the verified temporary file into the vacant target. External applications may
observe the brief missing-target interval. Recover before scanning, preserve
unknown targets, and commit the index/archive state and intent removal together.
Temporary reuse requires persisted root/file ownership plus byte verification;
never write or delete an unowned file merely because its name ends in `.!psp`.

**Tricky:**

- Assemble next to the target, so a new file inherits that directory's ACL. A file moved from elsewhere on the volume keeps its old ACL.
- Use `File.Replace` for existing targets: it keeps the target's ACL and attributes and moves the old version to the archive in the same step.
- `File.Replace` fails on read-only targets: clear the flag first, then re-apply attributes.
- Re-check the target right before swapping, or a user's edit made during the download is silently lost.

```csharp
// src/PeerSync.Engine/Sync/FileApplier.cs
public async Task<ApplyResult> ApplyAsync(FileRecordBody rec, string target, CancellationToken ct)
{
    string tmp = target + ".!psp";
    await assembler.AssembleAsync(rec, tmp, ct);                 // writes and verifies every chunk, then the whole-file hash
    File.SetAttributes(tmp, FileAttributes.Hidden);
    File.SetLastWriteTimeUtc(tmp, new DateTime(rec.MtimeTicks, DateTimeKind.Utc));

    var known = await db.GetLocalAsync(PathRules.Key(rec.Path), ct);
    var current = new FileInfo(target);
    if (current.Exists && !Matches(current, known))
    {
        File.Delete(tmp);
        await scanner.RescanPathAsync(target, ct);                // the user changed it: resolve again first
        return ApplyResult.Deferred;
    }

    try
    {
        if (current.Exists)
        {
            if (current.IsReadOnly) current.IsReadOnly = false;
            string archivePath = archive.ReserveSlot(rec.Path, DateTime.UtcNow);   // inside .psp\archive, same volume
            Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
            File.Replace(tmp, target, archivePath, ignoreMetadataErrors: true);
            await archive.RecordAsync(rec.Path, archivePath, ct);
        }
        else
        {
            File.Move(tmp, target);
        }
    }
    catch (IOException ex) when (IsLockViolation(ex))
    {
        return ApplyResult.RetryLater;                            // the user has the file open: backoff 10 s to 10 min
    }

    File.SetLastWriteTimeUtc(target, new DateTime(rec.MtimeTicks, DateTimeKind.Utc));   // times first, read-only flag last
    var attrs = (FileAttributes)rec.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden);
    File.SetAttributes(target, attrs == 0 ? FileAttributes.Normal : attrs);              // also clears the temp file's Hidden
    await db.RecordAppliedAsync(rec, new FileInfo(target), ct);
    return ApplyResult.Applied;
}

static bool IsLockViolation(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;   // sharing or lock violation
```

On start-up, stale `*.!psp` files whose record is still needed are kept: their verified chunks are reused. Other stale temp files older than 24 hours are deleted.

### P1.6 Simulation harness

- [x] **P1.6.1 Transport abstraction and in-memory transport** (`PeerSync.Net/InMemory/`) (#34)
  - Done when: the engine talks only to `IPeerTransport`. The in-memory implementation supports injected latency, drops and partitions.
  - [x] **P1.6.1a Bounded identity-bound in-memory fabric** (#34)
    - Done when: connection-scoped handlers, isolated identity/message ownership, independent bounded requests, directional latency/drop/partition injection and cancel/drain/disconnect behavior have executable tests and canonical CI coverage.
  - [x] **P1.6.1b Connected engine integration** (#34)
    - Done when: real folder indexes, Hello clocks, bounded range/page synchronization and verified chunk serving/pulling use IPeerTransport, with actual file transfer, partition/reconnect and skew-blocking tests. No raw remote callback bypass in this connected path. Parent remains open until all connected engine integration is proven.
  - Merged with [final CI/main audit](docs/verification/P1-clock-transport-ci-merge-2026-10-19.md): all connected peer exchanges in TransportFolder use IPeerTransport; callback appliers remain trusted local primitives as specified by the design. Actual TLS/folder authentication, rendered clock UI and the separate multi-node property gate remain open.
  - [x] **P1.6.1c Connected-session review hardening** (#34)
    - Done when: growth beyond an announced target retains the true range barrier; disconnect/fresh-Hello cannot revive old queued admission; peer cancellation/disposal permits independent work and durable retry while caller cancellation remains intact; all folder disposal callers await the same drain/failure. Focused regressions and canonical/main/CI evidence pass.
    - [Repair evidence](docs/verification/P1.6.1c-connected-session-hardening-2026-10-19.md): baseline reproduces seven failures; all 52 focused and 970 canonical cases pass after twelve additions. All five final CI jobs pass, threads are resolved, and actual main matches the tested tree; see the merge audit above.
- [ ] **P1.6.2 Multi-node property tests** (`tests/PeerSync.Simulation`) [WIP @codex 2026-10-19]
  - Done when: `CsCheck` generates random edits, renames, deletes, partitions and heals on 3 to 7 nodes, each with a temp folder. After the network heals, all trees are identical. Every overwritten version exists in some node's archive or as a conflict copy. 1,000 seeds run in CI, and failing seeds are saved as regression tests.
  - [x] **P1.6.2a Reproducible real-engine scenarios and loss oracle** (#34)
    - Done when: generated/replayed operation traces use actual engine folders, heal to byte-identical visible trees and verify preservation from current/archive/conflict files. Save failure evidence and promote found regressions. Initial bounded smoke does not close the parent or phase gate.
    - [Simulation evidence](docs/verification/P1.6.2a-real-engine-simulation-2026-10-19.md) and [merged audit](docs/verification/P1-clock-transport-ci-merge-2026-10-19.md): initial generated scenarios, saved checker regression and native loss-oracle negative control pass locally and in final green CI, with an eight-scenario smoke. Expanded coverage, 1,000 CI cases, five consecutive days and the parent gate remain pending.
    - An additional local 1,000-case batch on the first PR #34 head passes: 9,807 versions, 500 actual renames and 1,001 actual deletions, all converged/preserved. This is initial flat-path evidence before the review repairs, not CI/five-day/100k-file gate evidence.
    - A [fresh local 1,000-case batch](docs/verification/P1-simulation-current-head-1000-2026-10-19.md) on submitted PR #35 head passes after the review/hierarchy/staging changes: 1,000 distinct seeds, 9,696 ledger versions, 508 actual renames and 940 actual deletions; exit 0 in 12m42s. Flat-path coverage remains separate from expanded scenarios, 1,000 CI cases, five consecutive days and the 100k-file gate.

### Gate P1

- [ ] All P1 tasks ticked.
- [ ] Five simulated nodes converge on a 100,000-file corpus with zero lost versions (attach the report).
- [ ] Simulation runs green for 1,000 seeds in CI for 5 consecutive days.

---

## 8. Phase 2: LAN sync as a service (weeks 9-13)

Goal: real PCs on one LAN, the engine inside a Windows service, and a CLI to drive it.

### P2.1 Protocol

- [ ] **P2.1.1 `.proto` files and frame codec** (`PeerSync.Protocol/`)
  - Done when: every message in the design doc's message table is defined. Frames are `varint length ‖ type byte ‖ body`, with a 16 MiB maximum. The codec handles frames split across reads (8.1) and is fuzzed.
- [ ] **P2.1.2 Message validation** (`PeerSync.Protocol/Validation/`)
  - Done when: limits are enforced: 1,000 records per `IndexUpdate`, 64 block requests per frame, path rules on every record. A violation closes the connection with a reason code and adds a strike against that DeviceId.

### 8.1 Code: frame reader over `PipeReader` (P2.1.1)

**Tricky:** `AdvanceTo(consumed, examined)` must mark *everything* as examined when a frame is incomplete. Otherwise `ReadAsync` returns immediately with the same bytes and spins at 100% CPU.

```csharp
// src/PeerSync.Protocol/Framing/FrameReader.cs
public const int MaxFrameBytes = 16 * 1024 * 1024;

public static async IAsyncEnumerable<(byte Type, ReadOnlySequence<byte> Body)> ReadFramesAsync(
    PipeReader reader, [EnumeratorCancellation] CancellationToken ct)
{
    while (true)
    {
        ReadResult result = await reader.ReadAsync(ct);
        ReadOnlySequence<byte> buffer = result.Buffer;
        while (TryReadFrame(ref buffer, out byte type, out var body))
            yield return (type, body);                       // consumers must copy or parse before the next MoveNext
        reader.AdvanceTo(consumed: buffer.Start, examined: buffer.End);
        if (result.IsCompleted)
        {
            if (!buffer.IsEmpty) throw new ProtocolException("Connection closed mid-frame.");
            yield break;
        }
    }
}

static bool TryReadFrame(ref ReadOnlySequence<byte> buffer, out byte type, out ReadOnlySequence<byte> body)
{
    type = 0; body = default;
    var r = new SequenceReader<byte>(buffer);
    if (!TryReadVarint(ref r, out ulong length)) return false;
    if (length > MaxFrameBytes) throw new ProtocolException($"Frame of {length} bytes exceeds the limit.");
    if (r.Remaining < (long)length + 1) return false;        // +1 for the type byte
    r.TryRead(out type);
    body = buffer.Slice(r.Position, (long)length);
    buffer = buffer.Slice(body.End);
    return true;
}

static bool TryReadVarint(ref SequenceReader<byte> r, out ulong value)
{
    value = 0;
    for (int shift = 0; shift < 35; shift += 7)                // lengths fit in 5 bytes
    {
        if (!r.TryRead(out byte b)) return false;              // need more data
        value |= (ulong)(b & 0x7F) << shift;
        if ((b & 0x80) == 0) return true;
    }
    throw new ProtocolException("Varint too long.");
}
```

### P2.2 Transport and handshake

- [ ] **P2.2.1 QUIC transport** (`PeerSync.Net/Quic/`)
  - Done when: the listener and dialler from 6.1 run with stream 0 for control, one stream per folder index, and a pool of block-request streams. Connections close cleanly with `Close {reason}`.
- [ ] **P2.2.2 TCP plus TLS 1.3 fallback** (`PeerSync.Net/Tcp/`)
  - Done when: TCP listens on the same port number as QUIC. Each peer gets one control connection plus up to 3 data connections, each with its own `SslStream` and the same certificate rules as QUIC.
  - Data connections attach with a 32-byte session ticket issued on the control connection, and must present the same DeviceId. This avoids writing a stream multiplexer.
- [ ] **P2.2.3 Handshake** (`PeerSync.Net/Handshake/`)
  - Done when: `Hello` is exchanged, then `FolderAuth` with the direction-bound proofs in 8.2, the RO proof and the RW signature. Pinned DeviceIds are checked. A reflected proof fails (test). A MITM test with a different certificate fails.
  - Ref: Protocol specification, Connection and handshake.
- [ ] **P2.2.4 Connection manager** (`PeerSync.Net/Connections/ConnectionManager.cs`)
  - Done when: dialling follows happy eyeballs (LAN addresses, then public, then punch, then relay, 250 ms apart). The first authenticated connection wins. Duplicates are resolved as in 8.3. Reconnects back off from 1 s to 5 min.

### 8.2 Code: direction-bound folder proof (P2.2.3)

**Tricky:**

- If both sides compute the same MAC, an attacker can echo a peer's own proof back to it. Put the sender first.
- Take both DeviceIds from the TLS certificates, never from `Hello`.
- Compare with `FixedTimeEquals`.

```csharp
// src/PeerSync.Net/Handshake/FolderProof.cs
public static byte[] Compute(byte[] key, ReadOnlySpan<byte> label,
    ReadOnlySpan<byte> senderDeviceId, ReadOnlySpan<byte> receiverDeviceId,
    ReadOnlySpan<byte> senderNonce, ReadOnlySpan<byte> receiverNonce, ReadOnlySpan<byte> folderId)
{
    using var h = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
    h.AppendData(label);              // "psp1 auth" with K_auth, "psp1 ro-auth" with K_ro_auth
    h.AppendData(senderDeviceId);
    h.AppendData(receiverDeviceId);
    h.AppendData(senderNonce);
    h.AppendData(receiverNonce);
    h.AppendData(folderId);
    return h.GetHashAndReset();
}

public static bool Verify(byte[] key, ReadOnlySpan<byte> label, PeerContext ctx, ReadOnlySpan<byte> folderId, ReadOnlySpan<byte> received)
{
    // ctx.RemoteDeviceId comes from QuicConnection.RemoteCertificate / SslStream.RemoteCertificate.
    byte[] expected = Compute(key, label, ctx.RemoteDeviceId, ctx.LocalDeviceId, ctx.RemoteNonce, ctx.LocalNonce, folderId);
    return CryptographicOperations.FixedTimeEquals(expected, received);
}
```

### 8.3 Duplicate connections (P2.2.4)

When two peers dial each other at the same moment, both connections may authenticate. Keep the one **initiated by the device with the lower DeviceId** and close the other with reason `Duplicate`. Both sides apply the same rule, so they keep the same connection without negotiating.

### P2.3 LAN discovery and predefined hosts

- [ ] **P2.3.1 LAN multicast announce and listen** (`PeerSync.Net/Discovery/LanDiscovery.cs`)
  - Done when: announcements go to `239.255.80.83:47820` and `[ff12::5053]:47820` on every up, non-loopback interface, plus subnet broadcast. Packets carry the hourly-rotating HMAC tags. Interfaces are re-joined on `NetworkAddressChanged`. Two PCs find each other within 5 s of start.
  - Code: 8.4.
- [ ] **P2.3.2 Predefined hosts** (`PeerSync.Net/Discovery/PredefinedHosts.cs`)
  - Done when: each `host:port` from folder settings is resolved every 60 s and dialled.

### 8.4 Code: multicast on every interface (P2.3.1)

**Tricky:**

- Binding to `IPAddress.Any` and joining once only receives on the default interface. Join the group per interface.
- When sending, set `IP_MULTICAST_IF` per interface. For IPv4 the interface index is given in network byte order.
- Keep TTL 1 so packets never leave the LAN.

```csharp
// src/PeerSync.Net/Discovery/LanSocket.cs
static readonly IPAddress Group4 = IPAddress.Parse("239.255.80.83");
const int Port = 47820;

public static Socket OpenIPv4()
{
    var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = false };
    s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
    s.Bind(new IPEndPoint(IPAddress.Any, Port));
    s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
    foreach (int index in UsableIPv4InterfaceIndexes())
        s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group4, index));
    return s;
}

public static async Task AnnounceAsync(Socket s, ReadOnlyMemory<byte> packet, CancellationToken ct)
{
    foreach (int index in UsableIPv4InterfaceIndexes())
    {
        s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, IPAddress.HostToNetworkOrder(index));
        await s.SendToAsync(packet, SocketFlags.None, new IPEndPoint(Group4, Port), ct);
    }
}

static IEnumerable<int> UsableIPv4InterfaceIndexes() =>
    NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up
                 && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                 && n.SupportsMulticast)
        .Select(n => n.GetIPProperties().GetIPv4Properties())
        .Where(p => p is not null)
        .Select(p => p!.Index);
```

### P2.4 Index exchange and block transfer

- [ ] **P2.4.1 Index exchange** (`PeerSync.Engine/Sync/IndexExchange.cs`)
  - Done when: on connect, `IndexSummary` is followed by `IndexRequest {afterSeq}`. Updates stream in batches of 1,000. Live changes are pushed as they happen. Interrupted exchanges resume from `last_seq_received`.
- [ ] **P2.4.2 Block server** (`PeerSync.Engine/Transfer/BlockServer.cs`)
  - Done when: chunk hash → (path, offset) via the `chunks` table, read with `RandomAccess.ReadAsync`. Requests for unknown hashes or ignored paths return an error code. Per-peer request quotas apply.
- [ ] **P2.4.3 Transfer scheduler** (`PeerSync.Engine/Transfer/TransferScheduler.cs`)
  - Done when: missing chunks are spread across every peer that has them. Each peer has an adaptive in-flight window (4 MiB, up to 32 MiB). Endgame mode is on for the last chunks. Single-chunk files are batched 64 per request. Rate limits use 8.5. LAN transfer reaches the P0.2.4 target.

### 8.5 Code: rate limiting large chunks (P2.4.3)

**Tricky:** `TokenBucketRateLimiter.AcquireAsync` throws if `permitCount` is above `TokenLimit`. A 2 MiB chunk under a 1 MB/s limit would crash. Acquire in slices.

```csharp
// src/PeerSync.Engine/Transfer/Throttle.cs
public static RateLimiter? Create(int bytesPerSecond) => bytesPerSecond <= 0 ? null :
    new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
    {
        TokenLimit = Math.Max(bytesPerSecond / 4, Slice),          // at most 250 ms of burst
        TokensPerPeriod = Math.Max(bytesPerSecond / 10, 1),
        ReplenishmentPeriod = TimeSpan.FromMilliseconds(100),
        QueueLimit = int.MaxValue,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        AutoReplenishment = true,
    });

const int Slice = 64 * 1024;

public static async ValueTask ThrottleAsync(RateLimiter? limiter, int bytes, CancellationToken ct)
{
    if (limiter is null) return;
    for (int left = bytes; left > 0; left -= Slice)
    {
        using RateLimitLease lease = await limiter.AcquireAsync(Math.Min(left, Slice), ct);
        if (!lease.IsAcquired) throw new InvalidOperationException("Rate limiter rejected the request.");
    }
}
```

### P2.5 Service host

- [ ] **P2.5.1 `Program.cs` and hosted services** (`PeerSync.Service/Program.cs`)
  - Done when: the service runs both as a console app (for debugging) and as a Windows service. Power events reach the engine. Stop finishes within 15 s.
  - Code: 8.6.
- [ ] **P2.5.2 `--init` command** (`PeerSync.Service/Init/InitCommand.cs`)
  - Done when, run elevated, it creates:
    - the data folders with the ACLs from 5.1
    - the device certificate from 5.2, with its thumbprint stored
    - a default `settings.json` with a free random port (20000 to 60000, checked against active TCP and UDP listeners)
    - the SID of the account running Setup (`WindowsIdentity.GetCurrent().User`) in `ControlUserSids`. If an administrator installed it for someone else, that person uses "Authorize this account" (P3.2.6).
    - the `PeerSync` Windows Event Log source
  - It is idempotent: a second run changes nothing and exits 0.
- [ ] **P2.5.3 `--remove-data` command** (`PeerSync.Service/Init/RemoveDataCommand.cs`)
  - Done when: it stops the service if running (waiting up to 30 s), deletes `%ProgramData%\PeerSync` and the device certificate with its key. It never touches synced folders.
- [ ] **P2.5.4 Settings store and folder-key protection** (`PeerSync.Service/Settings/`, `Secrets/`)
  - Done when: section 4 is implemented, and a corrupt `settings.json` recovers from `.bak` (test).
- [ ] **P2.5.5 Network change and resume** (`PeerSync.Service/Events/`)
  - Done when: after sleep and resume, or a Wi-Fi switch, peers reconnect and folders rescan within 30 s.

- [ ] **P2.5.6 Deployment diagnostics and bundle command** (`PeerSync.Service/Diagnostics/`, `PeerSync.Cli/Diagnostics/`)
  - Done when: debug events cover service startup/shutdown, configuration recovery, certificate loading, discovery/dial/fallback/retry, folder auth/approval, index/block verification, apply/defer/archive and filesystem failures, using P0.1.5's safe schema. Session IDs join both endpoints' logs. `psp diagnostics` produces a sanitized bundle; the runbook reproduces a two-PC failure and identifies its cause. Keys, FolderIds, file contents and raw records are absent.

### 8.6 Code: service host (P2.5.1)

**Tricky:**

- `AddWindowsService` registers its own `IHostLifetime` only when running as a service. Register the replacement lifetime *after* it, and only under the same condition, or console debugging breaks.
- `CanHandlePowerEvent` must be set in the constructor, before the Service Control Manager starts the service.

```csharp
// src/PeerSync.Service/Program.cs
if (args is ["--init", ..]) return InitCommand.Run(args);
if (args is ["--remove-data"]) return RemoveDataCommand.Run();

new SettingsStore(Paths.SettingsFile, NullLogger<SettingsStore>.Instance).EnsureValidOrRestore();

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,      // services start in System32 otherwise
});

builder.Services.AddWindowsService(o => o.ServiceName = "PeerSync");
if (WindowsServiceHelpers.IsWindowsService())
    builder.Services.AddSingleton<IHostLifetime, PeerSyncServiceLifetime>();
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(15));

builder.Configuration.Sources.Clear();                // one source of truth on clients
builder.Configuration.AddJsonFile(Paths.SettingsFile, optional: false, reloadOnChange: true);
builder.Services.AddOptions<DeviceSettings>().Bind(builder.Configuration).ValidateDataAnnotations().ValidateOnStart();

builder.Services.AddSerilog((_, lc) => lc
    .MinimumLevel.Information()
    .WriteTo.File(Path.Combine(Paths.Logs, "peersync-.log"), rollingInterval: RollingInterval.Day,
                  retainedFileCountLimit: 7, fileSizeLimitBytes: 14_000_000, rollOnFileSizeLimit: true)
    .WriteTo.EventLog("PeerSync", manageEventSource: false, restrictedToMinimumLevel: LogEventLevel.Warning));

ControlPipe.Configure(builder);                       // 5.4: named pipe, PipeSecurity, HTTP/2 only
builder.Services.AddGrpc(o => o.Interceptors.Add<CallerIdentityInterceptor>());

builder.Services.AddSingleton<PowerEvents>();
builder.Services.AddHostedService<FolderManager>();
builder.Services.AddHostedService<ConnectionManager>();
builder.Services.AddHostedService<DiscoveryService>();
builder.Services.AddHostedService<TransferScheduler>();
builder.Services.AddHostedService<MaintenanceService>();

var app = builder.Build();
app.MapGrpcService<ControlService>();
await app.RunAsync();
return 0;
```

```csharp
// src/PeerSync.Service/Hosting/PeerSyncServiceLifetime.cs
[SupportedOSPlatform("windows")]
public sealed class PeerSyncServiceLifetime : WindowsServiceLifetime
{
    readonly PowerEvents power;

    public PeerSyncServiceLifetime(IHostEnvironment env, IHostApplicationLifetime app, ILoggerFactory loggers,
        IOptions<HostOptions> host, IOptions<WindowsServiceLifetimeOptions> service, PowerEvents power)
        : base(env, app, loggers, host, service)
    {
        CanHandlePowerEvent = true;
        this.power = power;
    }

    protected override bool OnPowerEvent(PowerBroadcastStatus status)
    {
        switch (status)
        {
            case PowerBroadcastStatus.Suspend: power.RaiseSuspending(); break;
            case PowerBroadcastStatus.ResumeAutomatic:
            case PowerBroadcastStatus.ResumeSuspend: power.RaiseResumed(); break;
        }
        return true;
    }
}
```

The event source `PeerSync` is registered by `--init` (`EventLog.CreateEventSource`), because the service account can't create it.

### P2.6 Control API skeleton and CLI

- [ ] **P2.6.1 Control API, first RPCs** (`PeerSync.Control.Contracts/control.proto`, `PeerSync.Service/Control/`)
  - Done when: `GetStatus`, `ListFolders`, `AddFolder`, `JoinFolder`, `ListPeers` and `WatchEvents` work over the pipe with 5.4 security.
- [ ] **P2.6.2 `psp.exe`** (`PeerSync.Cli/`)
  - Done when: `psp status`, `psp add <path>`, `psp join <key> <path>`, `psp peers <folder>` and `psp dump-index <folder>` work, and output is machine-readable with `--json`.

### Gate P2 (LAN beta)

- [ ] Complete deferred P0.2.1: a 1 GiB mutual-certificate QUIC transfer with ALPN `psp/1`, certificate-derived DeviceIds and verified hash between physical PCs on a 1-Gbit LAN; record link speeds and throughput, then close P0.2.1 with the evidence.
- [ ] Three physical PCs sync a 50 GB mixed corpus (large files, 100,000 small files, deep trees) over the LAN.
- [ ] 72-hour soak with random operations, sleep/resume and Wi-Fi switching: zero data loss, and the hourly tree comparisons all match (attach the report).
- [ ] The MITM and reflection tests from P2.2.3 pass in CI.

---

## 9. Phase 3: WPF app and installer (weeks 14-17)

Goal: a non-developer can install PeerSync and sync two PCs in under 10 minutes.

### P3.1 Control API completion

- [ ] **P3.1.1 Remaining RPCs** (`control.proto`)
  - Done when: every RPC in the design doc's control API table exists, including `ListOverwritten`, `RestoreOverwritten`, `GetFolderKeys` (audited), `ApproveDevice`, `DenyDevice`, `BlockDevice`, `UpdateFolderSettings`, `Get/SetIgnoreRules`, `PauseAll` and `AuthorizeUser`. Every sensitive RPC is covered by the identity interceptor.
- [ ] **P3.1.2 Event and metrics streams**
  - Done when: `WatchEvents` delivers folder state, progress, peers, overwrites and errors. `WatchMetrics` sends a snapshot every second. A client that reconnects gets a full snapshot first.

### P3.2 WPF app

- [ ] **P3.2.1 App skeleton** (`PeerSync.App/`)
  - Done when: it uses the generic host, `CommunityToolkit.Mvvm` and the Fluent theme. A single instance is enforced (a named mutex, and a second launch activates the first). `--tray` starts minimized to the tray. The pipe client reconnects with backoff and shows the "Service not running" banner.
- [ ] **P3.2.2 Folders screen and Add / Join / Share**
  - Done when: Add runs the folder-access grant (5.3) with progress and the elevation fallback, then `AddFolder`. Join validates the key checksum live. Share shows the keys with copy buttons and a QR code (`QRCoder`), with the RW key hidden until clicked.
- [ ] **P3.2.3 Folder details**
  - Done when: the Peers, Activity, Overwritten (restore my version) and Settings tabs work, including the ignore-rule editor.
- [ ] **P3.2.4 Pending devices and preferences**
  - Done when: approve, deny and block show the DeviceId fingerprint. Preferences edit `DeviceSettings` through `UpdateSettings`.
- [ ] **P3.2.5 Tray and toasts**
  - Done when: the icon reflects the overall state, the menu offers Pause for 30 min, 1 h or until resumed, and toasts appear for overwritten edits, errors and pending devices.
- [ ] **P3.2.6 "Authorize this account"**
  - Done when: a user outside `PeerSync Users` is offered elevation. Afterwards the app connects without signing out (5, rule 7).

### P3.3 Installer (Inno Setup)

- [ ] **P3.3.1 `installer/PeerSync.iss`**
  - Done when: it implements the design doc's script outline (service creation, `--init`, firewall, local group, autostart task, uninstall entries), and `ISCC /DArch=arm64` builds the Arm64 variant.
- [ ] **P3.3.2 Upgrade path**
  - Done when: installing v(n+1) over v(n) stops the service in `PrepareToInstall`, replaces files, re-runs `--init` (a no-op), restarts the service, and keeps all folders and settings. A downgrade is refused in `InitializeSetup`.
- [ ] **P3.3.3 Uninstall with data prompt**
  - Done when: an interactive uninstall asks whether to remove data (default: keep). Yes runs `--remove-data`. A silent uninstall keeps data.
  - Code: 9.1.
- [ ] **P3.3.4 Signing and silent install**
  - Done when: the setup, uninstaller and EXEs are signed (or BLOCKED on the certificate). `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS="autostart"` installs unattended and leaves a log with `/LOG`.

### 9.1 Code: uninstall data prompt (P3.3.3)

**Tricky:** ask at `usUninstall`, before files are removed. At `usPostUninstall` the service EXE that performs the cleanup is already gone.

```pascal
// installer/PeerSync.iss, [Code] section
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Code: Integer;
begin
  if (CurUninstallStep = usUninstall) and (not UninstallSilent) then
    if MsgBox('Also remove PeerSync settings, databases, logs and the device certificate?' + #13#10 +
              'Your synced folders and their files are not touched.',
              mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      Exec(ExpandConstant('{app}\PeerSync.Service.exe'), '--remove-data', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;
```

### Gate P3

- [ ] A tester who hasn't seen the project installs on two PCs and syncs a folder in under 10 minutes, using only the app (screen recording attached).
- [ ] Upgrade, uninstall-keep-data and uninstall-remove-data each pass on a clean Windows 11 VM and a clean Server 2022 VM.
- [ ] A standard user who isn't authorized can't call the control API. After "Authorize this account" they can.

---

## 10. Phase 4: Internet sync (weeks 18-22)

Goal: sync between different networks through `sync.taskscape.com:99`, with relay fallback.

### P4.1 Rendezvous server

- [ ] **P4.1.1 Host and Kestrel on port 99** (`PeerSync.Rendezvous/Program.cs`)
  - Done when: it runs as the Windows service `PeerSyncRendezvous` with HTTP/1, HTTP/2 and HTTP/3 on port 99, TLS 1.3 only, the certificate from `LocalMachine\My`, and client certificates (or the fallback logged in P0.2.6).
  - Code: 10.1.
- [ ] **P4.1.2 Session stream and presence** (`Rendezvous/Tracker/`)
  - Done when: `Announce`, `PeerList` push, `ObservedAddress` and the 15-minute TTL work. Every DeviceId is taken from the client certificate. A device sees only peers sharing a FolderId.
- [ ] **P4.1.3 Hole-punch coordination** (`Rendezvous/Tracker/Punch.cs`)
  - Done when: `PunchRequest` leads to `PunchNow` sent to both peers with a start time 500 ms ahead, as decided in P0.2.2.
- [ ] **P4.1.4 Relay** (`Rendezvous/Relay/`)
  - Done when: the `Relay.Pipe` gRPC stream is spliced as in 10.2. Limits apply: 2 MB/s per device, a total cap below the server uplink, sessions per device, and a 5-minute idle timeout.
- [ ] **P4.1.5 Abuse controls, bootstrap file and observability**
  - Done when: ASP.NET Core rate limiting is partitioned by IP and DeviceId, with at most 500 folders per device. `/.well-known/peersync.json` is served. `/healthz` responds. OpenTelemetry metrics and the Event Log work. Logs hold no FolderIds.
- [ ] **P4.1.6 `installer/PeerSync.Rendezvous.iss`**
  - Done when: it installs the service under `NT SERVICE\PeerSyncRendezvous`, adds firewall rules for TCP and UDP 99, and upgrades in place.

- [ ] **P4.1.7 Central deployment diagnostics** (`PeerSync.Rendezvous/Diagnostics/`, `docs/diagnostics.md`)
  - Done when: server debug events correlate authentication, presence, introductions, relay joins/leaves, quota/timeout and transport errors with client sessions; health and environment checks distinguish TCP/UDP failures. Central and second-PC bundles diagnose a forced relay failure on LAN and WAN. No FolderIds, keys or plaintext are logged; IP retention stays at most 24 hours.

### 10.1 Code: Kestrel on port 99 (P4.1.1)

```csharp
// src/PeerSync.Rendezvous/Program.cs
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Services.AddWindowsService(o => o.ServiceName = "PeerSyncRendezvous");
builder.Configuration.AddEnvironmentVariables(prefix: "PEERSYNC_");
builder.Services.Configure<RendezvousOptions>(builder.Configuration.GetSection("Rendezvous"));
var cfg = builder.Configuration.GetSection("Rendezvous").Get<RendezvousOptions>()!;   // Port = 99, CertificateSubject = "sync.taskscape.com"

builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenAnyIP(cfg.Port, listen =>
    {
        listen.Protocols = HttpProtocols.Http1AndHttp2AndHttp3;   // HTTP/3 = QUIC on UDP 99
        listen.UseHttps(https =>
        {
            https.ServerCertificate = StoreCertificates.NewestValid(StoreLocation.LocalMachine, cfg.CertificateSubject);
            https.SslProtocols = SslProtocols.Tls13;
            https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
            https.ClientCertificateValidation = (cert, _, _) => cert.GetECDsaPublicKey() is not null;  // self-signed device certs
        });
    });
});
builder.Services.AddGrpc(o => o.MaxReceiveMessageSize = 256 * 1024);
builder.Services.AddRateLimiter(o => o.AddPolicy("per-ip", ctx => RateLimitPartition.GetTokenBucketLimiter(
    ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
    _ => new TokenBucketRateLimiterOptions { TokenLimit = 60, TokensPerPeriod = 30, ReplenishmentPeriod = TimeSpan.FromMinutes(1), QueueLimit = 0 })));

var app = builder.Build();
app.UseRateLimiter();
app.MapGrpcService<TrackerService>().RequireRateLimiting("per-ip");
app.MapGrpcService<RelayService>();
app.MapGet("/.well-known/peersync.json", (IOptions<RendezvousOptions> o) => o.Value.BootstrapDocument);
app.MapGet("/healthz", () => Results.Ok());
await app.RunAsync();
```

The certificate is loaded at start. win-acme's post-renewal script restarts the service (P4.4.2), and clients reconnect on their own.

### 10.2 Code: relay splice and TLS over a gRPC stream (P4.1.4, P4.2.4)

**Tricky:**

- Each `IServerStreamWriter` accepts one write at a time. Each direction of the splice writes to a different peer's writer, so there's never a concurrent write.
- On the client, wrap the duplex call as a `Stream` so `SslStream` can run end to end through the relay.

```csharp
// src/PeerSync.Rendezvous/Relay/RelayService.cs
public override async Task Pipe(IAsyncStreamReader<RelayChunk> requests, IServerStreamWriter<RelayChunk> responses, ServerCallContext ctx)
{
    string token = ctx.RequestHeaders.GetValue("psp-relay-token")
        ?? throw new RpcException(new Status(StatusCode.Unauthenticated, "Missing relay token."));
    byte[] deviceId = DeviceId.FromCertificate(ctx.GetHttpContext().Connection.ClientCertificate!);
    RelayLeg other = await sessions.JoinAsync(token, deviceId, requests, responses, ctx.CancellationToken);  // waits up to 30 s

    // This leg copies only its own requests to the OTHER leg's writer; the other leg does the reverse.
    try
    {
        await foreach (var chunk in requests.ReadAllAsync(ctx.CancellationToken))
        {
            await Throttle.ThrottleAsync(limits.For(deviceId), chunk.Data.Length, ctx.CancellationToken);
            await other.Writer.WriteAsync(chunk, ctx.CancellationToken);
        }
    }
    finally
    {
        // Returning completes OUR response stream, which the other leg writes to. Tell it to stop and
        // wait until it has, or its next write fails with "stream already completed".
        await sessions.LeaveAsync(token, deviceId);
    }
}
```

```csharp
// src/PeerSync.Net/Relay/GrpcDuplexStream.cs
public sealed class GrpcDuplexStream(AsyncDuplexStreamingCall<RelayChunk, RelayChunk> call) : Stream
{
    ReadOnlyMemory<byte> pending;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (pending.IsEmpty)
        {
            if (!await call.ResponseStream.MoveNext(ct)) return 0;     // relay or peer closed
            pending = call.ResponseStream.Current.Data.Memory;
        }
        int n = Math.Min(buffer.Length, pending.Length);
        pending.Span[..n].CopyTo(buffer.Span);
        pending = pending[n..];
        return n;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
        new(call.RequestStream.WriteAsync(new RelayChunk { Data = ByteString.CopyFrom(buffer.Span) }, ct));   // copy: SslStream reuses its buffer

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
    public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) call.Dispose(); base.Dispose(disposing); }
}

// Usage: the requesting device is the TLS client, the other device the TLS server, with the same rules as 6.1.
var tls = new SslStream(new GrpcDuplexStream(call));
await tls.AuthenticateAsClientAsync(PeerTls.ClientOptions(deviceCert), ct);
```

### P4.2 Client: tracker and NAT

- [ ] **P4.2.1 Tracker client** (`PeerSync.Net/Rendezvous/TrackerClient.cs`)
  - Done when: one `Rendezvous.Session` stream per configured server uses the device certificate for mutual TLS. Announces are sent on start, on folder changes and every 5 minutes. Peer lists feed the connection manager. Reconnects back off up to 5 minutes. All servers in `RendezvousServers` are used.
- [ ] **P4.2.2 Port mapping** (`PeerSync.Net/Nat/PortMapper.cs`)
  - Done when: UPnP or NAT-PMP maps the listening port for TCP and UDP, renews every 30 minutes, removes the mapping on stop, and degrades silently when no gateway answers.
  - Code: 10.3.
- [ ] **P4.2.3 Hole punching** (`PeerSync.Net/Nat/Puncher.cs`)
  - Done when: it implements the option chosen in P0.2.2, and the NAT test matrix from P0.2.2 passes against the real server.
  - P0 local outcome (2026-10-18): all three same-port options failed on the development PC. User-approved baseline (2026-10-19): implement direct reachable endpoints followed by relay at 5 s, with no coordinated-punch claim. The deferred P0.2.2 physical NAT matrix and final choice remain mandatory at Gate P4; see `docs/spikes/P0.2.2-quic-punch.md`.
- [ ] **P4.2.4 Relay client** (`PeerSync.Net/Relay/RelayClient.cs`)
  - Done when: after 5 s without a direct path, `RelayRequest`, then `Relay.Pipe`, then TLS inside (10.2), then the normal handshake. The UI shows "via relay" for that peer.
- [ ] **P4.2.5 `psp.exe probe`** (`PeerSync.Cli/Probe.cs`)
  - Done when: `psp probe sync.taskscape.com:99` reports TCP reachability, UDP reachability (an HTTP/3 request) and the address the server observes. Exit code 0 means both protocols work.

### 10.3 Code: port mapping (P4.2.2)

```csharp
// src/PeerSync.Net/Nat/PortMapper.cs  (Mono.Nat 3.x)
public sealed class PortMapper(int port, ILogger<PortMapper> log) : IAsyncDisposable
{
    readonly ConcurrentBag<INatDevice> devices = [];

    public void Start()
    {
        NatUtility.DeviceFound += async (_, e) =>
        {
            try
            {
                await e.Device.CreatePortMapAsync(new Mapping(Protocol.Udp, port, port, 3600, "PeerSync"));
                await e.Device.CreatePortMapAsync(new Mapping(Protocol.Tcp, port, port, 3600, "PeerSync"));
                devices.Add(e.Device);
                log.LogInformation("Mapped port {Port} on {Gateway}", port, e.Device.DeviceEndpoint);
            }
            catch (Exception ex) { log.LogWarning(ex, "Port mapping refused by {Gateway}", e.Device.DeviceEndpoint); }
        };
        NatUtility.StartDiscovery();        // UPnP and NAT-PMP; renew from a 30-minute timer
    }

    public async ValueTask DisposeAsync()
    {
        NatUtility.StopDiscovery();
        foreach (var d in devices)
        {
            try { await d.DeletePortMapAsync(new Mapping(Protocol.Udp, port, port)); } catch { }
            try { await d.DeletePortMapAsync(new Mapping(Protocol.Tcp, port, port)); } catch { }
        }
    }
}
```

### P4.3 Deployment on the taskscape.com server

- [ ] **P4.3.1 DNS** (done by whoever manages taskscape.com DNS)
  - Done when: `sync.taskscape.com` has an A record (and AAAA if the server has IPv6) pointing at the server.
- [ ] **P4.3.2 Certificate with win-acme**
  - Done when: a Let's Encrypt certificate for `sync.taskscape.com` is in `LocalMachine\My`, renewal is scheduled, and the post-renewal script restarts `PeerSyncRendezvous`.
- [ ] **P4.3.3 Port 99 free and open**
  - Done when: nothing else listens on 99, the Windows Firewall rules exist, and the provider allows TCP 99 (confirmed) and UDP 99 (to verify).
- [ ] **P4.3.4 Install and verify**
  - Done when: the rendezvous installer has run, `/healthz` answers, and `psp probe sync.taskscape.com:99` passes from an outside network for both TCP and UDP. If UDP fails, ask the provider to open UDP 99 and record the outcome in section 2.

### 10.4 Server commands (P4.3)

Run in an elevated PowerShell on the taskscape.com server.

```powershell
# 1. Port 99 must be free (no output expected), and HTTP.sys must have no reservation on it.
Get-NetTCPConnection -LocalPort 99 -ErrorAction SilentlyContinue
Get-NetUDPEndpoint  -LocalPort 99 -ErrorAction SilentlyContinue
netsh http show urlacl | Select-String ':99/'

# 2. Firewall (the rendezvous installer also adds these).
New-NetFirewallRule -DisplayName 'PeerSync Rendezvous TCP 99' -Direction Inbound -Protocol TCP -LocalPort 99 -Action Allow
New-NetFirewallRule -DisplayName 'PeerSync Rendezvous UDP 99' -Direction Inbound -Protocol UDP -LocalPort 99 -Action Allow

# 3. Post-renewal script for win-acme.
New-Item -ItemType Directory -Force 'C:\ProgramData\PeerSync.Rendezvous' | Out-Null
Set-Content 'C:\ProgramData\PeerSync.Rendezvous\after-renewal.ps1' 'Restart-Service PeerSyncRendezvous -Force'
```

win-acme, interactive (`wacs.exe`): create a certificate with full options, manual input, host `sync.taskscape.com`. For validation, use HTTP through IIS on port 80 (add `sync.taskscape.com` as a binding on the taskscape.com site) or DNS validation. Store the certificate in the Windows Certificate Store (`My`, local machine), and as the installation step, run the script `C:\ProgramData\PeerSync.Rendezvous\after-renewal.ps1`.

```powershell
# 4. From a PC outside that network, after installing the rendezvous service:
Test-NetConnection sync.taskscape.com -Port 99      # TCP only
psp probe sync.taskscape.com:99                     # TCP and UDP, plus the observed address
```

### Gate P4 (WAN beta)

- [ ] Complete deferred P0.2.2: record same-LAN, full-cone home NAT, port-restricted home NAT and mobile/CGNAT outcomes for the required binding alternatives, log the final transport decision and reconcile P4.2.3, then close P0.2.2. Tailscale and rendezvous reachability do not substitute for these checks.
- [ ] Sync works between two home networks behind different NATs, and with one PC behind a mobile hotspot (CGNAT).
- [ ] With direct paths blocked, the relay carries the sync. Its rate caps hold, and taskscape.com stays responsive (load test attached).
- [ ] UDP 99 verified end to end, or the TCP-only outcome is logged with its impact.
- [ ] A 72-hour soak across the WAN with zero data loss.

---

## 11. Phase 5: Hardening and v1.0 (weeks 23-26)

- [ ] **P5.1 Fuzzing** (`tests/PeerSync.Fuzz`)
  - Done when: frame decoder, message validation, path rules, ignore parser and key text parser each run for 24 hours with no crash. Corpus and crashes are saved.
- [ ] **P5.2 Security review**
  - Done when: an external or peer review covers the threats table in the design doc. Findings are fixed or logged with a rationale. Keys are absent from logs, crash dumps and the control API (except `GetFolderKeys`).
- [ ] **P5.3 Scale: 1M files**
  - Done when: first index of 1M files takes under 20 minutes on SSD, idle CPU is under 1%, memory is under 300 MB, and a 1M-file folder syncs to a new peer.
- [ ] **P5.4 Faster start-up with the USN journal** (`PeerSync.Engine/Scanning/UsnJournal.cs`)
  - Done when: changes made while the service was stopped are found from the journal instead of a full walk.
  - `[VERIFY]` Reading the journal needs a volume handle, and the virtual account isn't an administrator. Spike first. If it needs `SeManageVolumePrivilege` or admin rights, either grant the privilege to the service SID or drop this task and keep the size/mtime short-circuit scan.
- [ ] **P5.5 Archive restore UI**
  - Done when: the Archive tab lists versions by path and date, and supports restore and restore-as-copy.
- [ ] **P5.6 Device approval end to end**
  - Done when: with "Require approval" on, unknown devices wait in Pending until approved, and blocked devices can't reconnect.
- [ ] **P5.7 Signed auto-update** (`PeerSync.Service/Update/`)
  - Done when: the service checks a signed manifest daily and downloads the new setup. A short-lived helper process verifies the Authenticode signature and runs `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`, because Setup stops the service. Updates can be turned off.
- [ ] **P5.8 Documentation**
  - Done when: user guide, admin guide (service account, ACLs, ports, logs, `--init`, `--remove-data`) and rendezvous operations guide are written.

### Gate P5 (v1.0 release)

- [ ] All tests and performance targets met (attach the numbers).
- [ ] No open data-loss or security bugs.
- [ ] A 72-hour soak on the release candidate, on LAN and WAN, with zero data loss.
- [ ] Release signed and published, and the design doc and this plan updated to "v1.0 shipped".

---

## 12. Phase 6: After v1.0 (optional)

Each feature ships on its own, behind a flag, after the same 72-hour soak gate.

- [ ] **P6.1 Encrypted backup peers (F keys), about 3 weeks**
  - Done when: an F peer stores and serves only ciphertext (chunks and names encrypted as in the design doc's Security model) and can't decrypt. A new RW peer restores a full folder from an F peer alone.
- [ ] **P6.2 Share links with identities and owner approval, about 4 weeks**
  - Done when: `peersync://` links with expiry by time and by uses work. The owner approves the requester's fingerprint. Approval survives across the owner's linked devices.
- [ ] **P6.3 On-demand placeholders (Windows Cloud Files API), about 6 to 8 weeks**
  - Done when: a folder can be in "selective" mode. Files appear as cloud placeholders in Explorer and hydrate on open. "Free up space" dehydrates.
- [ ] **P6.4 Mainline DHT discovery, about 2 weeks**
  - Done when: with the tracker unreachable, peers find each other via BEP 5 using the HMAC-derived info-hash. It is off by default.

---

## Appendix A: Constants

| Name | Value |
| --- | --- |
| ALPN | `psp/1` |
| Client listening port | Random 20000-60000, chosen by `--init`, UDP and TCP |
| LAN multicast | `239.255.80.83:47820`, `[ff12::5053]:47820`, TTL 1 |
| Rendezvous endpoint | `https://sync.taskscape.com:99` (TCP and UDP) |
| Bootstrap document | `https://sync.taskscape.com:99/.well-known/peersync.json` |
| Control pipe | `\\.\pipe\PeerSync.Control` |
| Service names | `PeerSync` (clients), `PeerSyncRendezvous` (server) |
| Service accounts | `NT SERVICE\PeerSync`, `NT SERVICE\PeerSyncRendezvous` |
| Local group | `PeerSync Users` |
| Device certificate | `CN=PeerSync Device` in `LocalMachine\My`, ECDSA P-256, 20 years |
| Max frame | 16 MiB |
| Index batch | 1,000 records |
| Block requests per frame | 64 |
| Per-peer in-flight window | 4 MiB start, 32 MiB max |
| Chunk classes | 0: ~64 KiB (files < 256 MiB), 1: ~512 KiB (< 8 GiB), 2: ~2 MiB |
| Watcher debounce | 2 s quiet, 30 s maximum |
| Rescan interval | 600 s |
| Clock-skew limit | 10 min |
| Archive | 30 days, files up to 1,000 MB, 10% of the volume |
| Pause downloads below | 1 GiB free |
| Tracker re-announce / TTL | 5 min / 15 min |
| Relay per-device cap | 2 MB/s |
| Relay idle timeout | 5 min |
| Direct-path timeout before relay | 5 s |
| Peer expiry | 7 days |
| Temp file suffix | `.!psp` (hidden, next to the target) |
| HKDF / MAC labels | `psp1 sign`, `psp1 content`, `psp1 auth`, `psp1 ro-auth`, `psp1 folder`, `psp1 record`, `psp1 gear` |
