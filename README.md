# PeerSync

A Windows peer-to-peer folder sync service, currently in **P1: local sync
engine**. P0 passed with physical LAN/NAT validation deferred to Gates P2/P4.
The production sync engine, central tracker/relay, WPF client and
installers are not implemented yet. See [plan.md](plan.md) for the acceptance
criteria and phase gates. P6 is optional and outside the v1 target.

Requires Windows 11 / Server 2022 or newer and .NET SDK 10.0.402. The solution
contains all eleven production projects plus test projects and executable P0
probes. Dependencies are centrally pinned and restore uses checked-in lockfiles.

```powershell
dotnet build -c Release
dotnet test --project tests/PeerSync.Core.Tests/PeerSync.Core.Tests.csproj -c Release --report-trx
dotnet test --project tests/PeerSync.Crypto.Tests/PeerSync.Crypto.Tests.csproj -c Release --report-trx
./build/Run-P0.ps1             # native local probes, including 1 GiB QUIC transfers
./build/Run-ServiceP0.ps1      # elevated; temporary services and one standard user, cleaned up
./build/Verify.ps1             # CI parity: build, local tests, probes, 10 min fuzz, publish
./build/Publish.ps1 -Runtime win-x64
```

CI builds x64 and Arm64 on Windows. Runtime checks run on x64; Arm64 currently
has compile/publish coverage only. Fuzz exercises chunk boundaries, hostile
paths, compact version-vector decoding and ignore rules. Later phases add protocol-frame
fuzz, simulations and product integrations.
CI runs the full ten-minute fuzz smoke in its own required parallel job;
build/publish jobs report `fuzzSeconds: 0` and do not substitute for that job.
The default local `Verify.ps1` still includes fuzzing. `Run-Fuzz.ps1` runs the
same target independently.

Folder capabilities use HKDF-SHA256 and Ed25519 with explicit RW/RO/F text
exports. Key objects redact string/JSON diagnostics and clear owned secrets
when disposed. The checked-in public synthetic vectors are generated
independently with Python/OpenSSL; normal .NET tests need no Python. See
[the key verification report](docs/verification/P1.1.1-folder-keys-2026-10-19.md).

For server / second-PC probe deployment, debug events and diagnostic bundles,
read [docs/diagnostics.md](docs/diagnostics.md). Probe bundles deliberately say
`productionSyncAvailable: false` in their manifest. No installer or working
folder sync is implied by a successful scaffold build.

The full design is checked in as
[the design document](Resilio-style%20P2P%20Sync%20for%20Windows%20Implementation%20Plan.md).
Work proceeds in phase order. Cross-machine, NAT, VM and soak evidence is kept
separate from single-host checks, and unfinished gates stay open.

Run and replay the generated local engine scenarios using [the simulation guide](docs/simulation.md).
