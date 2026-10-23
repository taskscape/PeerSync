# PeerSync user guide

PeerSync synchronizes folders between Windows PCs. Each PC runs its own
service and has its own device identity. The dashboard is the interactive
client; the service continues after the dashboard closes. The central
rendezvous server helps peers find each other and can relay encrypted peer
traffic when a direct route is unavailable.

## Add or join a folder

Install PeerSync on both PCs. Open the dashboard and use **Add** on the
first PC. Use **Share** to obtain the folder key, then **Join** with
that key and an empty destination on the second PC. A key grants access to
the folder; transmit it only through a trusted channel and do not include it
in diagnostics or screenshots. Each PC must retain its own device
certificate; copying an installation's private identity to a second PC is
unsupported.

The CLI offers the same enrollment operations:

```powershell
& 'C:\Program Files\PeerSync\cli\psp.exe' add 'C:\PeerSync\Work'
& 'C:\Program Files\PeerSync\cli\psp.exe' join '<folder-key>' 'C:\PeerSync\Work'
```

`add` prints the share key. `psp share <folder>` displays it again for an
authorized local account. Avoid `--json` when capturing terminal output for
support because the JSON also contains the key. Set **Require approval for
new devices** in folder Settings if you want each newly seen identity to wait
for a decision. Review the full device fingerprint in the dashboard's Pending
list before approving. Blocking an identity prevents its future sessions.
Changing folder policy in Settings briefly reconnects that folder's peers so
the new approval, ignore, archive and scan rules take effect. Other folders
stay registered, though shared peer sessions may reconnect. If the change
leaves the folder paused, fix the reported folder error and select **Resume**;
the saved policy is retained.

## Watch transfers and routes

The folder list shows registered folders. Select a folder to see peers,
activity, overwritten versions and settings. The peer Route field identifies
a direct QUIC/TCP or relayed connection. The status view reports service and
clock health; a clock warning can pause admission until the machine's time is
correct. The service scans on startup and follows local file changes, while
periodic rescans cover missed watcher events.

For a terminal check, use `psp status --json` and `psp peers <folder> --json`.
To provide a known peer address directly, use
`psp hosts <folder> <host:port>`; `psp hosts <folder> --clear` removes the
configured list. The default rendezvous address is
`sync.taskscape.com:99`; `psp probe sync.taskscape.com:99` checks both HTTP/2
and HTTP/3 reachability from this PC. A successful probe does not by itself
prove that two peers can exchange files.

## Recover an older version

Select a folder and open **Archive**. The list shows relative path, archive
date in local time and size. Select a version and choose **Restore** to
recover it at the original path, or **Restore as copy** and enter a new
relative path inside the same folder. A restore creates a new local version
for normal synchronization. It does not silently replace the current version
without retaining it. The Archive status says when the list is truncated;
older entries may exist even if they are not shown. The **Overwritten** tab
shows the subset replaced by incoming versions and has the same restore
actions.

## Diagnose a problem

Check the service state and status first. Collect a diagnostic ZIP promptly
after a failure:

```powershell
& 'C:\Program Files\PeerSync\cli\psp.exe' diagnostics --output 'C:\Temp\peersync-client.zip'
```

Send the matching time window and session ID from both PCs and, for relay
problems, the central server. Review the ZIP before sharing it. Do not send
folder keys, private certificates, database files or folder contents. See
[diagnostics.md](diagnostics.md) for log meanings and
[rendezvous-operations.md](rendezvous-operations.md) for central-server
troubleshooting. Physical LAN, home NAT, mobile WAN and long-soak acceptance
remain open in [plan.md](../plan.md); this guide does not imply a v1 release.
