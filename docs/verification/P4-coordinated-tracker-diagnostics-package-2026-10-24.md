# Coordinated tracker-diagnostics packages, 2026-10-24

The client and rendezvous x64 installers were built from source revision
`75b06eeacc5e0615cd7e6c1c98991b10cabffb40` as version 1.0.20.0. Both
are signed with the existing **development** code-signing certificate. The
installer signatures and published executable signatures verified as Valid,
and both installer hashes match their manifests:

| Package | Bytes | SHA-256 |
| --- | ---: | --- |
| `artifacts/installer/PeerSync-Setup-1.0.20.0-x64.exe` | 166,891,872 | `9F4C1BD34382F2BBF93F99C3479FE6CB833EA8F8A1FC51D71A98F492A7F562C6` |
| `artifacts/installer/PeerSync-Rendezvous-Setup-1.0.20.0-x64.exe` | 66,291,584 | `05F46E94E78D44E5ABFB7A7B6BFA10339DD4CE89CF9836C844D13030438070C0` |

The packages contain the [tracker session correlation](P4-tracker-session-correlation-2026-10-24.md)
validated locally by the 59-test Net and 22-test Rendezvous suites. They are
ready for a coordinated field upgrade so the next reset can be matched by
session ID between the client and server journals. Neither package has been
installed as part of this check. The first PC's installed CLI still reports
1.0.19.0 and its service is Running; the server was last verified at 1.0.17.0.
The third PC's installed version is unconfirmed and its diagnostic bundle is
pending. The manifests mark these packages as
`localDevelopmentOnly`; they cannot satisfy public release signing.
