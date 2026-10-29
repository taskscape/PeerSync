# Rendezvous server deployment and diagnosis

This is the P4 test procedure for a Windows Server central component and two
PeerSync PCs. It does not close the physical NAT, WAN or 72-hour soak gates in
`plan.md`. The central server carries presence and, when direct transport
fails, opaque end-to-end TLS relay traffic. It does not receive folder keys or
plaintext files.

## Prepare the central server

Use Windows Server 2022 with current cumulative updates or Server 2025. Point
the `sync.taskscape.com` A record at this server and add AAAA only if the
server has working public IPv6. Before installing, confirm port 99 is free:

```powershell
Get-NetTCPConnection -LocalPort 99 -ErrorAction SilentlyContinue
Get-NetUDPEndpoint -LocalPort 99 -ErrorAction SilentlyContinue
netsh http show urlacl | Select-String ':99/'
```

Obtain a Server Authentication TLS certificate for `sync.taskscape.com` in
`Cert:\LocalMachine\My`, with a private key. For a public deployment, configure
win-acme with DNS or HTTP validation and Windows certificate-store installation.
Set its post-renewal command to
`C:\ProgramData\PeerSync.Rendezvous\After-RendezvousRenewal.ps1`; the script
regrants the private-key ACL and restarts `PeerSyncRendezvous`. A self-signed
server certificate is suitable only for an isolated test where each client
explicitly trusts it. It is separate from the P-256 device certificates and
the code-signing certificate.

Build the x64 setup from the repository on the development PC. A test identity
in `LocalMachine\My` can be passed by thumbprint; `-DevelopmentSigning` exports
its public `.cer` next to the installer. Production release signing needs a
publicly trusted code-signing identity.

```powershell
./build/Build-RendezvousInstaller.ps1 -Version 1.0.0 `
  -CertificateThumbprint '<code-signing-thumbprint>' -DevelopmentSigning
```

Copy the resulting `artifacts\installer\PeerSync-Rendezvous-Setup-*.exe` to the
server and run it elevated. The setup checks the server certificate before
stopping an existing service. It installs under `NT SERVICE\PeerSyncRendezvous`,
grants that identity private-key access and Modify permission only on the
`%ProgramData%\PeerSync.Rendezvous\logs` directory, creates its
Application Event Log source, and adds inbound TCP/UDP 99 firewall rules.
It preserves the installed `appsettings.json` on upgrade. Run the newer setup
over the old version to upgrade; do not delete the data directory to upgrade.
The service identity must not be able to edit the administrator-run renewal
scripts in the parent data directory. Setup removes that inherited grant when
upgrading an earlier development installation.

### Replace a newer test build with a release package

The GitHub development-release workflow currently builds version `1.0.0.0`.
A server used for testing may already have a higher file version, so setup
rejects that package as a downgrade even if its source commit is newer.
Updated installers show both versions in the setup error and log.

For an intentional rollback, back up the installed `appsettings.json` outside
the installation directory, then run a setup containing the recovery switch
from an elevated PowerShell:

```powershell
$setup = '.\PeerSync-Rendezvous-Setup-1.0.0.0-x64.exe'
$process = Start-Process -FilePath $setup -ArgumentList '/ALLOWDOWNGRADE=1',
    '/LOG="C:\Windows\Temp\PeerSync-Rendezvous-setup.log"' -Wait -PassThru
$process.ExitCode
```

Only `/ALLOWDOWNGRADE=1` permits a lower version. Normal installs still reject
downgrades, and the switch does not bypass certificate or service-stop checks.
Setup preserves the existing `appsettings.json`, data directory and logs.
Check that the older service supports any settings added by the test build,
then verify the service and listeners using the commands below.

Packages built before this switch was added cannot use it. With those packages,
back up `appsettings.json`, uninstall **PeerSync Rendezvous** through Installed
apps, then run the downloaded setup. If uninstall requests a reboot, complete
it first. Uninstall stops and removes the rendezvous service and its firewall
rules, but retains `%ProgramData%\PeerSync.Rendezvous` and the server TLS
certificate. After installation, stop `PeerSyncRendezvous`, restore your saved
`appsettings.json` to its installation directory, and start the service again.
This interrupts discovery and relay sessions while the service is unavailable.

### Verify the installed service

```powershell
Get-Service PeerSyncRendezvous
Get-NetTCPConnection -State Listen -LocalPort 99
Get-NetUDPEndpoint -LocalPort 99
Get-NetFirewallRule -DisplayName 'PeerSync Rendezvous*'
```

Both Windows Firewall and the server provider must permit TCP and UDP 99.
`/healthz` reports HTTP service health. From a **different network**, run the
installed client CLI:

```powershell
psp probe sync.taskscape.com:99
```

The JSON must report `tcp.reachable` and `udp.reachable` true, with the
server-observed address. Exit code 0 requires both. If TCP works and UDP fails,
check provider UDP filtering and QUIC support. A local probe proves only the
server's own listeners, not external reachability.

## Configure two PCs

Install the signed PeerSync client setup separately on each PC and initialize
each client service. Never copy one PC's device certificate or private key to
another. The default `RendezvousServers` setting is
`https://sync.taskscape.com:99`; set each folder's `UseTracker` and `UseRelay`
to true and the device's `UseRelay` to true. `UsePortMapping` enables best-effort
UPnP/NAT-PMP for the client's TCP and UDP listener. Restart the service after
editing settings. Share only the folder capability through the intended
enrollment flow; the tracker gets a derived FolderId, not the capability.

For a controlled fallback test, first confirm a direct transfer works. Then
block the direct **peer listener port** between the two PCs while leaving
their outbound TCP 99 paths to the central server open. After five seconds the
initiating client should request a relay. Check the app's peer Route column for
`via relay`, and verify a file hash on the receiving PC. Restore the direct
firewall path after the test. Repeat from distinct home networks and a mobile
hotspot to close the WAN gate; a Hyper-V virtual LAN is supplemental only.

## Capture a failure

Run the probe and capture the command's JSON and exit code. Collect the server
bundle using `PeerSync.Rendezvous.exe diagnostics --output <new-zip>` and
each PC's `psp diagnostics` bundle immediately after the failed transfer.
The exact commands, event names, privacy rules and interpretation steps are in
[diagnostics.md](diagnostics.md). Compare the same UTC window and session ID
across the three bundles. Check `Get-WinEvent -LogName Application` for
`PeerSyncRendezvous` warnings/errors if the server service fails before a
journal is written. Keep the original transport and native error codes in the
report; do not send raw settings, databases, private keys or folder contents.
