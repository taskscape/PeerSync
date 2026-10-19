# P1.5.3 atomic replacement and recovery decision

Status: proposal only; approval is pending. No atomicity requirement is waived.

The current source design's apply step 5 and plan.md section 7.11 use
`File.Replace` for an existing file. The implementation also needs to keep its
verified no-reparse target guard while installing, so ordinary concurrent
writers/deleters cannot replace the verified file. Its directory ancestors
remain pinned through root-relative handles.

The native check in `AtomicReplacementTests` found that
`FileRenameInformationEx` with replacement/POSIX/ignore-readonly flags still
fails with native error 32 while that no-delete-sharing guard is held. Both
normal and read-only cases retain the original, verified temporary data and
no-overwrite archive link. This does not establish a working guarded atomic
replacement primitive. Evidence: `artifacts/p1-atomic-native-tests.log` and its
TRX. The test never calls File.Replace or replaces through a symbolic link.

## Option A: approve recoverable handle moves

Keep the verified original guard and all pinned directory leases. Persist a
FULL-synchronous, root/file-bound intent after verified assembly and metadata
preparation. Move the exact old handle to a no-overwrite archive/conflict slot,
then move the verified temporary handle into the now-free target name without
overwriting. The final install is atomic; the archive-plus-install pair has a
brief missing-target interval. External applications can observe that interval.
A shared folder operation gate prevents scans from publishing it as deletion.

On restart, recover before scanning. If a verified temporary file is available,
finish installation; otherwise safely restore the old version when the name
is vacant. Preserve unknown concurrent targets and every retained version.
Commit local/chunk/sequence/archive/needed metadata and remove the intent in
the existing atomic durable transaction. Cover each crash/failure boundary,
changed targets, locked/read-only files and root/reparse rejection in tests.

Exact plan changes after approval:

- Replace source apply step 5's File.Replace choice with the above journaled
  root-relative handle moves and explicit external missing-target interval.
- Supersede plan.md 7.11's File.Replace example with the coordinator and state
  that atomic final installation and recoverable archival are separate steps.
- Keep all P1.5.2 conflict outcomes, P1.5.3 scheduling/retry requirements,
  P1.5.4 retention requirements, no-lost-version tests and every phase gate.

This changes observable atomicity, so it is not applied without approval.

## Option B: retain strict existing-target atomic replacement

Keep the original contract and continue investigating a native protocol that
provides atomic replacement without releasing the verified target guard.
The failed POSIX capability check is not considered an implementation. Journal,
ownership validation, folder serialization, recovery codec and archive work
can continue independently while this primitive is unresolved. Do not silently
substitute the two-move protocol or claim strict atomicity from it.

Primary references: [native rename flags](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_file_rename_information),
[ReplaceFile behavior and partial-failure states](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew).
