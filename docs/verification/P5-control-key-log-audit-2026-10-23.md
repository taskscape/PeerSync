# P5.2 verified-pipe raw control-log audit, 2026-10-23

`FolderManagementRpcsAddJoinListPeersAndStreamIndexOverTheVerifiedPipe` now
passes one debug-enabled journal through the test service runtime, installed
folder host and named-pipe control host. It exercises `AddFolder`,
`GetFolderKeys`, `JoinFolder`, peer decisions, invalid requests, archive calls
and `DumpIndex`, then reads the **raw** `debug.jsonl` file. The audit requires
the `control.folder_keys_read` event while rejecting both returned key texts,
both FolderIds, both native folder paths and the indexed file name. A failed
assertion reports only a generic error and does not print the secret value.

The focused test passed 1/1. The complete Release Service suite passed 55/55
on the same source. This checks the implemented fixture path and the actual
verified named pipe, not a production installation or every possible error
path. An independent reviewer still needs to inspect raw logs from installed
LAN/WAN sessions and the crash-report boundary. No P5.2 gate closure is
claimed from this test.
