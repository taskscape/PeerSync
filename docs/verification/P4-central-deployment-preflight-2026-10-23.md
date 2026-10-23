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

The actual Server 2022/2025 installation, certificate trust and key ACL,
server-local passing result, provider TCP/UDP 99 admission, outside-network
`psp probe`, forced relay diagnosis and WAN gates remain open. Follow
[rendezvous operations](../rendezvous-operations.md) for the server command and
issue interpretation.
