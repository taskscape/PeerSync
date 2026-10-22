# P2 self-contained service package local verification

`build/Publish-Service.ps1 -Runtime win-x64` completed successfully on 2026-10-22. It published separate self-contained `PeerSync.Service.exe` and `psp.exe` directories, included the diagnostics and two-PC deployment guides, wrote source/runtime/dirty-tree provenance with `phase=P2` and `gateP2Passed=false`, and created a ZIP archive.

The packaged CLI ran with `--help` and listed the documented control and diagnostics commands. The ZIP contains both executables, the runbook and diagnostics reference. The generated archive is `artifacts/deploy/PeerSync-win-x64-20261022T021740Z-6c5825a2.zip`; it was built from source revision `87a247fe5051009a20d6850d4ca526626bb7c724` with `workingTreeDirty=true`.

This verifies local packaging and CLI startup only. The package is experimental and is not Authenticode-signed. It has not been installed from the ZIP on Windows Server or validated between two service PCs; the P2 gate remains open.
