# Bounded release verification, 2026-10-26

The user reduced the required PeerSync test budget. For new required gate
executions, the generated content must be **at most 10 GiB**
(10,737,418,240 bytes) and active test execution must finish within **three
hours**. This is a test-resource limit, not a production file-size limit or a
change to archive, retry, certificate, or log-retention policy. Historical
longer/larger evidence remains historical and is not silently reclassified.

| Gate | Revised acceptance |
| --- | --- |
| P2 physical LAN corpus | Three physical PCs converge a 9 GiB random-content mix with 100,000 small files, 24 deep levels and eight large files within three hours after the source folder resumes. Keep total test-tree content at or below 10 GiB, reserving up to 1 GiB for later soak operations. Preserve native counts, hashes, transport and elapsed-time evidence. |
| P2/P4/P5 physical soaks | At most three hours of active random operations, with comparable native-tree snapshots at scheduled hours 0, 1, 2 and 3. The final read-only capture and report comparison may finish after operations stop. Preserve sleep/resume, Wi-Fi, NAT and relay observations required by the respective gate. A missed or mismatched checkpoint fails. |
| P5 fuzz | Three continuous hours with all seven existing targets, retained corpus, zero crashes and stderr. The already completed 24-hour seven-target campaign exceeds the reduced duration and remains valid evidence; do not rerun it for this change. |
| P5 scale | First index **100,000 files** within 20 minutes on SSD; idle CPU below 1%; observed peak working set and sampled private memory below 300 MB; unchanged rescan has zero rehashes/updates and preserves sequence. A fresh peer must verify all 100,000 native names and bytes and index rows within one three-hour end-to-end benchmark execution. The older million-file completed result remains useful stress evidence, but the interrupted later million-file run does not close the revised gate. |

The caps apply to verification workloads and active operation windows. Build,
fixture preparation, read-only final report comparison and calendar spacing
between the already accepted P1 campaign dates are recorded separately.
No failed assertion, missing physical network path, independent security
review, release-signer check or code-signing requirement is waived. A run that
hits its new time cap is a failed or incomplete gate; it cannot be accepted
solely because a smaller smoke passed. The plan and design doc carry the
authoritative gate descriptions, and the field-kit scripts enforce the new
size and scheduled-hour parameters.
