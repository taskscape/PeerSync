# Installed-service physical LAN throughput kit, 2026-10-28

Gate P2 still requires the production transfer scheduler to sustain at least
80% of a 1 Gbit/s LAN link. The earlier 1 GiB QuicBasics result measured the
transport spike, not the installed service. The three-PC physical soak is
running on the 1.0.43.0 clients; its synchronized corpus and scheduled tasks
must remain untouched until its final report is captured.

`build/field/Measure-InstalledLanThroughput.ps1` prepares a separate 4 GiB
random file in an already joined, empty two-PC folder. Both PCs must have the
same installed service version, one authenticated peer in this folder, direct
on-link 1 Gbit/s physical routes in both directions, and enough free space
for native content, PeerSync's block store and headroom. The script rejects
Tailscale or routed uplinks, folder content above the 10 GiB test limit, and
active execution beyond three hours. It stages the file outside the shared
folder and moves it in atomically after computing the source SHA-256.

The receiver's native file must reach the exact length and SHA-256. The
end-to-end rate is measured from the source move to the completed receiver
hash, including scan, network, application and verification costs. The gate's
80% line-rate threshold is 100,000,000 native bytes per second. To prevent a
LAN route preflight from being mistaken for actual transport selection, the
script also requires the receiver's installed-service journal to account for
at least the test file's bytes in authenticated `quic_block` or `tcp_block`
responses from the first PC's physical LAN address during the transfer. It
records the aggregate bytes, request count, transport and session IDs without
writing the folder key or file contents to the report.

After the soak, create and authorize a **new empty** shared test folder on
the first and second PCs. Keep the existing 9 GiB soak corpus in place. Use a
new run ID and an evidence directory on the source's volume, outside the
shared folder. Run a read-only `-PreflightOnly` pass, then execute the same
command without that switch. A typical invocation from the first PC is:

```powershell
$throughputParams = @{
    SourceRoot = 'C:\PeerSync-Gate-Evidence\lan-throughput-source'
    DestinationRoot = 'C:\PeerSync-Gate-Evidence\lan-throughput-destination'
    RemoteComputer = '100.88.55.83'
    RemoteLanAddress = '192.168.0.183'
    OutputDirectory = 'C:\PeerSync-Gate-Evidence\lan-throughput-reports'
    FileBytes = 4GB
    TimeoutSeconds = 10800
}
& .\build\field\Measure-InstalledLanThroughput.ps1 @throughputParams -PreflightOnly
& .\build\field\Measure-InstalledLanThroughput.ps1 @throughputParams
```

The addresses above are an example from the earlier physical LAN check; use
the actual on-link address at execution time. The preflight and run do not
create or share the folder and do not approve a device. The run removes only
its own incomplete staging file on failure and leaves the synchronized test
file and JSON report for audit; clean the test folder only after saving the
evidence. **No throughput result is claimed by this preparation.** The
separate native multicast, sleep/recovery and three-PC soak requirements stay
open until their own physical evidence is complete.
