# Resilio-style P2P Sync for Windows: Implementation Plan

Oct 18, 2026 · @Maciej Zagozda

## Summary

Build **PeerSync** (working name): a Windows service that keeps chosen folders identical across computers peer to peer, plus a small WPF app to pick those folders. It copies Resilio Sync's model (secret folder keys, LAN plus tracker discovery, relay fallback, block-level swarming, an archive of old versions) but uses its own documented wire protocol.

Key decisions:

- **Own protocol, not wire-compatible with Resilio.** Resilio's protocol is closed and undocumented. Interoperating would mean reverse-engineering a moving target, likely against its licence terms. This plan reproduces the behaviour, not the bytes.
- **.NET 10 (LTS) end to end.** The sync engine runs inside a Worker Service registered as a Windows service. The WPF app is only a remote control for it.
- **QUIC transport with a TCP + TLS fallback.** QUIC (built into Windows 11 and Server 2022) gives encryption, multiplexed streams and UDP hole punching in one stack. It plays the role Resilio's uTP-over-UDP plays.
- **One self-hosted server, optional.** It runs a tracker (who holds which folder) and a relay (for peers that cannot connect directly) as one Windows service on the existing taskscape.com server, reachable as sync.taskscape.com on port 99. It never sees file contents or folder keys. LAN-only sync works without it.
- **SQLite metadata per folder, version vectors per file.** Changes are found with a file watcher plus periodic rescans. When two devices change the same file, the latest version wins and the other goes to the archive.

Assumptions: Windows 11 and Windows Server 2022 or later, which .NET's QUIC support requires (decided: no Windows 10); one developer or a small team; mobile apps, a web UI and Resilio's paid team features are out of scope. Each is open to change. Estimate: about 26 weeks to version 1.0 for one senior .NET developer (see Phased roadmap).

## How Resilio Sync works

Resilio Sync is a serverless, BitTorrent-derived sync engine: one random folder key names the folder, grants access and seeds the encryption. Peers find each other by LAN multicast, a vendor tracker or fixed addresses, then swap signed pieces over encrypted uTP or TCP sessions, using a relay only when no direct path exists. The current release is 3.1.2 (October 2025), and Nasuni bought Resilio in March 2026.

| Area | What Resilio does | Source |
| --- | --- | --- |
| Folder key | 33 Base32 characters (A–Z, 2–7). The first letter is the type, the rest is 20 random bytes. Types: A read-write, B read-only, C one-time, D read-write that can seed encrypted peers, E read-only matching D, F encrypted, M identity. | [Key structure](https://help.resilio.com/hc/en-us/articles/206767810-Key-structure-and-flow) |
| Key chain | RW key → Ed25519 key pair (signs file hashes) → RO key from the public key → Share ID from the RO key → a fresh traffic key per session from the RO key. Built on Ed25519 and SHA-3. | [Key structure](https://help.resilio.com/hc/en-us/articles/206767810-Key-structure-and-flow) |
| Encrypted key | 65 characters: a 20-byte data-access key plus a 20-byte encryption key. An F peer stores and seeds AES-128 ciphertext and encrypted names but can't read or change them. Clear-text peers encrypt with a storage key before sending, so data is encrypted twice in transit. | [Encrypted folders](https://help.resilio.com/hc/en-us/articles/207370466-Encrypted-folders), [blog](https://www.resilio.com/blog/understanding-the-sync-encrypted-folder) |
| Advanced folders | Each user has an identity key pair. Each folder gets its own X.509 root CA, signed by its creator. Owners sign per-user certificates (read-only, read-write or owner), and certificates sync to every peer. Revoking access issues a new certificate, and old ones are kept as history. | [Security brief](https://www.resilio.com/docs/Resilio_Sync_Security_and_Privacy_Brief.pdf) |
| Share links | `https://link.resilio.com/#f=name&sz=size&t=type&s=folderID&i=tempKey&e=expiry&v=version`. Everything after `#` stays in the browser. The requester sends its public key, and the owner approves after checking the fingerprint. Links expire after a set number of days or uses. | [Link structure](https://help.resilio.com/hc/en-us/articles/204754739-Link-structure-and-flow) |
| Bootstrap | Downloads `https://config.resilio.com/sync.conf` to learn tracker and relay addresses, then refreshes it every 3,600 s. | [Ports](https://help.resilio.com/hc/en-us/articles/204754759-What-ports-and-protocols-are-used-by-Sync), [Power prefs](https://help.resilio.com/hc/en-us/articles/207371636-Power-user-preferences) |
| Tracker | The peer sends its Share IDs, peer ID and local IP:port over TCP or UDP. The tracker adds the external IP:port it sees and returns the other peers' internal and external addresses. A peer on the same subnet is tried on the LAN first. | [Key structure](https://help.resilio.com/hc/en-us/articles/206767810-Key-structure-and-flow), [Security brief](https://www.resilio.com/docs/Resilio_Sync_Security_and_Privacy_Brief.pdf) |
| LAN discovery | UDP multicast to 239.192.0.0:3838 plus subnet broadcast. A peer announces its Share IDs and listening port at start-up and whenever the network changes. | [Ports](https://help.resilio.com/hc/en-us/articles/204754759-What-ports-and-protocols-are-used-by-Sync) |
| Other discovery | Predefined hosts: `IP:port` or `DNS:port` pairs per folder. An optional DHT, off by default. BitTorrent Sync 1.x used the mainline BitTorrent DHT. | [Security brief](https://www.resilio.com/docs/Resilio_Sync_Security_and_Privacy_Brief.pdf), [network study](https://arxiv.org/abs/1409.8489) |
| Transport | One listening port for both TCP and UDP. Tunnel protocols are `utp` (BitTorrent's LEDBAT-paced UDP), `utp2` (a WAN-tuned variant), `tcp`, `relay` and `inproc`. Ports are mapped through UPnP (239.255.255.250:1900) and NAT-PMP (gateway:5351). | [Power prefs](https://help.resilio.com/hc/en-us/articles/207371636-Power-user-preferences), [Ports](https://help.resilio.com/hc/en-us/articles/204754759-What-ports-and-protocols-are-used-by-Sync) |
| Session crypto | A TLS tunnel using SYNC-SRP, DHE-PSK-AES128-GCM-SHA256 or DHE-PSK-AES256-GCM-SHA384. The pre-shared key comes from the folder key, and the DHE exchange gives forward secrecy. LAN encryption can be switched off. | [Power prefs](https://help.resilio.com/hc/en-us/articles/207371636-Power-user-preferences), [Security brief](https://www.resilio.com/docs/Resilio_Sync_Security_and_Privacy_Brief.pdf) |
| Relay | Used only when hole punching fails, for example behind a symmetric NAT. It forwards encrypted pieces and stores nothing. The vendor says over 97% of data still moves directly. | [Relay](https://help.resilio.com/hc/en-us/articles/204754779-What-is-a-Relay-Server), [Security brief](https://www.resilio.com/docs/Resilio_Sync_Security_and_Privacy_Brief.pdf) |
| Data transfer | Files split into fixed pieces of 32 KB to 2 MB, scaled to file size. Pieces download from many peers at once, BitTorrent style, and only changed pieces move. An insertion that shifts every piece re-sends the whole file. Files smaller than the current speed go whole ("direct torrent"). | [Delta FAQ](https://help.resilio.com/hc/en-us/articles/206217095-When-a-file-changes-does-Resilio-Sync-transfer-the-entire-file-again-or-just-the-part-that-s-changed), [Power prefs](https://help.resilio.com/hc/en-us/articles/207371636-Power-user-preferences) |
| Change detection | OS file notifications, plus a full rescan every 600 s. Locked files are rechecked every 600 s. Transfers stop when peer clocks differ by more than 600 s. | [Power prefs](https://help.resilio.com/hc/en-us/articles/207371636-Power-user-preferences) |
| On-disk layout | A storage folder (`%APPDATA%\Resilio Sync\`, or the service profile when run as a service) holds settings and one SQLite database per share. Each synced folder has a hidden `.sync` holding `ID`, `IgnoreList`, `StreamsList` and `Archive\`. Downloads land as `*.!sync` temp files, then are renamed into place. | [Storage folder](https://help.resilio.com/hc/en-us/articles/206664690-Sync-Storage-folder), [.sync folder](https://help.resilio.com/hc/en-us/articles/206217185-What-is-sync-folder-and-StreamsList-IgnoreList-and-Archive-inside) |
| Archive and renames | Deleted or overwritten files move to `.sync\Archive` for 30 days, for files up to 1,000 MB. For a rename, the receiver archives the old name, then restores the file from the archive by hash when the new name shows up, so nothing is re-downloaded. | [Rename](https://help.resilio.com/hc/en-us/articles/209606526-What-happens-when-file-is-renamed), [Power prefs](https://help.resilio.com/hc/en-us/articles/207371636-Power-user-preferences) |
| Conflicts | The latest version to come online wins, even if it was edited earlier offline. The losing version goes to the Archive, and no conflict copy is made. A `.Conflict` suffix is used only for name clashes, such as case differences. | [Conflicts FAQ](https://help.resilio.com/hc/en-us/articles/204754209-What-if-several-people-make-changes-to-the-same-file), [Power prefs](https://help.resilio.com/hc/en-us/articles/207371636-Power-user-preferences) |
| Ignore rules | `.sync\IgnoreList` is UTF-8, one rule per line, case-sensitive. It supports `*`, `?` and `**`, `#` comments, and root-anchored paths. It is re-read on change and is not synced between peers. | [IgnoreList](https://help.resilio.com/hc/en-us/articles/205458165-Ignoring-files-in-Sync-Ignore-List) |
| Sync modes | Disconnected, Selective (unsynced files appear as `.rsls` placeholders and download when opened) or Synced. A private identity links your devices, so they share one folder list. | [Sync modes](https://help.resilio.com/hc/en-us/articles/205457775-Synchronization-Modes) |
| Old wire format | In BitTorrent Sync 1.x, UDP datagrams began with `BSYNC\0` followed by bencoded dictionaries (`m`=`ping`/`get_peers`/`peers`, a 20-byte peer ID, the share ID, 6-byte IP:port fields). Clients sent `get_peers` every second per share. The protocol changed in v2. | [Network study](https://arxiv.org/abs/1409.8489) |

Not publicly documented, so this plan designs its own version of each:

- The v2/v3 message format, handshake and relay framing
- How the PSK, the storage key and encrypted file names are derived
- The piece-hash tree ("tree nodes" in the settings) and how file versions are recorded
- The DHT variant Resilio runs today

## Scope and key design decisions

Version 1 copies Resilio's standard-folder model (folder keys, multi-path discovery, swarming, archive). Identity-based sharing, encrypted peers and on-demand placeholders come in later phases. Where Resilio's behaviour is weak (fixed-offset pieces, optional plain-text LAN traffic), the plan does better and says so.

| Decision | Choice | Why |
| --- | --- | --- |
| Wire compatibility | No. Our own versioned protocol, `PSP/1` (PeerSync Protocol). | Resilio's protocol is closed, changed between v1 and v2, and reverse-engineering it is legally risky. Copying its behaviour gets the same user experience. |
| Access model | Folder keys like Resilio's standard folders: read-write, read-only and encrypted, derived one way from the RW key. | No accounts and no server-side permissions. Whoever holds a key has that level of access, which is exactly Resilio's model. |
| Write authority | Every change record is signed with an Ed25519 key derived from the RW key. Read-only and encrypted peers verify but cannot sign. | Resilio does the same ("sign and verify file hashes"). A read-only peer can't inject changes even if it is modified. |
| Transport | QUIC (`System.Net.Quic`, TLS 1.3) first, then TCP + TLS 1.3, then relay. | QUIC gives encryption, multiplexed streams, congestion control and UDP hole punching in one in-box stack. It plays the role of Resilio's uTP. |
| Peer authentication | Each device has a self-signed TLS certificate. After the handshake, both sides prove they hold the folder's auth key with an HMAC bound to both certificate hashes. | .NET has no TLS-PSK, so this replaces Resilio's DHE-PSK suites. TLS 1.3 still gives forward secrecy. |
| Discovery | LAN multicast, our own tracker, predefined hosts. DHT is optional and later. | This matches Resilio's defaults: its DHT is off by default. |
| Central server | One self-hosted ASP.NET Core Windows service, tracker plus relay, on the taskscape.com server at port 99 (TCP and UDP). It never sees keys or plaintext. | Needed for peers on different networks. LAN-only sync works without it. |
| Chunking | Content-defined chunks (FastCDC, about 64 KiB average) instead of fixed offsets. | Resilio re-sends the whole file when an insertion shifts every piece. Content-defined chunks re-send only the region that changed. |
| Conflicts | Version vectors detect true concurrent edits. The latest version wins and the other goes to the archive, as in Resilio. | Resilio's "latest to come online wins" can let an older offline edit beat a newer one. Here the newest edit wins, and nothing is lost because the loser is archived. |
| UI | WPF app as a client of the service over a named pipe. No web UI. | That is what was asked for, and it avoids exposing a local HTTP port. |

Confirmed on 2026-10-18: no Windows 10 support, latest version wins on conflicts, and one self-hosted server on the taskscape.com machine, with the relay and tracker on port 99 because the website already uses port 80. Also confirmed: Windows Server 2025 or the latest release on that machine, host name sync.taskscape.com, and TCP 99 allowed by the hosting provider (UDP 99 assumed, verified in phase 4).

Out of scope for all phases unless decided otherwise: mobile and macOS/Linux clients, Resilio's paid business features (central management, file locking, WAN acceleration), and interoperability with real Resilio peers.

## System architecture

Every PC runs the same Windows service, and the WPF app is only a remote control for it. The rendezvous server introduces peers and relays as a last resort. File data always flows peer to peer, from every peer that holds it at once.

&#91;embedded content: system architecture · two peers and the optional rendezvous server\]

On one LAN, peers find each other by multicast and need no server. Across the internet they meet through the tracker, then connect directly or, failing that, through the relay, with TLS still end to end.

## Protocol specification (PSP/1)

PSP/1 is a QUIC application protocol (ALPN `psp/1`) with Protobuf messages on multiplexed streams. Access is proven by keys derived from the folder's RW key, exactly as Resilio derives RO key, Share ID and session key from it.

### Keys and identifiers

```text
S_rw       = 32 random bytes (RandomNumberGenerator)
(sk, pk)   = Ed25519 key pair from HKDF-SHA256(S_rw, info="psp1 sign")
K_content  = HKDF-SHA256(S_rw, info="psp1 content")      // 32 bytes
RO key     = pk || K_content                             // 64 bytes
F key      = pk                                          // 32 bytes, encrypted peers
K_auth     = HKDF-SHA256(pk, info="psp1 auth")
K_ro_auth  = HKDF-SHA256(K_content, info="psp1 ro-auth")
FolderId   = SHA-256("psp1 folder" || pk)                // public lookup id
DeviceId   = SHA-256(device certificate SubjectPublicKeyInfo)
```

| Item | Text form | Held by | Grants |
| --- | --- | --- | --- |
| RW key | `A` + Base32(S\_rw) + 2-char checksum, 55 chars | Read-write devices | Sign changes, read, decrypt |
| RO key | `B` + Base32(pk ‖ K\_content) + checksum, 106 chars | Read-only devices | Verify, read, decrypt |
| F key | `F` + Base32(pk) + checksum, 55 chars | Untrusted backup devices | Verify, store and seed ciphertext only |
| FolderId | 32 bytes, never shown | Tracker, LAN | Find peers; nothing else |
| DeviceId | 52 Base32 chars in groups of 4 | Every device | Identifies a device; pinned per folder |

The checksum catches typos when a key is pasted. Use CRC-10/ATM over the
canonical uppercase ASCII prefix followed by the raw payload: width 10,
polynomial 0x233, initial/final XOR zero, no reflection. Encode its high five
bits and low five bits as the final two RFC 4648 Base32 symbols. Reject nonzero
unused Base32 padding bits and incorrect lengths. Input is case-insensitive
and ignores whitespace/dashes. This preserves the 55/106/55-character forms
and detects every single-symbol change that changes canonical key material;
case and spacing changes preserve the same key by design. The earlier draft's
truncated SHA checksum cannot guarantee the stated single-character typo
rejection: a synthetic collision was reproduced before implementation. CRC
is only an input-error check; HKDF/Ed25519/HMAC provide the security properties.
Keys are shared out of band, or later as links and QR codes. Key text is never
logged, and key objects redact their string/JSON representations. Key objects
own their private buffers and native signing keys and clear them on disposal;
key material is exposed through explicit exports returning defensive copies.
Callers must clear exported secret byte arrays and avoid retaining exported
key strings, which cannot be reliably erased from managed memory.

### Discovery

| Method | How | When |
| --- | --- | --- |
| LAN | UDP multicast to 239.255.80.83:47820 (IPv4) and ff12::5053 (IPv6), plus subnet broadcast. Packet: magic `PSP1`, DeviceId, listening port, and per folder a 16-byte tag = HMAC(K\_auth, "lan" ‖ current hour). | At start, on `NetworkAddressChanged`, then every 30 s. Rotating tags stop LAN observers from tracking folders. |
| Tracker | Persistent gRPC stream to the rendezvous server. Sends DeviceId, FolderIds, local endpoints. Server adds the public endpoint it observes and pushes peer lists back. | Re-announce every 5 min, entry TTL 15 min. Push on change. |
| Predefined hosts | `host:port` list per folder, DNS resolved every 60 s. | Always, if set. |
| DHT | BitTorrent mainline DHT (BEP 5) with info-hash = first 20 bytes of HMAC(K\_auth, "dht"). | Later phase, off by default, as in Resilio. |

### Connection and handshake

1. **Dial in parallel**, happy-eyeballs style, 250 ms apart: LAN addresses, then public addresses, then a coordinated hole punch, then relay. The first authenticated connection wins and the rest are cancelled.
2. **TLS 1.3 over QUIC.** Each device presents a self-signed ECDSA P-256 certificate created at install. Both sides accept any certificate but record its SPKI hash as the DeviceId.
3. **Hello** on stream 0: protocol version, DeviceId, device name, client version, capability flags, 32-byte random nonce.
4. **FolderAuth** for each folder both sides announce: `HMAC(K_auth, "psp1 auth" ‖ sender DeviceId ‖ receiver DeviceId ‖ sender nonce ‖ receiver nonce ‖ FolderId)`.
   1. A read-only or RW peer adds the same MAC under K\_ro\_auth, proving it may receive plaintext.
   2. An RW peer adds an Ed25519 signature over the transcript with sk.
   3. Binding the MAC to both certificate hashes defeats a man in the middle, who would have to present a different certificate. Putting the sender first stops an attacker reflecting a peer's own proof back to it. Both DeviceIds come from the TLS certificates, never from Hello.
5. **IndexSummary**: per folder, the highest sequence number each side has seen from the other. Incremental index exchange follows.

A folder can optionally require approval: unknown DeviceIds are held as Pending until approved in the UI, like Resilio's "Approve all peers".

### Messages

Each stream carries frames of `varint length ‖ 1-byte type ‖ Protobuf body`. Stream 0 is control. Each folder gets one index stream. Block requests use a pool of short-lived streams, so one slow request never blocks another.

| Message | Purpose |
| --- | --- |
| `Hello`, `FolderAuth`, `FolderAuthResult` | Handshake (above) |
| `IndexSummary` | Per-folder version summary on connect |
| `IndexRequest {folderId, afterSeq}` | Ask for records the remote changed since a sequence number |
| `IndexUpdate {folderId, records[], lastSeq}` | Batches of up to 1,000 signed FileRecords |
| `Availability {folderId, path, chunkBitmap}` | Chunks a peer holds of a file still downloading, for swarming |
| `BlockRequest {folderId, chunkHash, length}[]` | Batched requests, up to 64 per frame |
| `BlockResponse {chunkHash, data \| errorCode}` | Data, checked against its hash before use |
| `Ping` / `Pong` | Keep-alive every 30 s, RTT sample |
| `Close {reason}` | Orderly shutdown with a reason code |

**FileRecord** fields: path (relative, `/`-separated, Unicode NFC), type (file, directory, symlink), size, mtime (100 ns UTC ticks), Windows attributes (read-only, hidden), deleted flag, version vector, ordered chunk list `(length, BLAKE3 hash)`, whole-file BLAKE3 hash, modifying DeviceId, local sequence number. Records are signed with sk over their canonical Protobuf encoding, excluding the local sequence number. Peers forward other devices' records with the original signature. Receivers drop any record whose signature fails.

### Block transfer and swarming

- **Chunking:** FastCDC with a size class per file, stored in the record so every peer agrees. About 64 KiB average for files under 256 MiB, 512 KiB under 8 GiB, 2 MiB above that.
- **Local reuse first:** before requesting anything, look up each needed chunk hash in the local chunk index. This turns renames, moves and copies into local copies, as Resilio's archive-by-hash trick does.
- **Scheduler:** missing chunks are spread across every peer that has them. Each peer gets an adaptive window of outstanding bytes (starting at 4 MiB, capped at 32 MiB) sized from RTT and throughput. The last few chunks of a file go to two peers at once (endgame mode).
- **Small files:** files of one chunk are requested 64 at a time to cut round trips, Resilio's "direct torrent" idea.
- **Writing:** chunks go to a hidden temp file next to the target (`name.ext.!psp`), verified chunk by chunk, then the whole-file hash. The file is moved into place only if the local copy hasn't changed since the last scan.
- **Rate limits:** global and per-peer token buckets, with LAN peers optionally exempt.

### NAT traversal

1. Map the listening UDP and TCP port on the router with UPnP or NAT-PMP (`Mono.Nat`).
2. Learn the public UDP endpoint from the rendezvous server, which reports the address it sees.
3. To punch a hole, the server tells both peers to dial each other's public endpoint at the same moment, from their listening port.
4. If no direct path appears within 5 s, use the relay. The relay pipes bytes and the peers run TLS 1.3 end to end inside it, so the relay sees only ciphertext.

Open technical question: step 3 needs outgoing QUIC connections to leave from the same UDP port as the QUIC listener. Phase 0 must prove `System.Net.Quic` allows this. If it doesn't, call MsQuic directly or rely on TCP plus the relay for hard NATs.

P0 local result (2026-10-18): outgoing QUIC on the listener port failed with WSAEADDRINUSE on Windows build 26300, .NET 10.0.13 / MsQuic 2.5.11. Direct MsQuic with shared binding and TCP simultaneous open also failed locally. Provisionally retain direct QUIC/TCP for reachable endpoints and relay fallback after 5 seconds for hard NATs. Do not advertise coordinated punching until the physical NAT matrix confirms a supported option. See `docs/spikes/P0.2.2-quic-punch.md`; that matrix remains open.

User-approved implementation baseline (2026-10-19): proceed with reachable
direct QUIC/TCP connections and relay fallback after five seconds while the
native NAT matrix is pending. Working coordinated punching is not claimed.
The final native transport decision and full required matrix remain mandatory
before the WAN gate, including a real relay fallback check.

## Sync engine design

Each folder runs an independent loop: detect local changes, publish them as signed records, pull what remote peers have that is newer, and apply it safely. The model follows Resilio's (a database per share, watcher plus 600 s rescans, archive on overwrite) with version vectors added so concurrent edits are detected and the losing version is always archived.

### Folder life cycle

A folder is in one of these states: **Paused**, **Scanning**, **Idle**, **Syncing** (pulling or serving), or **Error** (path missing, disk full, `.psp\id` mismatch). Errors retry with exponential backoff from 10 s to 10 min. Low disk space (default 1 GiB free) pauses downloads but keeps serving, like Resilio's free-space threshold.

### Metadata database

One SQLite file per folder at `%ProgramData%\PeerSync\state\folders\<FolderId>.db`, in WAL mode, accessed through `Microsoft.Data.Sqlite` with hand-written SQL on the hot paths. It lives outside the synced folder, as Resilio's storage folder does, so it never syncs itself. A separate state\\peersync.db holds the folder list, per-folder settings, protected folder keys and known devices.

| Table | Holds |
| --- | --- |
| `local_files` | This device's view: one row per path with FileRecord fields, NTFS FileId, last scan stamp and flags (ignored, conflict, unsyncable) |
| `remote_files` | Latest record per (device, path) received from each peer |
| `needed` | Paths where some remote version dominates the local one. Rebuilt incrementally and drives the puller. |
| `chunks` | Chunk hash → (path, offset, length) for local reuse |
| `devices` | Known peers: DeviceId, name, addresses, last seen, last sequence received, approval state |
| `archive` | Archived versions: original path, archive path, time, file hash |
| `meta` | Schema version, folder settings, local sequence counter |

Version vectors use an 8-byte short device id (the first 8 bytes of DeviceId) to keep records small.
Missing counters are zero; canonical vectors omit zero entries. Incrementing
an exhausted 64-bit counter fails instead of wrapping. The Core compact
storage encoding is an unsigned LEB128 entry count followed by ascending
unsigned device IDs (fixed 8-byte little endian) and unsigned LEB128 counters.
Readers reject zero counters, duplicate/unsorted IDs, nonminimal/overflowing
varints, truncation and trailing bytes. Protobuf records use the equivalent
sorted `VvEntry` list; the compact storage encoding is not a protocol frame.

### Detecting local changes

- **Watcher:** `FileSystemWatcher` (ReadDirectoryChangesW) with a 64 KB buffer. A buffer overflow triggers a full scan.
- **Debounce:** a path is processed once it has been quiet for 2 s, or after 30 s of continuous change. That avoids syncing half-written files.
- **Rescan:** a full walk at start-up and every 600 s, Resilio's default, configurable per folder. Unchanged size, mtime, attributes and FileId mean no hashing.
- **Hashing:** a changed file is streamed once through FastCDC and BLAKE3, on a bounded worker pool (default: cores − 1, low I/O priority).
- **New record:** increment this device's counter in the version vector, bump the local sequence, sign (RW folders only), then push `IndexUpdate` to connected peers.
- **Renames:** the NTFS FileId shows a path change. It is published as delete plus create with the same chunks, so peers rebuild from local data.
- **Locked files:** opened with `FileShare.ReadWrite | Delete`. A sharing violation is retried after 10 s, backing off to 10 min.
- **Start-up speed (later phase):** read the NTFS USN change journal to find what changed while the service was stopped, instead of walking the whole tree.

### Version vectors and conflicts

| Situation | Outcome |
| --- | --- |
| Remote vector dominates local | Apply the remote version. The old local version goes to the archive. |
| Local vector dominates remote | Ignore. The remote peer will pull ours. |
| Vectors concurrent, contents equal | Merge the vectors. Nothing moves. |
| Vectors concurrent, contents differ | **Latest version wins:** newer mtime, tie broken by higher DeviceId, so every peer picks the same winner. The losing version goes to the archive on every peer that held it, and the UI shows a notice. |
| Losing version bigger than the archive limit (1,000 MB) | Kept as `name.sync-conflict-20261018-142233-ABCD.ext` instead, so it is never dropped silently. |
| Concurrent delete and edit | The later change wins, the delete included. An edit that loses goes to the archive. |
| Directory deleted remotely, new local files inside | Delete the known children. Keep the directory and the unknown files, which then sync back. |
| Two remote names differ only by case | Keep the first. Rename the second with a `.case-conflict` suffix, as Resilio's `.Conflict` handling does. |
| Name invalid on Windows | Mark the record unsyncable here and show a warning. Don't block the rest of the folder. |

Because mtime picks the winner, peers whose clocks differ by more than 10 minutes don't exchange changes, as in Resilio, and the UI says why. A per-folder setting, "Keep both copies", saves every losing version as a conflict copy instead.

### Applying remote changes

1. Create new directories, shallowest first.
2. Download files in the folder's priority order: default, smaller first, larger first, older first or newer first. Resilio offers the same five.
3. For each file, assemble it in a hidden temp file next to the target (`name.ext.!psp`, like Resilio's `.!sync`), from local chunks first, then network chunks. A new file created there inherits that directory's permissions. Verify every chunk and then the whole-file hash.
4. Re-check the target just before replacing. If it changed since the database last saw it, scan it now and re-run the conflict rules.
5. If the target exists, `File.Replace(tmp, target, archivePath)` archives the old version and swaps in the new one, keeping the target's permissions. Otherwise `File.Move(tmp, target)`. Set mtime and attributes, then record the new state.
6. Apply deletions last by moving files to the archive, then remove emptied directories deepest first.

### Archive, ignore rules and special files

- **Archive:** `.psp\archive\` keeps replaced and deleted files for 30 days, up to 1,000 MB per file, plus a total cap (default 10% of the volume). A daily task prunes it. Restore is in the UI from phase 5.
- **Ignore rules:** `.psp\ignore` uses gitignore-style patterns (`*`, `?`, `**`, `#`, a leading `/` anchors to the root), case-insensitive on Windows. The defaults ignore `Thumbs.db`, `desktop.ini`, `~$*`, `*.tmp`, `$RECYCLE.BIN` and `System Volume Information`. Like Resilio's IgnoreList, it is local to each device.
- **Read-only folders:** local edits are never sent. An "Overwrite local changes" option restores the swarm's version, as Resilio's `overwrite_changes` does.
- **`.psp` folder:** hidden and system attributes, holding `id`, `ignore` and `archive\`. It is never synced. Temp files (`*.!psp`) are hidden and skipped by the scanner.
- **Not synced in v1:** NTFS ACLs and alternate data streams (Resilio syncs streams through its StreamsList), symlinks and junctions (recorded, not followed), and files that are open with exclusive locks.
- **Paths:** Unicode NFC, long paths supported natively by .NET. The folder root must not be inside another synced folder.

## Security model

Security rests on three things: holding a folder key, TLS 1.3 between pinned device certificates, and signed change records. The rendezvous server never sees a key or a byte of file content.

### Cryptography

| Purpose | Algorithm | .NET implementation |
| --- | --- | --- |
| Transport | TLS 1.3 (AES-GCM or ChaCha20-Poly1305), forward secret | `System.Net.Quic` (MsQuic on Schannel), `SslStream` for TCP |
| Device identity | ECDSA P-256 self-signed certificate, 20-year validity | `ECDsa` + `CertificateRequest` |
| Key derivation | HKDF-SHA256 | `System.Security.Cryptography.HKDF` |
| Folder proof | HMAC-SHA256 | `HMACSHA256` |
| Change signatures | Ed25519 | `NSec.Cryptography` (libsodium) |
| Content hashes | BLAKE3 | `Blake3.Native` 3.0.2 (the `Blake3` namespace); selected in P0.2.4 after measuring both implementations |
| Encrypted-peer data (phase 6) | AES-256-GCM with a synthetic, content-derived nonce | `AesGcm` |
| Secrets at rest | Device key: non-exportable CNG key in LocalMachine\\My, usable only by the service. Folder keys: DPAPI (machine scope plus app entropy) inside peersync.db | `ProtectedData` |

All randomness comes from `RandomNumberGenerator`. No home-made ciphers: the only custom constructions are the key chain and the deterministic nonce, both built from standard primitives.

### Threats and mitigations

| Threat | Mitigation |
| --- | --- |
| Eavesdropper on the network | Everything runs over TLS 1.3, LAN included. Unlike Resilio, LAN encryption can't be turned off. |
| Tracker or relay operator | Sees FolderIds, DeviceIds, IP addresses, timing and volume. Can't join a folder (needs pk) or read data. Self-hosting removes the third party. |
| LAN observer | LAN announcements carry hourly-rotating HMAC tags, not FolderIds. |
| Man in the middle | The folder proof is bound to both certificate hashes. Each folder pins a peer's DeviceId on first contact and warns if it changes. |
| Malicious read-only peer | Can't sign records, so RW peers drop anything it sends. Data it serves must match signed chunk hashes. |
| Malicious encrypted peer | Holds only pk and ciphertext. It can withhold or corrupt data, and the hash checks catch corruption. |
| Leaked folder key | Anyone with it can join. Turn on device approval, or rotate: a new key creates a new folder, re-shared to trusted devices. As in Resilio, a key change does not propagate. |
| Compromised RW device | It can sign destructive changes. The archive (30 days) is the recovery path. |
| Hostile paths in records | Reject `..`, absolute or UNC paths, drive letters, `:` (alternate streams), reserved names (`CON`, `NUL`...), trailing dots and spaces. Fuzz-test this check. |
| Oversized or flooding input | Max frame 16 MiB, max 1,000 records per batch, per-peer request quotas, and a connection limit per IP. |
| Local user abuses the control pipe | The pipe ACL allows only Administrators and a local `PeerSync Users` group the installer creates. |

### Encrypted (untrusted) peers, phase 6

- Chunk keys come from `HKDF(K_content, "chunk")`. The nonce is the first 12 bytes of `HMAC(K_chunk, plaintext hash)`. Equal chunks encrypt identically, so encrypted peers can dedupe and verify ciphertext hashes.
- Path components are encrypted the same way under a separate name key, then Base32-encoded. The same name always encrypts to the same name.
- Encrypted peers get a parallel record stream: ciphertext paths, ciphertext chunk hashes and sizes, signed with sk like every other record.
- The known leak, as with any deterministic scheme: an encrypted peer can tell when two chunks or names are equal, and sees approximate file sizes.

## Windows service

The engine ships as `PeerSync.Service.exe`, a .NET 10 Worker Service registered with the Service Control Manager. It starts automatically at boot, before anyone signs in, and keeps syncing whether or not the WPF app is open.

### Hosting

`Host.CreateApplicationBuilder` plus `AddWindowsService(o => o.ServiceName = "PeerSync")`, from `Microsoft.Extensions.Hosting.WindowsServices`. The work is split into hosted services that share one dependency-injection container:

| Hosted service | Job |
| --- | --- |
| `FolderManager` | Opens each folder's database, runs its scanner, watcher and puller |
| `ConnectionManager` | QUIC and TCP listeners, outbound dialling, the handshake, per-peer sessions |
| `DiscoveryService` | LAN multicast, the tracker stream, predefined hosts, UPnP/NAT-PMP mapping |
| `TransferScheduler` | Global chunk scheduling, rate limits, endgame mode |
| `ControlApiHost` | gRPC server on the named pipe for the WPF app |
| `MaintenanceService` | Archive pruning, SQLite checkpoint and vacuum, log rotation |

### Account, permissions and paths

- **Account:** the virtual account `NT SERVICE\PeerSync`, not LocalSystem, because the service parses untrusted network input. When the user adds a folder they own, the WPF app grants that account inheritable Modify rights on it, with no elevation. For any other folder, the app asks for elevation once to grant the same right. A LocalSystem fallback is the installer switch `/SERVICEACCOUNT=LocalSystem`.
- **Start type:** Automatic. Recovery restarts after 60 s, 60 s, then 5 min (`sc.exe failure`).
- **Binaries:** `%ProgramFiles%\PeerSync\`, self-contained, single-file, x64 and ARM64.
- **Data:** `%ProgramData%\PeerSync\` holds `config\settings.json` (device settings, written only by the service), `state\peersync.db` (folder list, per-folder settings, DPAPI-protected folder keys, known devices, audit log), `state\folders\*.db` (one index per folder) and `logs\`. Only SYSTEM, Administrators and the service account can open `config` and `state`.
- **Device certificate:** in the `LocalMachine\My` store, with a non-exportable key that only the service account, SYSTEM and Administrators can use. `PeerSync.Service.exe --init`, run elevated by the installer, creates it once.
- **UI preferences:** per user in `%LocalAppData%\PeerSync\ui.json` (window size, theme). The app never stores sync settings itself.
- **Firewall:** the installer adds one inbound rule for `PeerSync.Service.exe`, which covers the random listening port and LAN discovery.

### Settings

Resilio's power-user list is the reference. Every setting is changed live through the control API and applied with `IOptionsMonitor`.

| Setting | Default |
| --- | --- |
| Listening port (UDP + TCP) | Random port from 20000–60000, picked at install |
| UPnP / NAT-PMP port mapping | On |
| LAN discovery | On |
| Tracker and relay URLs | sync.taskscape.com:99 |
| Use relay when needed | On |
| Upload / download limit | Unlimited, with LAN optionally exempt |
| Rescan interval | 600 s |
| Archive retention | 30 days, files up to 1,000 MB |
| Pause downloads below free space | 1 GiB |
| Pause on metered networks | Off |
| Peer expiry from the peer list | 7 days |

### Life cycle and system events

- **Start:** load settings, open databases, check each folder's `.psp\id`, start listeners, then discovery, then scans.
- **Stop:** stop accepting work, finish in-flight chunk writes (up to 10 s), checkpoint SQLite, send `Close` to peers. Request extra stop time from the Service Control Manager.
- **Sleep and resume:** a custom `WindowsServiceLifetime` overrides `OnPowerEvent`. On resume it re-announces, redials peers and rescans the folders.
- **Network change:** `NetworkChange.NetworkAddressChanged` triggers a LAN re-announce and a new public-address check.
- **Low impact:** hashing runs at below-normal priority with low I/O priority hints. Battery saver can pause hashing as an option.

### Logging, metrics and upgrades

- **Logs:** Serilog rolling files, 7 days and 100 MB total (Resilio's defaults). Warnings and errors also go to the Windows Event Log. Keys and file contents are never logged.
- **Metrics:** `System.Diagnostics.Metrics` counters (bytes up and down, peers, queue depth, hashing rate), streamed to the UI.
- **Upgrades:** running a newer Inno Setup installer stops the service, replaces the binaries and restarts it. On first start, migrations back up each database and then upgrade its schema. Signed auto-update comes in phase 5, by running the new installer silently.

Deployment diagnostics requirement (2026-10-18): the Windows client and central
server need opt-in structured debug events with UTC timestamps, run/process,
session and operation correlation, transport selection, byte counts, timings,
retry delays and native failure codes. Logs must never contain keys, FolderIds,
file contents or raw protocol bodies. Network-address events expire within
24 hours; other logs retain the existing bounded rotation policy. A diagnostic
bundle contains filtered structured logs and environment metadata, excluding
settings, databases, certificates and dumps. Server and second-PC deployment
instructions must identify exact OS/runtime versions and distinguish native
loopback, LAN, WAN, container and VM evidence. The P0 journal, collector and
probe runbook establish the mechanism; production event hooks ship with their
components in P2 and P4.

Cross-machine diagnostic refinement (2026-10-18): retain managed HRESULT,
native socket error and QUIC transport/application error numbers as separate
metadata. Failed probes report the operation stage and transferred bytes;
progress is emitted at bounded intervals. Collection must reject a bundle
with no recent valid debug events and report file/event counts. The P0 QUIC
probe gives connection establishment and the transfer independent deadlines,
so waiting for the second operator does not consume the transfer budget.

## WPF control app

`PeerSync.exe` is a small WPF app that only controls the service: closing it never stops syncing. It is built on .NET 10 WPF with `CommunityToolkit.Mvvm`, the built-in Fluent theme and a tray icon (`H.NotifyIcon.Wpf`).

### Screens

| Screen | Shows | Actions |
| --- | --- | --- |
| Folders (main window) | Per folder: name, path, access (RW, RO, Encrypted), status (Synced, Syncing 43%, Paused, Error), peers online, size, last synced | Add folder, Join with key, Pause or resume, Open in Explorer, Remove |
| Add folder | Folder picker (`OpenFolderDialog`), "Require approval for new devices" option | Creates the folder and its keys, grants the service account access |
| Join folder | Key box with live checksum validation, local path picker, warning if the folder isn't empty | Joins as RW, RO or Encrypted, depending on the key |
| Share | RW and RO keys with copy buttons and a QR code (`QRCoder`) | Copy, show QR, hide the RW key |
| Folder details: Peers | Device name, short DeviceId, address, connection type (LAN, direct, relay), speeds | Disconnect, block device |
| Folder details: Activity | Recent local and remote changes, queue of pending files | Open file location |
| Folder details: Overwritten | Versions that lost to a newer edit, with both times and devices | Restore my version, open both |
| Folder details: Archive (phase 5) | Archived versions by path and date | Restore, restore as copy, delete |
| Folder details: Settings | Discovery toggles (LAN, tracker, relay), predefined hosts, rescan interval, priority, archive retention, ignore-rule editor | Save, applied live |
| Pending devices | Device name, DeviceId fingerprint, IP, which folder | Approve, deny |
| Preferences | Device name, listening port, UPnP, rate limits, rendezvous server URL, start tray app at sign-in | Save |

**Tray:** the icon shows the overall state (synced, syncing, paused, error). The menu has Open, Pause all for 30 min, 1 h or until resumed, and Exit app (the service keeps running). Toasts appear when a newer edit overwrites yours, on errors and for pending devices.

### Control API (service ↔ app)

gRPC over the named pipe `\\.\pipe\PeerSync.Control`. The service hosts it with Kestrel's named-pipe transport (`ListenNamedPipe`) and a `PipeSecurity` ACL. The app connects with `Grpc.Net.Client` through a `NamedPipeClientStream` connect callback. Nothing listens on a TCP port.

| RPC | Purpose |
| --- | --- |
| `GetStatus`, `WatchEvents` (server stream) | Service health. Pushes folder state, transfer progress, peer, conflict and error events. |
| `WatchMetrics` (server stream) | Speeds, queue depth, hashing rate, every 1 s |
| `ListFolders`, `AddFolder`, `JoinFolder`, `RemoveFolder` | Folder management. `RemoveFolder` never deletes local files unless asked. |
| `PauseFolder`, `ResumeFolder`, `PauseAll` | Pausing |
| `GetFolderKeys` | Returns keys for the Share screen. Audit-logged. |
| `ListPeers`, `ApproveDevice`, `DenyDevice`, `BlockDevice` | Peer control |
| `GetFolderSettings`, `UpdateFolderSettings`, `GetIgnoreRules`, `SetIgnoreRules` | Per-folder configuration |
| `GetSettings`, `UpdateSettings` | Device-wide preferences |
| `ListOverwritten`, `RestoreOverwritten` | Versions overwritten by a newer edit |
| `ListArchive`, `RestoreArchived` | Archive (phase 5) |

The app shows a "Service not running" banner with a Start button (through an elevated helper) when the pipe is unreachable, and reconnects automatically.

## Central rendezvous server

One small ASP.NET Core service, `PeerSync.Rendezvous`, runs the tracker and the relay on the existing taskscape.com server. It is a Windows service listening on port 99, TCP and UDP, while the website keeps ports 80 and 443. It keeps only in-memory sightings ("device D, holding folder F, is reachable at these addresses"), and stores no files, keys or folder names. Without it, LAN and predefined-host sync keep working, as Resilio does with its tracker turned off.

### Tracker

- **Endpoint:** `sync.taskscape.com:99`. One Kestrel listener serves gRPC over HTTP/2 and TLS on TCP 99, and HTTP/3 (QUIC) on UDP 99. Devices authenticate with their device certificate (mutual TLS), so a DeviceId can't be spoofed.
- **Session stream:** each device holds one bidirectional stream, `Rendezvous.Session`.
  - Device → server: `Announce {folderIds[], localEndpoints[], listenPort}`, `PunchRequest {deviceId, folderId}`, `RelayRequest {deviceId, folderId}`.
  - Server → device: `ObservedAddress {ip, port}`, `PeerList {folderId, peers[]}` pushed on every change, `PunchNow {endpoints, atUtc}`, `RelayOffer {sessionId, token}`.
- **Matching:** a device sees only peers that announced the same FolderId, Resilio's Share ID model. Knowing a FolderId lets you list its peers' addresses, but not join, since joining needs pk.
- **State:** an in-memory map from FolderId to device entries with a 15-minute TTL. One instance is enough, so no Redis.
- **Bootstrap file:** `https://sync.taskscape.com:99/.well-known/peersync.json` lists the tracker and relay endpoints and is refreshed hourly. This copies Resilio's `config.resilio.com/sync.conf`, so the endpoint can move (another port or host) without a client update.

### Relay

1. Device A asks for a relay to device B for a folder. The server sends both a `RelayOffer` with a one-time token.
2. Both open a `Relay.Pipe` bidirectional gRPC stream on the same port 99 and present the token. The relay splices the two streams with `System.IO.Pipelines` and bounded buffers. Running the relay over gRPC keeps everything on one port.
3. Inside the splice, the peers run TLS 1.3 (`SslStream`) and the normal PSP handshake, so the relay sees only ciphertext.
4. Limits: a per-device rate cap (default 2 MB/s), a total relay cap set below the server's uplink so taskscape.com keeps its bandwidth, a session cap per device, and a 5-minute idle timeout.

### Abuse and privacy controls

- Per-IP and per-device rate limits on announces and relay requests. At most 500 folders per device.
- Logs keep aggregate counters only: no FolderIds, and client IPs for at most 24 hours for abuse handling.
- Several rendezvous servers can be listed in a client's settings. The client announces to all of them, so a self-hosted server and a friend's server can coexist.

### Hosting

The service shares the taskscape.com machine with IIS. It runs on Windows Server 2025 or the latest release (any version from 2022 has QUIC in-box), and doesn't touch the website's configuration.

- **Port 99, TCP and UDP.** IIS keeps 80 and 443. Port 443 is avoided on purpose, since IIS can also use UDP 443 for HTTP/3. Before go-live, `netstat -ano | findstr :99` must show nothing else on port 99.
- **Process:** a self-contained `PeerSync.Rendezvous.exe` running as a Windows service under its own virtual account (`NT SERVICE\PeerSyncRendezvous`) at below-normal priority, so the website wins any CPU contention.
- **Certificate:** DNS A and AAAA records for `sync.taskscape.com` pointing at the server, with a Let's Encrypt certificate from win-acme in `LocalMachine\My`. Kestrel loads it from the store by subject name at start, and a win-acme post-renewal script restarts the service so renewals take effect. IIS keeps answering the HTTP-01 challenge on port 80.
- **Firewall:** the hosting provider allows TCP 99, and UDP 99 is assumed. Windows Firewall gets both rules. Before the phase 4 gate, psp.exe probe sync.taskscape.com:99 sends a UDP packet and expects the observed address back. If UDP 99 turns out blocked, tracker and relay still work over TCP 99, but QUIC hole punching is lost and more traffic goes through the relay. Hole punching also needs a public IPv4 address, ideally IPv6 as well.
- **No IIS reverse proxy:** Kestrel serves port 99 directly, because QUIC and long-lived gRPC streams don't pass cleanly through IIS.

## Solution structure and tech stack

One .NET 10 solution with eleven projects. References only point down the stack, so the engine can be tested without a network, through an in-memory transport (the equivalent of Resilio's `inproc` tunnel).

| Project | Type | Contents |
| --- | --- | --- |
| `PeerSync.Core` | Class library | Folder keys and their text form, FileRecord, version vectors, path rules and ignore matching have no filesystem I/O. The shared, dependency-free diagnostic event/journal infrastructure is an explicit exception for log-file I/O (deployment diagnostics requirement, 2026-10-18). |
| `PeerSync.Crypto` | Class library | Key chain (HKDF, HMAC), Ed25519 signing, BLAKE3, FastCDC (written in-house, about 200 lines), the encrypted-peer scheme |
| `PeerSync.Protocol` | Class library | `.proto` files, frame codec, message validation and size limits |
| `PeerSync.Storage` | Class library | SQLite schema, migrations, repositories, chunk index |
| `PeerSync.Engine` | Class library | Scanner, watcher, hasher, puller, conflict resolver, archive, transfer scheduler |
| `PeerSync.Net` | Class library | QUIC and TCP transports, handshake, peer sessions, LAN discovery, tracker and relay clients, NAT mapping, the in-memory transport |
| `PeerSync.Service` | Worker Service | Windows service host, hosted services, control API server |
| `PeerSync.Control.Contracts` | Class library | `control.proto` and the generated client, the only thing the UI references |
| `PeerSync.App` | WPF app | UI, tray icon, folder ACL grant |
| `PeerSync.Rendezvous` | ASP.NET Core | Tracker and relay, Windows service on port 99 |
| `PeerSync.Cli` | Console app | `psp.exe status, add, join, peers, dump-index`, probe for scripting and debugging |

Outside `src\`: `installer\` (Inno Setup script PeerSync.iss), and `tests\` with unit, multi-node integration, fuzz (`SharpFuzz`) and benchmark (`BenchmarkDotNet`) projects.

### Packages

| Package | Used for |
| --- | --- |
| `Microsoft.Extensions.Hosting.WindowsServices` | Running as a Windows service |
| `Microsoft.Data.Sqlite` | Metadata databases |
| `Google.Protobuf`, `Grpc.Tools` | Wire messages |
| `Grpc.AspNetCore`, `Grpc.Net.Client` | Control API and rendezvous API |
| `NSec.Cryptography` | Ed25519 |
| `Blake3.Native` | Chunk and file hashes (3.x moved the original native implementation to this package); managed `Blake3` is used only for benchmark comparison |
| `Mono.Nat` | UPnP and NAT-PMP port mapping |
| `CommunityToolkit.Mvvm` | WPF view models |
| `H.NotifyIcon.Wpf` | Tray icon |
| `QRCoder` | QR codes for keys |
| `Serilog.Extensions.Hosting`, `Serilog.Sinks.File` | Logging |
| `Inno Setup 7.1 (ISCC.exe)` | Installer, built in CI (a tool, not a NuGet package) |
| `xunit.v3`, `Shouldly`, `SharpFuzz`, `BenchmarkDotNet` | Tests, fuzzing, benchmarks |
| `MonoTorrent` (phase 5, optional) | Mainline DHT |

In-box with no package: `System.Net.Quic`, `SslStream`, `HKDF`, `HMACSHA256`, `AesGcm`, `FileSystemWatcher`, `System.IO.Pipelines`, `System.Diagnostics.Metrics`. DPAPI (`ProtectedData`) needs the small `System.Security.Cryptography.ProtectedData` package.

## Phased roadmap

Version 1.0 (LAN and internet sync, the service, the WPF app and the installer) takes about 26 weeks for one senior .NET developer. Two developers can run phases 3 and 4 side by side and finish in about 20 weeks. No phase starts until the previous gate passes.

User-approved external-validation phasing (2026-10-19): Gate P0 may pass with
only P0.2.1's physical 1-Gbit LAN measurement and P0.2.2's native NAT matrix
explicitly deferred. Their tasks stay unchecked and their acceptance criteria
remain intact. The LAN measurement is mandatory before Gate P2; the same-LAN,
full-cone, port-restricted and mobile/CGNAT matrix plus final transport decision
are mandatory before Gate P4. All other P0 requirements must pass first.
Existing Tailscale and public rendezvous-probe successes retain their actual
scope. No unexecuted test is marked passed or dropped by this phasing change.
All other phase gates, security, soak, installer and release checks remain
unchanged.

&#91;embedded content: phased roadmap · 7 phases, 7 gates\]

The riskiest work comes first: the transport spike in phase 0 decides the networking design, and the engine is proven in simulation before it touches a real network.

Phase 6 options are independent of each other:

- **Encrypted backup peers** (F keys, see Security model): about 3 weeks.
- **Share links with identities and owner approval**, like Resilio's Advanced folders: about 4 weeks.
- **On-demand placeholders** through the Windows Cloud Files API, the modern equivalent of Resilio's `.rsls` Selective Sync: about 6–8 weeks.
- **Mainline DHT discovery**: about 2 weeks.

## Testing strategy

The engine's correctness matters more than its speed: every phase gate includes a soak run with zero lost versions. Most of the logic is tested with many simulated nodes in one process over the in-memory transport, before any real network is involved.

| Layer | What it proves | How |
| --- | --- | --- |
| Unit | Key chain and checksums, version-vector comparison, ignore matcher, path rules, FastCDC boundaries, conflict naming | `xunit.v3` with fixed test vectors |
| Property-based | Convergence: after random edits, renames and deletes on 3–7 nodes with random partitions, all trees end up identical. No loss: every overwritten version survives in the archive or as a conflict copy. | `CsCheck` generators over the in-memory transport, thousands of seeds in CI |
| Integration | Real service processes (3–5 on one machine, separate data folders) over loopback QUIC: rename storms, 50 GB files, 1M small files, offline edits, delete-versus-edit races | Scenario scripts driving `psp.exe` |
| Network conditions | Latency, loss, full-cone versus symmetric NAT, CGNAT, relay fallback | Hyper-V VMs behind a Linux router VM (iptables NAT, `tc netem` for 100 ms RTT and 2% loss) |
| File system edge cases | Office lock files, files changing during hashing, long paths, case-only renames, read-only attributes, exFAT USB drives (no stable file IDs), sleep in the middle of a write | Dedicated tests on real NTFS and exFAT volumes |
| Security | A MITM proxy with its own certificate must fail. Forged records from a read-only peer are dropped. Tampered relay bytes are detected. Hostile paths are rejected. | Attack harness plus `SharpFuzz` on the frame decoder, message validation, path rules and the ignore parser |
| Performance | Hashing, chunking, index throughput, LAN transfer rate | `BenchmarkDotNet`, plus timed runs on reference hardware |
| Soak | 72 h of random operations on 3 physical PCs, with sleep, resume and network switches. A checker compares trees and hashes every hour. | Run before each phase gate |

**Performance targets (to confirm in phase 0):** hashing at least 1 GB/s on NVMe, LAN transfer at 80% or more of 1 Gbit/s line rate, first index of 1M files within 20 min on an SSD, idle CPU under 1%, and memory under 300 MB with 1M files.

P0.2.4 local measurement (2026-10-18): a sequential 10 GiB file pass performing FastCDC plus both chunk and whole-file BLAKE3 reached 1.051 decimal GB/s with `Blake3.Native` 3.0.2, versus 0.887 GB/s with managed `Blake3`, on the recorded Ryzen 7600X / Crucial NVMe workstation after reducing buffer copies. Hashes matched across both implementations. This is a warm-cache, repeated-4-MiB-block corpus; it does not establish cold-disk, other hardware or LAN throughput. The 1 GB/s target is retained; see `docs/spikes/P0.2.4-chunking-hashing.md`.

**CI:** GitHub Actions on `windows-latest` builds, runs unit and loopback integration tests, a 10-minute fuzz smoke test, and builds the signed x64 and Arm64 installers with ISCC on every main-branch commit.

## Packaging and deployment

One signed Inno Setup 7 installer per architecture (`PeerSync-Setup-<version>-x64.exe` and `-arm64.exe`) installs the service and the app for all users. It needs administrator rights once, and none for daily use after that. Inno Setup has no built-in service support, so the script drives `sc.exe`, `netsh` and `icacls` itself, and stops the service before replacing files.

### What the installer does (Inno Setup)

1. **`[Setup]`:** a fixed `AppId` GUID (never changed, so a newer setup upgrades in place), `PrivilegesRequired=admin`, `MinVersion=10.0.20348` (Server 2022; Windows 11 is 10.0.22000, so Windows 10 is refused), `ArchitecturesAllowed` and `ArchitecturesInstallIn64BitMode` set to `x64compatible and not arm64`, or `arm64` for the Arm64 build. `CloseApplications=yes` lets Restart Manager close the tray app.
2. **`[Files]`:** self-contained, ReadyToRun builds of the service, app and CLI go to `{app}` (`%ProgramFiles%\PeerSync\`), each signed once with the `signonce` flag.
3. **`PrepareToInstall` (Pascal Script):** if the service exists, run `sc.exe stop` and poll until it reports STOPPED, for up to 30 s. If it won't stop, Setup shows an error instead of replacing files in use.
4. **`[Run]`, in order, hidden:**
   1. `sc.exe create` on first install only: start `auto`, account `NT SERVICE\PeerSync`. The installer switch `/SERVICEACCOUNT=LocalSystem` overrides it through `{param:ServiceAccount|...}`.
   2. `sc.exe failure`: restart after 60 s, 60 s, then 5 min.
   3. `net localgroup "PeerSync Users" /add`, then add the installing user (`{username}`) to it.
   4. `PeerSync.Service.exe --init` (elevated): creates `config`, `state` and `logs` under `%ProgramData%\PeerSync` with their ACLs (by SID, so it works in any language), creates the device certificate and lets the service account use its private key, and writes default settings. It skips anything already done, so upgrades run it again safely.
   5. `netsh advfirewall firewall add rule` for inbound traffic to `PeerSync.Service.exe`. A program rule covers the random listening port and LAN discovery in one rule.
   6. `sc.exe start PeerSync`, then optionally open the app (`postinstall`, skipped in silent mode).
5. **`[Dirs]`:** `{commonappdata}\PeerSync` with `uninsneveruninstall`, so settings and databases survive upgrades and uninstalls.
6. **`[Tasks]` and `[Registry]`:** an optional "Start the tray app when I sign in" task writes the HKLM `Run` value. Phase 6 adds the `peersync://` URL handler under `Software\Classes`.
7. **`[UninstallRun]`:** `sc.exe stop`, `sc.exe delete`, delete the firewall rule and the local group, each with a `RunOnceId`.

**Script outline** (abridged, x64 build; `ISCC /DArch=arm64` switches the architecture lines and source folder):

```ini
#define AppVer GetVersionNumbersString("..\publish\x64\PeerSync.Service.exe")

[Setup]
AppId={{00000000-0000-0000-0000-000000000000}   ; generate once, never change
AppName=PeerSync
AppVersion={#AppVer}
DefaultDirName={autopf}\PeerSync
PrivilegesRequired=admin
MinVersion=10.0.20348
ArchitecturesAllowed=x64compatible and not arm64
ArchitecturesInstallIn64BitMode=x64compatible and not arm64
CloseApplications=yes
SignTool=signtool
SignedUninstaller=yes
OutputBaseFilename=PeerSync-Setup-{#AppVer}-x64

[Tasks]
Name: autostart; Description: "Start the PeerSync tray app when I sign in"

[Files]
Source: "..\publish\x64\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion signonce

[Dirs]
Name: "{commonappdata}\PeerSync"; Flags: uninsneveruninstall

[Registry]
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PeerSync"; ValueData: """{app}\PeerSync.exe"" --tray"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{sys}\sc.exe"; Parameters: "create PeerSync binPath= ""{app}\PeerSync.Service.exe"" start= auto obj= ""{param:ServiceAccount|NT SERVICE\PeerSync}"""; Flags: runhidden; Check: not ServiceExists
Filename: "{sys}\sc.exe"; Parameters: "failure PeerSync reset= 86400 actions= restart/60000/restart/60000/restart/300000"; Flags: runhidden
Filename: "{sys}\net.exe"; Parameters: "localgroup ""PeerSync Users"" /add"; Flags: runhidden
Filename: "{sys}\net.exe"; Parameters: "localgroup ""PeerSync Users"" ""{username}"" /add"; Flags: runhidden
Filename: "{app}\PeerSync.Service.exe"; Parameters: "--init"; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""PeerSync"" dir=in action=allow program=""{app}\PeerSync.Service.exe"" enable=yes"; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "start PeerSync"; Flags: runhidden
Filename: "{app}\PeerSync.exe"; Description: "Open PeerSync"; Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop PeerSync"; Flags: runhidden; RunOnceId: "StopService"
Filename: "{sys}\sc.exe"; Parameters: "delete PeerSync"; Flags: runhidden; RunOnceId: "DeleteService"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""PeerSync"""; Flags: runhidden; RunOnceId: "DeleteFirewallRule"
Filename: "{sys}\net.exe"; Parameters: "localgroup ""PeerSync Users"" /delete"; Flags: runhidden; RunOnceId: "DeleteGroup"

[Code]
function ServiceExists: Boolean;
begin
  Result := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\PeerSync');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var Code, Tries: Integer;
begin
  Result := '';
  if not ServiceExists then Exit;
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop PeerSync', '', SW_HIDE, ewWaitUntilTerminated, Code);
  for Tries := 1 to 30 do
  begin
    Exec(ExpandConstant('{cmd}'), '/c sc query PeerSync | find "STOPPED"', '', SW_HIDE, ewWaitUntilTerminated, Code);
    if Code = 0 then Exit;
    Sleep(1000);
  end;
  Result := 'The PeerSync service did not stop. Stop it in Services and run Setup again.';
end;
```

**Upgrades:** running a newer setup over an installed one upgrades in place: same `AppId`, service stopped in `PrepareToInstall`, files replaced, service restarted. `InitializeSetup` compares versions and refuses a downgrade. Database migrations run on the first start after an upgrade, with a backup first.

**Uninstall:** stops and deletes the service, removes the binaries, firewall rule and local group. A `CurUninstallStepChanged` prompt asks whether to delete `%ProgramData%\PeerSync` (default: keep it). It never touches the contents of synced folders.

**Signing:** define a sign tool once (the IDE's Configure Sign Tools, or ISCC's `--signtool` option) that calls `signtool sign ... $f`. Then `SignTool=signtool` signs the setup, `SignedUninstaller=yes` signs the uninstaller, and the `signonce` flag signs the EXEs. Azure Trusted Signing or a standard code-signing certificate both work. Unsigned, SmartScreen warns every user.

P0 signing-path verification (2026-10-19) uses a fresh, non-exportable
development certificate generated on each CI runner. Sign and timestamp a
separate test EXE, verify its signature, and reject a changed PE section.
Remove the temporary machine-store signing key and root trust entry afterwards;
retain only the public certificate, signed smoke EXE and verification metadata.
This does not sign deployment artifacts with a disposable identity or provide
public release trust. A production signing identity remains required for
publicly trusted v1 EXEs and installers.

**Silent installs:** `PeerSync-Setup-x64.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS="autostart" /LOG="setup.log"` installs unattended, for scripted roll-outs. Phase 5 auto-update uses the same path: the service downloads a newer setup, checks its signature, and starts it from a short-lived helper process, because Setup stops the service.

**Build and licence:** CI installs Inno Setup 7.1 and runs `ISCC.exe` once per architecture after `dotnet publish`. Inno Setup is free for any use, including commercial. Since 6.5, commercial users are asked, not required, to buy a licence.

### First-run flow for a user

1. Install on PC A, open PeerSync, choose **Add folder**, pick a folder.
2. Open **Share** and copy the RW key (or the RO key for one-way sync).
3. Install on PC B, choose **Join with key**, paste the key, pick an empty or pre-filled folder.
4. Both PCs find each other on the LAN within seconds, or through the rendezvous server elsewhere, and sync starts.

### Rendezvous server deployment

- Published self-contained for `win-x64` and installed with a small Inno Setup installer, `PeerSync-Rendezvous-Setup.exe`. It registers the Windows service and opens TCP and UDP 99 in Windows Firewall, the same way the client installer does.
- Configuration lives in `appsettings.json` next to the exe: port (99), certificate subject, relay caps, TTLs, rate limits, and the endpoints published in `peersync.json`.
- `/healthz` on port 99 for uptime checks, errors to the Windows Event Log, and OpenTelemetry metrics (devices online, announces per second, relay sessions and bytes).
- Upgrades: run the newer server installer. Clients reconnect automatically, and syncs already in progress between peers carry on, since they don't pass through the server.

## Risks and open questions

The biggest technical risk is NAT hole punching with `System.Net.Quic`; the biggest product risk is a sync bug that loses data. Both are attacked first: the first in phase 0, the second by every phase gate.

| Risk | Impact | Mitigation |
| --- | --- | --- |
| `System.Net.Quic` can't dial out from the listener's UDP port | No QUIC hole punching, so more relay traffic across the internet | Phase 0 spike. Plan B: call MsQuic directly, or a reliable-UDP library with `SslStream` on top. TCP and the relay keep working either way. |
| An engine bug deletes or overwrites data | Users lose files | Archive on by default so every losing version is kept, conflict copies for files over the archive limit, property-based convergence tests, 72 h soak at every gate |
| `FileSystemWatcher` misses events | Changes sync late | 600 s rescans, full scan on buffer overflow, USN journal in phase 5 |
| 1M+ files strain memory or SQLite | Slow scans, high RAM | Streaming index exchange, indexed queries, the 1M-file benchmark is a phase 5 gate |
| Many peers behind symmetric NAT or CGNAT | High relay bandwidth cost | Prefer IPv6 direct, UPnP/NAT-PMP, relay rate caps |
| Service account can't get access to a folder (not owned by the user, other volumes) | That folder can't be added | Clear UI error and the LocalSystem installer switch |
| Native crypto dependency (`NSec` / libsodium) on ARM64 | Build or packaging failures | Test both architectures in CI. `BouncyCastle` is a pure-managed fallback. |
| Legal: Resilio name, trademarks, protocol | Takedown or dispute | Own name, clean-room design from public documentation only, no Resilio code or protocol bytes |
| Some networks (offices, hotels) block outbound port 99 | Devices there can't reach the tracker or relay, so only LAN and predefined hosts work | The UI says why. The bootstrap file can move the endpoint to another port without a client update. |
| Relay traffic competes with taskscape.com | Slower website while peers relay heavily | Total relay cap below the uplink, below-normal process priority, relay bytes on the metrics dashboard |
| Website and rendezvous share one machine | A reboot or outage takes both down | Only new introductions and relayed sessions stop. Direct peer connections and LAN sync carry on. |
| A wrong clock picks the wrong "latest" version | An older edit overwrites a newer one | Peers more than 10 min apart don't exchange changes, and the loser is always archived |
| UDP 99 turns out blocked at the hosting provider | No QUIC to the server and no hole punching, so more relay traffic | Probe early in phase 4. Everything still works over TCP 99 while the provider opens UDP. |

### Questions for you

- [x] Server: one self-hosted server on the taskscape.com machine, tracker and relay on port 99 (decided 2026-10-18).
- [x] Windows 10: not needed. Windows 11 and Server 2022 or later only.
- [x] Conflicts: latest version wins, and the other version goes to the archive.
- [x] Server OS: Windows Server 2025 or the latest release.
- [x] Host name: `sync.taskscape.com`.
- [x] Hosting firewall: TCP 99 allowed by the provider; UDP 99 assumed.
- [ ] Verify UDP 99 end to end with `psp.exe probe` before the phase 4 gate.
- [ ] Should encrypted backup peers or on-demand placeholders move into v1.0?
- [ ] Do you have a code-signing certificate?
- [ ] Team size: the estimates assume one senior .NET developer.

## Sources

Opened on 2026-10-18. Resilio help-centre articles describe Sync 2.x–3.x behaviour.

- [Key structure and flow](https://help.resilio.com/hc/en-us/articles/206767810-Key-structure-and-flow)
- [Link structure and flow](https://help.resilio.com/hc/en-us/articles/204754739-Link-structure-and-flow)
- [What ports and protocols are used by Sync?](https://help.resilio.com/hc/en-us/articles/204754759-What-ports-and-protocols-are-used-by-Sync)
- [Power user preferences](https://help.resilio.com/hc/en-us/articles/207371636-Power-user-preferences)
- [What is a Relay Server?](https://help.resilio.com/hc/en-us/articles/204754779-What-is-a-Relay-Server)
- [Sync Storage folder](https://help.resilio.com/hc/en-us/articles/206664690-Sync-Storage-folder)
- [What is the .sync folder](https://help.resilio.com/hc/en-us/articles/206217185-What-is-sync-folder-and-StreamsList-IgnoreList-and-Archive-inside)
- [What happens when a file is renamed](https://help.resilio.com/hc/en-us/articles/209606526-What-happens-when-file-is-renamed)
- [What if several people change the same file?](https://help.resilio.com/hc/en-us/articles/204754209-What-if-several-people-make-changes-to-the-same-file)
- [Does Sync transfer only the changed part?](https://help.resilio.com/hc/en-us/articles/206217095-When-a-file-changes-does-Resilio-Sync-transfer-the-entire-file-again-or-just-the-part-that-s-changed)
- [Ignoring files (IgnoreList)](https://help.resilio.com/hc/en-us/articles/205458165-Ignoring-files-in-Sync-Ignore-List)
- [Synchronization modes](https://help.resilio.com/hc/en-us/articles/205457775-Synchronization-Modes)
- [Encrypted folders](https://help.resilio.com/hc/en-us/articles/207370466-Encrypted-folders)
- [Understanding the Sync encrypted folder](https://www.resilio.com/blog/understanding-the-sync-encrypted-folder)
- [Sync dev: how keys work](https://www.resilio.com/blog/sync-dev-a-look-into-how-keys-formerly-known-as-secrets-work)
- [Resilio Sync security and privacy brief](https://www.resilio.com/docs/Resilio_Sync_Security_and_Privacy_Brief.pdf)
- [Resilio Sync 3.0 change log](https://help.resilio.com/hc/en-us/articles/31386579044755-Resilio-Sync-3-0-change-log)
- [BitTorrent Sync: network investigation methodology (Scanlon, Farina, Kechadi, 2014)](https://arxiv.org/abs/1409.8489)
- [Arch Wiki: Resilio Sync](https://wiki.archlinux.org/title/Resilio_Sync)
- [.NET QUIC platform dependencies](https://learn.microsoft.com/dotnet/fundamentals/networking/quic/quic-overview#platform-dependencies)
- [Create a Windows service using BackgroundService](https://learn.microsoft.com/dotnet/core/extensions/windows-service)
- [Inno Setup downloads (7.1.0, 2026-08-12)](https://jrsoftware.org/isdl.php)
- [Inno Setup 7 what's new](https://jrsoftware.org/files/is7-whatsnew.htm)
- [Inno Setup licence](https://jrsoftware.org/files/is/license.txt)
- [Inno Setup help: ArchitecturesAllowed](https://jrsoftware.org/ishelp/topic_setup_architecturesallowed.htm)
- [Inno Setup help: SignTool](https://jrsoftware.org/ishelp/topic_setup_signtool.htm)
