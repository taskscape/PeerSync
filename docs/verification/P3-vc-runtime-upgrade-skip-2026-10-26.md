# Avoid reinstalling an already current Visual C++ runtime

The signed 1.0.31.0 first-PC upgrade ran `vc_redist.exe /install /quiet
/norestart` at 2026-10-26 18:34:11 local even though the machine already had
Visual C++ v14 x64 version 14.51.36247.00. Windows created a restore point
named `Microsoft Visual C++ v14 Redistributable (x64) - 14.51.36247` at
18:34:12 local. At investigation, the resulting shadow copy used about 5.79
GB, materially reducing space available for the pending physical corpus. The
restore point remains intact.

Both Inno installers now compare the installed architecture-specific
`Installed`, `Major`, `Minor`, `Bld`, and `Rbld` registry values and the native
`VCRUNTIME140.dll` version to the **bundled** redistributable version. A
current or newer runtime skips the redistributable; missing, malformed, or
older evidence runs the same Microsoft-signed prerequisite before service
replacement. Exit codes 3010, 0, and 1638 retain their previous handling.
The packaged redistributable version is read at compile time, so the check
tracks future bundle updates.

The Inno 7.1 compiler parsed and compiled both updated scripts successfully
with x64 existing publish assets and `--output=no --no-signing
--define=UnsignedDevelopment`; no new installer was emitted. This validates
syntax, not a deployed skip. Verify the setup log's `skipping redistributable`
entry and absence of a new Visual C++ restore point on a later signed upgrade.
The clean-machine missing-runtime/reboot path remains required.

The no-output Inno compile removed its pre-existing same-name output files.
The signed client 1.0.31.0 setup was restored byte-for-byte from its
deployment package (SHA-256
`7BE0014A26E1E743F97A33BC30AE31225C2022B0BA23F8D5CA476BF5B0A69122`);
the rendezvous 1.0.0.0 setup was restored from the pre-existing Windows shadow
copy (SHA-256
`36FCD0C863C7955F2EE2CFFD76096B37EAF9FEDB0B1073120DDD124FC6F64586`).
Neither restored artifact contains this source change.
