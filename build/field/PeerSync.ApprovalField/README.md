# Installed approval field control

`PeerSync.ApprovalField` uses the same owner- and process-verified local named
pipe as the installed CLI. It has no network listener and never exports folder
keys. Publish it self-contained for the target architecture:

```powershell
dotnet restore build/field/PeerSync.ApprovalField/PeerSync.ApprovalField.csproj --locked-mode
dotnet publish build/field/PeerSync.ApprovalField/PeerSync.ApprovalField.csproj -c Release -r win-x64 --self-contained --no-restore -o artifacts/field/approval-tool
```

Run it as an authorized local account or SYSTEM:

```text
PeerSync.ApprovalField inspect <folder>
PeerSync.ApprovalField set-approval <folder> true|false
PeerSync.ApprovalField decide <folder> <64-hex-device-id> approve|deny|block
```

`inspect` reports only a folder fingerprint, approval state, device IDs and
session state. It does not print the folder path, FolderId or key. For field
validation, use a fresh disposable folder so that changing approval cannot
interrupt an existing share. Record a native file hash before joining,
confirm Pending and absence of transferred bytes, approve the actual pending
device ID, compare the destination hash, then block it and verify a new file
does not arrive. Remove the test membership and temporary credentials after
the run using the host's normal administration path.
