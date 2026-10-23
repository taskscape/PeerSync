# P4 central-server deployment preflight, 2026-10-23

The rendezvous installer now includes `Test-RendezvousDeployment.ps1`. The
script reads the installed `appsettings.json`, SCM state, eligible server
certificate metadata, TCP/UDP socket ownership, active inbound firewall rules
and DNS. It makes one TLS-validated `/healthz` request pinned to loopback with
the configured public host as SNI. It emits one JSON object and exits nonzero
when a local check fails. No raw configuration, certificate thumbprint, key,
path or resolved address is included. A health request adds one normal debug
event; the script does not alter configuration or service state.

The script parsed without errors. Running it under Windows PowerShell 5.1 on
the development PC with the repository rendezvous settings produced the
expected nonzero result: `service-not-running`, absent TCP/UDP service-owned
listeners, missing local server certificate and firewall rules, and
`local-health-or-tls-failed` with curl exit code 7. Its JSON parsed successfully
and reported `localReady: false`. This is a negative-path control, not a
central-server deployment test.

Inno Setup 7.1 compiled the unsigned development rendezvous installer from
the existing published x64 staging tree and explicitly compressed
`Test-RendezvousDeployment.ps1`. The resulting package is retained locally in
`artifacts/installer/preflight/`. The staged executable predates this script
change; the compile establishes installer inclusion, not current-source
service behavior or release signing.

The installed Server 2025 check subsequently exposed a no-argument invocation
bug: Windows PowerShell 5.1 evaluated the `$PSScriptRoot` parameter default as
empty before the script body, and `Join-Path` failed. Supplying the install
directory explicitly passed with zero issues. The script now derives its
default directory inside the body from its own path.

A current-source, development-signed 1.0.14.0 x64 rendezvous setup was built
with that correction. Its SHA-256 was
`A9E3DFC992B9FC417F2B1A64296607C917E8A75D4FEABD18724B78D7D37BF2F1`;
the Server 2025 guest received identical bytes and verified Authenticode
before the silent upgrade. Setup exited 0, left `PeerSyncRendezvous` Running,
and its process owned TCP and UDP port 99. The installed preflight's SHA-256
matched the repository script
(`98B4748BDE051F5554B7C8246631FEED5BC7B26A16585076DF0F8D5CBF37B4F0`).
The packaged no-argument Windows PowerShell 5.1 command then exited 0 with
schema `peersync-rendezvous-deployment-v1`, `issues: []`, `localReady: true`
and `localHealth.tlsValidated: true`. The guest setup log and preflight result
are retained under `C:\Temp\PeerSync`; the host package, manifest, build log
and one-off verification command are under `artifacts/installer`.

This is server-local evidence for the installed package and its trusted
development signer. Outside-network TCP/UDP 99 admission, second-PC probe,
forced relay diagnosis and WAN gates remain open. Follow
[rendezvous operations](../rendezvous-operations.md) for the command and issue
interpretation.
