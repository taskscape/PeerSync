# Installed two-guest central and relay check, 2026-10-23

This check used two disposable Hyper-V guests on the same Default Switch:
Windows Server 2025 Datacenter Evaluation (`PeerSync-P3-Server2025-Clean`)
running the installed 1.0.12 client and 1.0.14 rendezvous service, and
Windows Server 2022 Datacenter Evaluation build 20348 (`PeerSync-P3-Server2022-Clean`)
running the installed 1.0.13 client. The 2022 package was copied byte-for-byte,
matched SHA-256 `CB55BF3E217D1ED31F92EF9196C3FCDEEE603FEE690BDBC11AEC1B10E98E3729`,
had a valid development Authenticode signature in the guest, installed
silently with exit 0, started `PeerSync`, and answered the default verified
`psp status --json` CLI. Each guest retained its own distinct device identity;
only the folder capability was shared during enrollment, without printing it
to the console or saving it in this evidence.

An elevated folder grant and installed CLI Add/Join created a source folder
on Server 2025 and an empty destination on Server 2022. With each peer's
address configured using `psp hosts`, a 1,048,576-byte file moved from 2025 to
2022 and matched SHA-256
`E1EDB4FCBB6AC72B240C16B2741D6105C6ECA3A82D0FE47159B1708BE26A85B4`.
A separate 524,288-byte file moved back and matched
`790400D395F6CADF9AA3D997411DE2FD6170A42575C24968D623EA674128EB55`.
Both installed CLIs listed the other device as pinned. The client listeners
were TCP/UDP 34897 on Server 2025 and 22496 on Server 2022.

For central admission, the Server 2025 guest's public, self-signed
`CN=sync.taskscape.com` test certificate was imported into the *disposable
Server 2022 guest's* LocalMachine Root store; its private key was never
exported. That guest's hosts file maps `sync.taskscape.com` to the Server 2025
guest's Default Switch address for this test. Its installed `psp probe`
returned exit 0: TCP HTTP/2 and UDP HTTP/3 were both reachable and both
echoed the same correlation ID. This was an internal VM path, not provider
port-99 admission.

Both clients then cleared `psp hosts`, disabled LAN discovery in backed-up
device settings, and restarted the installed services. Their new service
journals recorded `tracker.session_connected` and
`tracker.peer_list_received`. With neither LAN discovery nor a predefined
address, a new 262,144-byte file arrived and matched
`33D6CF63FAD13B4FCE7AFC4778B8DFBEAEBEA9B1AA13230512226527B6534723`.
The tracker-advertised local endpoint was still directly reachable on this
virtual switch; this step verifies central introduction, not relay.

For a forced relay, temporary Windows Firewall block rules denied TCP and UDP
inbound to **both** peer listener ports from the other VM. The Server 2022
guest still reached central TCP 99. Both client services restarted with no
LAN discovery or predefined hosts, and a new 131,072-byte file arrived with
matching SHA-256 on both guests:
`798E6A6D76D3EB057556AB647042ADC0D386C810511A9F87D9FBB48731A8A114`.
The current client journals contain `connection.candidate_started` for relay,
`tracker.relay_offer_received`, `transport.authenticated` with transport
`Relay`, and `service.folder_peer_ready` with transport `Relay`. The central
journal contains `rendezvous.relay_offered` and `rendezvous.relay_joined`.
Sanitized bundles have four matching recent relay session IDs between each
client and the central bundle, and eight between the two clients. Transient
connect/stream/relay transport failures also appeared during restarts and
duplicate candidate attempts; they did not prevent the verified transfer.
The temporary firewall rules were removed from both guests and both client
services were Running afterward.

Three `psp diagnostics`/central diagnostic bundles are retained under
`artifacts/vm/two-guest-2026-10-23/`, together with one-off control scripts
under `artifacts/vm/`; guest logs and package setup logs remain under
`C:\Temp\PeerSync`. The bundle review found no test folder path or file name,
share-key field, or either VM IP address. The guest-specific trust and hosts
mapping, disabled LAN discovery, test folders and backed-up settings remain
inside the disposable VMs for follow-up; none changed the host OS.

This validates an installed two-guest path through direct QUIC/TCP candidate
selection, tracker introduction and a forced encrypted relay on one virtual
switch. It does not satisfy the physical two-PC LAN gate, the home/mobile NAT
matrix, provider-side TCP/UDP 99 admission, WAN relay load cap, 72-hour soak,
clean Windows 11 or production-signer requirements. The packages used a
development signer and their manifests identify revision `fd3bfa3` with a
dirty build tree; the later committed installer fixes are verified separately
in the [uninstall cycle](P3-server2022-uninstall-cycle-2026-10-23.md) and
[packaged preflight](P4-central-deployment-preflight-2026-10-23.md).
