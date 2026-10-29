# PeerSync

PeerSync is a Windows peer-to-peer folder sync service. The service, CLI,
authenticated QUIC and TCP/TLS transports, sync engine, control pipe,
diagnostics, and development installer are implemented. P4 rendezvous and
end-to-end encrypted relay code is being validated in this working tree.
The product remains experimental: physical LAN, native NAT, WAN and soak
gates are incomplete. See
[plan.md](plan.md) for acceptance criteria and phase gates. P6 is optional and
outside the v1 target.

Requires Windows 11 / Server 2022 or newer and .NET SDK 10.0.402. The solution
contains the production projects plus test projects and executable P0
probes. Dependencies are centrally pinned and restore uses checked-in lockfiles.

```powershell
dotnet build -c Release
dotnet test --project tests/PeerSync.Core.Tests/PeerSync.Core.Tests.csproj -c Release --report-trx
dotnet test --project tests/PeerSync.Crypto.Tests/PeerSync.Crypto.Tests.csproj -c Release --report-trx
./build/Run-P0.ps1             # native local probes, including 1 GiB QUIC transfers
./build/Run-ServiceP0.ps1      # elevated; temporary services and one standard user, cleaned up
./build/Verify.ps1                       # local build, tests, simulations, probes, fuzz and publishing
./build/Publish-Service.ps1 -Runtime win-x64 # experimental self-contained service + CLI bundle
```

CI builds x64 and Arm64 on Windows. Runtime checks run on x64; Arm64 currently
has compile/publish coverage only. Fuzz exercises chunk boundaries, hostile
paths, compact version-vector decoding and ignore rules. Later phases add protocol-frame
fuzz, simulations and product integrations.
CI runs the full ten-minute fuzz smoke in its own required parallel job;
build/publish jobs report `fuzzSeconds: 0` and do not substitute for that job.
The default local `Verify.ps1` still includes fuzzing. `Run-Fuzz.ps1` runs the
same target independently.

Every push to `main` builds the client installers (x64 and Arm64) and the
optional Windows Server Rendezvous/relay installer (x64). New pushes leave
earlier `main` builds running; superseded pull request runs are canceled.
Download the `PeerSync-client-win-x64-<commit>`,
`PeerSync-client-win-arm64-<commit>` or
`PeerSync-rendezvous-win-x64-<commit>` artifact from the
[CI run](https://github.com/taskscape/PeerSync/actions/workflows/ci.yml).
Artifacts are retained for 14 days and contain the setup EXE, source/hash
manifest and public development signing certificate. These are experimental
CI packages signed with a temporary test identity, not public release packages.
The optional server is built on every run; installing it is optional.

Folder capabilities use HKDF-SHA256 and Ed25519 with explicit RW/RO/F text
exports. Key objects redact string/JSON diagnostics and clear owned secrets
when disposed. The checked-in public synthetic vectors are generated
independently with Python/OpenSSL; normal .NET tests need no Python. See
[the key verification report](docs/verification/P1.1.1-folder-keys-2026-10-19.md).

For an experimental Windows Server / second-PC service deployment, use the
[two-PC service runbook](docs/peersync-service-two-pc-runbook.md). The service
package manifest marks P2 and keeps `gateP2Passed: false`; a local verifier pass
does not imply cross-PC validation. For event meanings and sanitized diagnostic
bundles, including the P4 rendezvous server, read
[docs/diagnostics.md](docs/diagnostics.md). P0 probe bundles
deliberately say `productionSyncAvailable: false` and do not implement folder
sync.

The full design is checked in as
[the design document](Resilio-style%20P2P%20Sync%20for%20Windows%20Implementation%20Plan.md).
Work proceeds in phase order. Cross-machine, NAT, VM and soak evidence is kept
separate from single-host checks, and unfinished gates stay open.

Run and replay the generated local engine scenarios using [the simulation guide](docs/simulation.md).
