# CLI Add/Join service-folder ACL repair, 2026-10-26

The fresh third-PC 1.0.28.0 `psp join` returned `Service request failed:
NotFound` for an existing, empty user-profile directory. The installed
service journal identified the underlying exception as
`DirectoryNotFoundException`. The service runs as `NT SERVICE\PeerSync`; the
selected directory initially granted access only to SYSTEM, Administrators
and the user. Windows `Directory.Exists` returned false to the service until
that one directory received an inheritable Modify grant for its service SID.
The [physical result](P2-three-pc-lan-join-2026-10-26.md) then passed.

The WPF Add and Join actions already apply this service grant before calling
the control API. The current-source CLI now does the same for `psp add` and
`psp join`, limited to the selected folder and inherited children. It leaves
an already sufficient explicit grant alone, rejects reparse roots, and
reports a clear local folder-access error if the grant fails. `psp share`,
`peers`, `hosts` and index reads do not change ACLs. The folder key is not
included in diagnostics. Join validates the key's canonical encoding and
checksum before changing the selected folder's ACL.

The focused CLI suite passed **8/8** on Windows using an isolated locked
NuGet restore and the xUnit v3 in-process runner. The added test verifies
one inheritable Modify rule on the selected folder after two grant calls and
no explicit grant on a sibling; another checks invalid-key rejection before
the ACL preflight. The repository's old local NuGet cache was
incomplete, so initial direct build/test attempts could not resolve packages;
the isolated restore and final test exited 0. No installed client package was
rebuilt or deployed for this source repair yet. The physical three-PC result
used the manual ACL grant and installed 1.0.28.0 CLI, so it is not evidence
that the repaired CLI has passed on a fresh physical install.
