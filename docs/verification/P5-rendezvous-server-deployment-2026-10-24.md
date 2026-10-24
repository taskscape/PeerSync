# Rendezvous Windows Server 2025 deployment, 2026-10-24

The `sync.taskscape.com` server at `100.95.151.81` accepted the renewed
Let's Encrypt certificate `97AC7FE3B681CBBBCE849450BA509FCA54427961` in
`LocalMachine\My`. The revised Windows PowerShell 5.1 certificate check passed
against its machine private key. Installer 1.0.17.0, SHA-256
`914F08B8D37691D3F7A444B57407761020C8282A12942B15F4D3823E7F4C39E1`,
then installed with exit code 0. The installer setup log is
`C:\Temp\PeerSync-Rendezvous-1.0.17-setup.log` on the server.

The packaged deployment check exited 0 with `localReady: true`: the service
was running under `NT SERVICE\PeerSyncRendezvous`, owned TCP and UDP 99,
had both inbound firewall rules, resolved DNS, and passed loopback TLS health.
From the development PC, `psp probe sync.taskscape.com:99` returned exit code
0 with correlated HTTP/2 and HTTP/3 responses, both before and after the
post-renewal service restart. This is a successful probe from that PC; a
second independent network and two-client synchronization remain separate
physical gates.
The later [route inspection](P4-public-probe-route-2026-10-24.md) found a
private observed address matching the first PC's Ethernet gateway, so this
probe must not be counted as independent provider admission.

The win-acme renewal `eh3gEm7dR0GbVSO6tazd0A` was backed up on the server,
then configured to store the next certificate in `LocalMachine\My` and run
`C:\ProgramData\PeerSync.Rendezvous\After-RendezvousRenewal.ps1` after its
existing IIS installation step. `wacs.exe --list` exited 0 and found the
renewal. The post-renewal script was run against the current certificate:
it regranted key access, restarted the service, and exited 0. A future real
win-acme issuance has not yet exercised the new configuration.

The field collector created
`C:\Temp\PeerSync-Rendezvous-1.0.17-evidence\deployment-evidence.json` and
`rendezvous-diagnostics.zip` on the server; copies are under
`artifacts/field/rendezvous-server-20261024/`. Its only issue was
`binary-signature-invalid`: the development signing certificate is not trusted
on the server, so Windows reports `UnknownError` for Authenticode. The installer
hash matched the locally signed build, and the installed service passed all
runtime checks. Publicly trusted production code signing remains outstanding.

The temporary `LocalAccountTokenFilterPolicy=1` used for remote setup was
removed after the work. The prior value was absent. The development PC's
WinRM service and TrustedHosts setting were also restored.
