# P5.3 remote-file scan batching, 2026-10-23

The coordinator previously ran a complete native scanner walk after every
1,000-item scheduled pull pass containing an applied file, even though the
applier had already committed that file's signed row and verified native bytes.
At million-file scale this could repeat the million-entry walk hundreds of
times while a new peer was still receiving live files.

Ordinary stage-1 file installs now share a scanner pass across at most 10,000
applied files or five minutes. The scan runs when live work drains and before
directory or deletion work enters the next batch. Explicit watcher/periodic
scans, native target races, preserved public conflict copies, file/directory
transitions, recovery, and source changes still request their existing scans.
The change keeps the 1,000-work per-pass limit and does not weaken the
directory-publication barrier. If the process exits with deferred work, its
startup recovery still scans before pulling. Debug events report deferred and
flushed counts without paths, hashes, keys or records.

A native 1,001-file case installs the first 1,000 live files without a rescan,
then scans once when the final file drains the queue; it checks native bytes,
needed rows, pending publication and the sanitized debug events. The complete
local Engine suite passes 1,044/1,044 after the final diagnostic-event edit.

The older `79d4e46` million-file fresh-peer process runs its original binary
and cannot validate this code. A new committed-source million-file transfer
with exact native-byte verification remains required to close P5.3. The local
case demonstrates fewer scanner passes, not a measured million-file speedup.
