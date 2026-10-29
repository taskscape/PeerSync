# Physical Rendezvous server upgrade to 1.0.42.0

On 2026-10-28, the existing Server 2025 host `WIN-8UU4IA5PEQV`
(`100.95.151.81` over the management overlay) was upgraded through an
Administrator WinRM session. Before installation, the service
`PeerSyncRendezvous` was Running with file version 1.0.27.0. The host had the
current `sync.taskscape.com` certificate in both LocalMachine My and
WebHosting, thumbprint `97AC7FE3B681CBBBCE849450BA509FCA54427961`,
expiring 2027-01-22; the inventory reported a private key. The host identity
and service state were reviewed before the installer was released.

The exact development-signed installer was
`artifacts/installer/PeerSync-Rendezvous-Setup-1.0.42.0-x64.exe` with SHA-256
`3B4A7318706EC3E4F6B168776D2E6795735DBD1DA99CCD97CD0C48A074E0FDEC`.
Its local Authenticode check was `Valid` with signer thumbprint
`6AE1CBFC31F1DB189AF03A82D60C535AE8E0A7BD`. The transferred installer
hash matched on the server. The server's Authenticode status was
`UnknownError`, so this record does **not** claim a valid server-side
signature chain; it is a development certificate, not a release signer.

Silent setup exited 0 and retained its server log at
`C:\Temp\PeerSync-Rendezvous-1.0.42.0\setup.log`. The installed executable
reports 1.0.42.0 and the service is Running. The installed
`Test-RendezvousDeployment.ps1` exited 0: configuration points to
`sync.taskscape.com:99`, the selected machine certificate key was usable by
the checker, TCP and UDP listeners are owned by the service, local TLS health
passed, and its issues array is empty. That check did not directly inspect
the service identity's key ACL or external firewall filtering.

From the first PC after setup, `psp probe sync.taskscape.com:99` exited 0.
The correlated TCP session negotiated HTTP/2 and the correlated UDP session
negotiated HTTP/3, both `reachable: true`. Probe session ID:
`31a7da5a-1dfa-4437-ba81-2c1c02148de4`. The local WinRM service and
TrustedHosts settings were restored after the remote session. The ignored
field transcript and inventory are under
`artifacts/field/remote-rendezvous-upgrade-20261028-run-42`.
Their SHA-256 values are `216E0D240EA97448E7FB3006BD57425833C56DF425A47BCEDDCDEB0E23F5108F`
and `776F0A695CA00F3EA6062D43B0EAEA4CCF0BD7966CF538866B16F65D372D2347`,
respectively.

This is one deployed connectivity checkpoint. Independent outside-network
probing, relay load, native WAN/NAT soak, independent security review and
release signing remain open.
