# PeerSync

PeerSync synchronizes folders between Windows PCs. A Windows service runs the
sync engine, a WPF dashboard and tray app provide local control, and the `psp`
CLI exposes status, folder management and diagnostics. An optional Windows
Server component provides peer discovery and encrypted relay transport when
direct connections are unavailable.

## Current status

The `main` branch contains the sync engine, client service, WPF app, CLI,
client installers, and P4 rendezvous/relay implementation. PeerSync remains
experimental; it has not passed the LAN beta, WAN beta or v1.0 release gates.

| Component | Implemented capabilities |
| --- | --- |
| Sync engine | FastCDC chunking, BLAKE3 hashing, SQLite state, filesystem watching and rescans, signed indexes, conflict preservation and archive storage |
| Client service | Authenticated QUIC and TCP/TLS, LAN discovery, predefined hosts, tracker/relay integration and an authenticated local control pipe |
| Desktop app | Add, join and share folders; peer routes and transfer status; pause/resume; pending devices, folder settings and clock warnings |
| CLI | `status`, `add`, `join`, `share`, `hosts`, `peers`, `dump-index`, `diagnostics` and `probe` |
| Optional server | Certificate-bound tracker sessions, presence expiry, connection introductions, end-to-end TLS relay, health checks and diagnostics |
| Installers | Self-contained x64/Arm64 client packages and an x64 Rendezvous server package, with development signing and a bundled Visual C++ runtime |

P2 service and P3 app/installer code have historical passing PR/main CI.
P4 code is merged and has focused local tests and supplemental Server 2025
VM evidence. Physical LAN throughput/recovery, the required clean-machine
matrix, native NAT/mobile-network behavior, forced relay/load checks and
72-hour soaks remain open. P5 hardening and release acceptance are still
outstanding; P6 features are optional and outside the v1 target.

The [implementation plan](plan.md) retains the acceptance checklists and
approved implementation-order exceptions. Local tests, VM checks and
historical CI results do not close the remaining physical or release gates.
See the [P2 merge audit](docs/verification/P2-code-merge-2026-10-22.md),
[P3 CI audit](docs/verification/P3-main-ci-2026-10-22.md) and
[P4 local/VM report](docs/verification/P4-rendezvous-local-2026-10-22.md)
for their exact scope and limitations.

## Requirements and build

- Windows 11 or Windows Server 2022 or newer.
- Git, PowerShell 7 and .NET SDK **10.0.402**, as pinned in [global.json](global.json).
- For signed installer builds: Inno Setup **7.1 or newer**, Windows SDK
  Signing Tools and a code-signing certificate available by thumbprint.

From the repository root:

```powershell
dotnet restore PeerSync.slnx --locked-mode
dotnet build PeerSync.slnx -c Release --no-restore
```

Dependencies are centrally pinned in [Directory.Packages.props](Directory.Packages.props),
and restore uses checked-in lockfiles. The solution contains production
projects, test projects and executable P0 probes. Installed packages include
the .NET runtime; the target PC does not need the SDK.

## Install and try two PCs

Use disposable test folders and the same client build on both PCs. Follow
the [installer and two-PC guide](docs/installer-validation.md) for signing
trust, installation, account authorization, folder ACLs, upgrades and removal.
The client installer includes the service, dashboard and CLI. Its default
service identity is `NT SERVICE\PeerSync`.

On PC A, open PeerSync, add a test folder and use Share to obtain its
read/write key. On PC B, use Join with that key and a separate local folder.
Create a file on either PC, confirm the received contents and compare file
hashes. Folder keys are capabilities: share them only with intended peers.

The optional central service is installed separately under
`NT SERVICE\PeerSyncRendezvous`. It provides discovery and carries opaque
end-to-end encrypted relay traffic; it does not receive folder keys or
plaintext files. Public deployment requires a server TLS certificate and
reachable TCP/UDP port 99. Follow the
[Rendezvous operations guide](docs/rendezvous-operations.md) for certificate
renewal, firewall rules, client settings, external probes and relay checks.
Installing the server is optional for directly reachable peers.

## Verification

Run a focused test project with the Microsoft.Testing.Platform runner:

```powershell
dotnet test --project tests/PeerSync.Core.Tests/PeerSync.Core.Tests.csproj `
  -c Release --report-trx
```

The canonical local verifier builds the solution, runs test suites,
simulations, native probes and the ten-minute fuzz smoke, and publishes
self-contained probe bundles:

```powershell
./build/Verify.ps1 -Runtime win-x64
```

Additional checks can be run independently:

```powershell
./build/Run-Fuzz.ps1 -Seconds 600
./build/Run-Simulation.ps1 -Cases 8
./build/Run-P0.ps1

# Elevated PowerShell: temporary services and a standard test account.
./build/Verify-ServiceSecurity.ps1
```

Fuzzing covers FastCDC boundaries, hostile paths, version-vector decoding,
ignore rules and protocol frames. Simulations include generated flat and
hierarchical scenarios plus retained regression seeds. Use the
[simulation guide](docs/simulation.md) to inspect evidence and replay cases.
Normal .NET tests do not need Python; campaign controls and regeneration of
independent synthetic crypto vectors use separate Python tooling.

## Packages and CI

The [CI workflow](.github/workflows/ci.yml) runs for every push to `main`,
pull requests and manual dispatch. Each `main` push keeps its own run;
superseded pull request runs are canceled. The configured jobs cover:

- Windows x64 and Arm64 builds, publishing and development-signing checks.
  Runtime checks execute on x64; Arm64 has compile/publish coverage only.
- Native service/security checks on Windows Server 2022 and 2025.
- A ten-minute fuzz smoke, baseline simulations, three retained simulation
  cohorts and an aggregate check of all fifteen simulation methods.
- Signed development client installers for x64/Arm64 and the optional
  Windows Server Rendezvous installer for x64.

Successful installer jobs upload these artifacts, retained for 14 days:

| Artifact | Contents |
| --- | --- |
| `PeerSync-client-win-x64-<commit>` | x64 client setup EXE, source/hash manifest and public test certificate |
| `PeerSync-client-win-arm64-<commit>` | Arm64 client setup EXE, source/hash manifest and public test certificate |
| `PeerSync-rendezvous-win-x64-<commit>` | x64 server setup EXE, source/hash manifest and public test certificate |

Download them from the [Actions run](https://github.com/taskscape/PeerSync/actions/workflows/ci.yml).
CI packages use a temporary development signing identity and are experimental
test packages. Public release signing remains a separate requirement.

As checked on **2026-10-29**, the
[latest `main` run](https://github.com/taskscape/PeerSync/actions/runs/40822000853)
was denied before any job steps ran because an Actions budget prevented
further use. That run produced no installers or test results. Hosted
verification of the current revision remains unproven until CI can run;
local validation must be reported separately.

To build installers locally with a development signing identity:

```powershell
$thumbprint = 'YOUR_LOCALMACHINE_CODE_SIGNING_CERTIFICATE_THUMBPRINT'
./build/Build-Installer.ps1 -Arch x64 -Version 1.0.0 `
  -CertificateThumbprint $thumbprint -DevelopmentSigning
./build/Build-RendezvousInstaller.ps1 -Version 1.0.0 `
  -CertificateThumbprint $thumbprint -DevelopmentSigning
```

Use `-Arch arm64` for the Arm64 client. Output is written to
`artifacts/installer/`. The [installer guide](docs/installer-validation.md)
explains how to verify and trust a development certificate on isolated test
machines.

`./build/Publish-Service.ps1 -Runtime win-x64` produces an experimental service
and CLI ZIP without the dashboard or installer; its manifest retains
`gateP2Passed: false`. Follow the
[manual service runbook](docs/peersync-service-two-pc-runbook.md) for that
package. `PeerSync-P0-probes-*` artifacts are diagnostic test executables,
not the folder-sync application.

## Documentation

- [Implementation plan and acceptance gates](plan.md)
- [Design document](Resilio-style%20P2P%20Sync%20for%20Windows%20Implementation%20Plan.md)
- [Installer and two-PC validation](docs/installer-validation.md)
- [Rendezvous server operations](docs/rendezvous-operations.md)
- [Diagnostics, events and sanitized support bundles](docs/diagnostics.md)
- [Simulation and regression replay](docs/simulation.md)
- [Scale validation](docs/scale-validation.md)
