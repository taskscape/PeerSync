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
3. **Hello** on stream 0: protocol version, DeviceId, device name, client version, capability flags, 32-byte random nonce and UTC DateTime ticks. UTC ticks are required and validated in the inclusive range 1..DateTime.MaxValue.Ticks; absent/default or invalid values block index exchange. Identity still comes from the TLS certificate.
4. **FolderAuth** for each folder both sides announce: `HMAC(K_auth, "psp1 auth" ‖ sender DeviceId ‖ receiver DeviceId ‖ sender nonce ‖ receiver nonce ‖ FolderId)`.
   1. A read-only or RW peer adds the same MAC under K\_ro\_auth, proving it may receive plaintext.
   2. An RW peer adds an Ed25519 signature over the transcript with sk.
   3. Binding the MAC to both certificate hashes defeats a man in the middle, who would have to present a different certificate. Putting the sender first stops an attacker reflecting a peer's own proof back to it. Both DeviceIds come from the TLS certificates, never from Hello.
5. **IndexSummary**: per folder, the highest sequence number each side has seen from the other. Incremental index exchange follows.

A folder can optionally require approval: unknown DeviceIds are held as Pending until approved in the UI, like Resilio's "Approve all peers".

P2 transport implementation uses a single mutually authenticated peer connection
with bounded control requests on stream 0, a persistent bidirectional index
stream per admitted folder and a bounded pool of short-lived block streams.
Both peers can initiate requests. Hello carries request_id/response and at
most 256 announced FolderIds; its wire nonce and certificate identity stay
fixed for the connection while UTC samples refresh. The adapter supplies
configured device/version text and preserves the engine's UTC sample.
FolderAuth requests have request IDs; FolderAuthResult includes the responder's
direction-bound proof and the matching request ID. Neither peer creates an
engine handler before validating folder proofs and local pin/approval policy.
Index queries/responses carry request IDs; zero denotes an unsolicited update
or the initial folder-stream binder. The lower certificate DeviceId opens the
folder index stream, preventing duplicate streams. All readers have one owner,
writes are serialized, pending work is bounded and cancellation drains before
handler/key/certificate ownership is released. Shared diagnostic session IDs
derive from the ordered certificate identities and connection nonces, without
logging keys, FolderIds, paths or packet bodies.

Direction-bound RW authentication signs "psp1 rw-auth" followed by sender and
receiver certificate DeviceIds, sender and receiver nonces and FolderId.
The ordinary/RO MACs use their existing distinct labels and derived keys.
Certificate validation requires current P-256 device certificates and TLS 1.3
with ALPN psp/1; folder proofs and optional pins establish authorization for
self-signed peers. Clear exported auth-key buffers after proof operations.

### Messages

P2 fixes the wire type numbers as Hello=1, FolderAuth=2, FolderAuthResult=3,
IndexSummary=4, IndexRequest=5, IndexUpdate=6, Availability=7,
BlockRequestBatch=8, BlockResponse=9, Ping=10, Pong=11 and Close=12.
The canonical unsigned varint is the Protobuf body length, excluding the
one-byte type; body length is at most 16 MiB. Reject overlong/noncanonical
prefixes, oversized bodies and truncated EOF. Unknown types and malformed
Protobuf are connection violations, with safe numeric reason codes.
PipeReader bodies remain valid only until the next iterator advance/disposal;
message decoding copies/parses before that boundary. Always advance examined
to the available buffer end when a frame is incomplete. Reader ownership
stays with the connection. IndexUpdate explicitly carries after_seq and
last_seq so complete reviewed ranges can include unpublished sequence gaps.
P2 message schemas supplement, rather than rewrite, original signed bodies.
Frame fuzz adds coverage alongside all four existing boundary targets.

P2 ingress validation is separate from structural Protobuf parsing. Require
protocol version 1, 32-byte Hello identity/nonce, valid nonzero UTC ticks,
single-line strict UTF-8 device/client names capped at 128/64 bytes, and
known role/decision/reason values. Folder-scoped post-handshake messages
require the authenticated folder context, not an identity/key from the body.
Enforce 1000 ordered index records and 64 block requests per frame, signed
64-byte signatures, reviewed sequence ranges within Int64, and existing path,
version metadata, chunk class/hash/length/total rules. Verify original signed
bytes before opening FileRecordBody; forwarding preserves those exact bytes.
Availability bitmaps are capped at 64 KiB (more bits than can be represented
by a legal 16-MiB signed chunk list). Data blocks are at most the class-2
8-MiB maximum and must match their BLAKE3 digest; response/request matching
and folder permissions remain mandatory in the transport/transfer layer.
Protocol violations add a strike to the certificate-derived device identity
and close the owned connection with a reason, even if recording a strike
fails. Error messages contain safe codes, not records, paths or keys.
Enforce repeated-message limits before generated Protobuf collection
allocation. Reject deprecated groups; keep ordinary unknown fields and opaque
signed bodies intact. After signature verification, preflight nested chunk
and vector entries before opening their full repeated collections.

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

**FileRecord** fields: path (relative, `/`-separated, Unicode NFC), type (file, directory, symlink), size, mtime (100 ns UTC ticks), Windows attributes (read-only, hidden), deleted flag, version vector, ordered chunk list `(length, BLAKE3 hash)`, whole-file BLAKE3 hash, modifying DeviceId, local sequence number. The originating peer serializes the body once and signs `"psp1 record" || FolderId || body`. Store and forward those exact body bytes and the original signature; only the local sequence is rewritten. Receivers verify before parsing, then validate the parsed record. Reserializing a parsed Protobuf message is not a valid way to recover the signed bytes, because field order and unknown fields may differ.

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

Each normalized database path has one writer owner in the service process.
Writes run through a bounded single-reader channel, with one transaction per
queued operation; failed/cancelled operations roll back without poisoning the
next write. Readers own separate read-only connections. Connections use WAL,
NORMAL synchronous mode, foreign keys, a 5-second SQLite busy timeout and memory
temp storage. Migrations run before exposing the database, back up through
SQLite's online backup API (including committed WAL data), then apply SQL and
`user_version` in one transaction. Future schema versions are rejected. Because
Microsoft.Data.Sqlite's async ADO.NET calls execute synchronously, the writer
runs on a worker task rather than blocking the service caller.

Fresh database initialization reserves the file with CreateNew. Under SQLite's
immediate writer transaction it must still have version zero and an empty schema
before all initial migrations and the final version are committed together.
There is no prior user state to back up in this case. Any existing file (including
an empty version-zero database), or a newly reserved file another process has
populated, retains the ordinary backup-before-each-step upgrade path. Failed or
cancelled initial DDL rolls back the entire schema; startup never exposes a
partially initialized database. Keep the full native checks and 15-minute CI
limit; reduce initialization I/O rather than skipping durability tests.

Destructive filesystem application requires a durable intent first. Add an
`apply_intents` journal keyed by operation ID and unique path key, with the
expected local sequence and a bounded versioned engine recovery payload.
Use the same serialized writer with synchronous=FULL for intent reservation,
recovery updates and final application commits, restoring NORMAL outside the
transaction. Final commit writes the local file/chunk index and sequence,
archive rows and latest needed-set recomputation, then removes the intent in
one transaction. A changed local sequence or failed/cancelled callback rolls
back the entire commit and retains recovery intent. Schema migration 4 follows
the existing backup-before-each-step policy. Ordinary scan batches retain NORMAL.
Scans and filesystem application/recovery share a per-folder asynchronous gate
keyed by the folder index owner. Before walking, the scanner rejects a folder
with pending apply intent, so an interrupted filesystem mutation is never
published as an ordinary local deletion. Recovery must resolve or safely park
pending work before scanning can resume.

Folder repositories commit up to 1,000 rows per transaction. Allocate local
sequence numbers and persist their records in that same transaction, so an
index cursor cannot pass an unpublished earlier sequence. Rewriting a local
sequence never changes the signed body/signature. Supplied local chunk indexes
are replaced atomically with their file row. Unsyncable local names may be
kept as metadata with no signed record; remote paths always pass path rules.

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
  Native callbacks only enqueue bounded notifications. Channel/pending-set
  saturation and native watcher errors request a full scan from the consumer;
  callbacks never scan, log to disk or call application handlers. Debounce uses
  monotonic time, coalesces both rename paths, and emits relative-path batches.
  Until targeted subtree scans exist, a ready batch schedules a full safe walk;
  unchanged metadata avoids file hashing. Our writes are observed normally:
  the puller commits actual file metadata before the debounced scan runs.
- **Rescan:** a full walk at start-up and every 600 s, Resilio's default, configurable per folder. Unchanged size, mtime, attributes and FileId mean no hashing.
  The first scheduler uses one process-wide folder-scan permit, which also
  enforces the per-disk limit across partitions and unmapped volumes. Each
  folder coalesces startup/periodic/watcher requests, retries transient scan
  problems from 10 s up to 10 min, and releases the permit before waiting.
  Independent-disk parallelism is a later performance refinement. Scanner
  file handles use the Windows very-low I/O priority hint; drivers determine
  how that hint is honored. A rejected hint is reported as a deferred read.
- **Hashing:** a changed file is streamed once through FastCDC and BLAKE3, on a bounded worker pool (default: cores − 1, low I/O priority). Each pass is bounded to the original length. Compare open-handle size, last-write/change timestamps, attributes and volume/file identity before and after reading, then reopen the path to check it still names that file. Detected edits, growth/truncation, removal or replacement return "retry later". This is metadata-based change detection, not an atomic filesystem snapshot; ordinary writer sharing remains allowed. Progress reports contain only byte/chunk counts. Reparse points are not hashed.
- **New record:** increment this device's counter in the version vector, bump the local sequence, sign (RW folders only), then push `IndexUpdate` to connected peers.
- **Unchanged-file cache:** persist the native change timestamp alongside size,
  last-write time, attributes and file identity for regular files. A missing or
  changed stamp requires hashing, including after upgrade/restart, so ordinary
  same-length writes which restore last-write time are detected. A cache-only
  refresh with the same authenticated bytes/synchronized metadata preserves the
  signed body/vector and original chunk partition. Incoming application records
  its final native stamp. Directory child changes do not create directory
  versions. Renames must verify the whole file under a read guard before reusing
  a previous signed partition; identity/mtime alone do not prove unchanged bytes.
- **Published content:** before publishing a live file, retain every verified chunk as independent bytes in a root-bound `.psp/blocks/<prefix>/<BLAKE3>` store. Copy during the hashing pass, flush before no-overwrite installation, verify existing entries, and never hardlink mutable user data. File metadata alone is not a stable source for an already advertised version. A durable per-row readiness bit withholds legacy/missing-capture live records until capture; metadata-only upgrades keep signed edit counters/bodies unchanged. Incoming verified files populate the same store before publication. Block serving/reuse verifies length/hash under no-write/no-delete-sharing read guards. Failures defer publication, without logging names or hashes. Old observed versions must subsequently be materialized into the archive/conflict policy before their final references are released; reference-aware orphan retention/cleanup is mandatory before P1's no-lost-version gate. Unobserved writes between scans are not snapshots.
- **Local supersession:** before committing a content-changing edit, deletion or file-to-directory change, reconstruct the prior authenticated live-file snapshot from independent blocks. Apply the same archive/oversize/keep-both policy. A bounded kind-4 intent binds the root, source record, expected local sequence, staging identity and no-overwrite destination. FULL-durable completion accounts for the retained version and an exact-source marker without changing the user's current file. Interrupted work recovers before scanning; a completed marker prevents duplicate histories when the later scan commit fails. Keep the authenticated manifest with its retained location for reference-aware block retention and recovery. RW conflict copies enter ordinary scanning; RO copies remain local. Missing/unverified historical bytes fail visibly rather than inventing an archive or discarding the old record. Metadata-only changes with unchanged bytes need no duplicate content archive.
  Reuse an already indexed, verified archive for the exact source path/size/hash,
  or a scanned conflict-tree descendant with the same native identity/bytes,
  instead of duplicating versions already retained by restore/type application.
  Bind the reused location in a FULL-durable source marker without changing its
  metadata. Index archive lookups by original path/hash/size. Changed or
  unverified destinations remain protected; reconstruct another independent copy.

Published block ownership is persistent. Before writing a new staging blob,
flush a bounded local capture proof binding the root, proof/data file identities,
expected hash/length, capture time and canonical GUID. A checksum detects torn
proofs; it is local recovery metadata, not network authorization. After installing
and verifying a blob, FULL-durable catalog registration precedes any live-file
readiness/publication. Recovery can finish complete owned staging, reconcile
installed captures after a crash, and preserve unknown, changed or invalid
objects. Valid proof cleanup follows catalog durability. Existing verified bytes
matching a caller-owned capture can be registered without rewriting them.
The subsequent reference sweep must protect current/needed/history data and wait
for pending application work; orphan expiry must not infer ownership from a
filename alone. All reference-changing engine entry points must serialize with
the folder sweep, and cleanup must verify root/file identity, length/hash and
independent-file state under native guards before deleting known objects.
**Concurrent capture ownership recovery.** Independent capture owners can race
to install identical bytes. The losing owner deletes only its own temporary,
but must retain its verified proof in the pending proof set until the winning
native object is FULL-durably cataloged. Its subsequent flush can then remove
the losing proof metadata. A restart may instead find that old temporary absent
and a different, independently verified final file already committed in the
same root/hash/length/native-identity catalog entry. Under the existing folder
coordination and pinned native guards, reconcile only the verified obsolete
proof metadata; never adopt the replacement from its name/hash alone or delete
any data. Missing/foreign catalog entries, changed bytes, links, replaced
temporaries, invalid proofs and reparse paths remain protected. Preserve the
existing 24-hour policy for actual staged bytes and all reference/loss oracles.
Use a bounded indexed ownership predicate rather than scanning the entire
catalog per proof. Add direct two-owner race/restart/native/catalog negative
controls and sanitized recovery diagnostics. A retained 100000-file fixture
must not be manually repaired or relabeled as a successful run.

Before policy pruning an indexed archive, FULL-durably record its exact archive
row, bound root/file identity, hash/length and expiry/cap reason under the verified
file guard. After native deletion is proven complete, atomically remove the
unchanged row and complete that retirement witness. On restart, finish only a
matching verified object or an already absent prepared target; preserve changed,
replaced, linked, locked and reparse objects. Unexpected missing archives without
such a witness may reconcile their rows, but do not authorize releasing history
references. Reference retention consumes completed witnesses or the explicit age
policy, never absence alone. Pending application still blocks policy maintenance.
Build persistent block-reference snapshots under the filesystem folder gate and
a separate per-index reference/index-mutation gate. Acquire the filesystem gate
first, then the index gate; index receive/recompute takes only the latter and
never waits for filesystem work. Application can therefore receive newer index
information during a download, while reference maintenance excludes both kinds
of mutation. Include all local chunk hints
and authenticated local live-file manifests (including withheld rows), every
candidate at a needed path, and authenticated root-bound history manifests.
Stage bounded batches in a separate snapshot and activate it with a FULL
transaction only after validation finishes; failure/cancellation keeps the
previous active snapshot. Never use an incomplete or stale snapshot to authorize
deletion. Unmanifested retained archives prevent sweeping until their content
references are captured, and policy retirement/reference release remains a
separate required step. Future transport dispatch must handle block responses
independently of a waiting serialized index handler.
Snapshot coverage alone is not deletion authorization. Archive creation and
missing-row reconciliation must retain provenance before removing their last
metadata, and upgrades must protect older cataloged objects whose historical
references cannot be reconstructed. The sweep must establish that protection,
refresh references under both gates, and hold them through verified deletion.
Preserve archive provenance transactionally at the database boundary: every
archive insertion records its exact row and current root binding with a stable
source identifier. Replacing metadata detaches the previous source and creates
a new source; removing an archive row detaches its source without deleting its
metadata. Seed existing archives during migration before any future cleanup.
Unchanged upserts do not duplicate provenance, and transaction rollback applies
to archive rows and provenance together. Provenance is local protection data,
not proof of file ownership or authorization to delete bytes. Native verification,
content manifests and explicit policy release must establish those separately.
For archive sources without an authenticated history manifest, verify the
independent private file under a no-write/no-delete-sharing read guard, hash
and chunk its exact indexed bytes, and capture independent blocks through the
ordinary durable ownership path. Stage a root/source/file-identity-bound content
manifest in batches of at most 1,000 chunks, then FULL-activate after complete
length/hash verification and block registration. Bound a manifest to 100,000
chunks; oversized/unverifiable sources defer with their protection intact.
The prior active manifest survives interrupted replacement and remains a
reference source if the archive subsequently disappears or changes. A content
manifest is protection metadata, not permission to delete an archive or blob.
On restart, complete staged manifests can FULL-activate after root/source,
contiguous chunk count/length, every independent block and the whole-file hash
are verified again, even if the original archive is now missing. Partial or
unverifiable staging remains protected until safe recapture or explicit policy.
Track snapshot archive coverage by stable provenance source ID, including
detached sources, so archive-row removal/reuse cannot make an unknown source
silently disappear from the reference safety checks. Missing/changed/locked/
linked/reparse sources without a manifest remain uncovered and prevent future
sweeping until explicit policy release is implemented.
Retention must account for history reuse before releasing references or
pruning physical archives. A newly retained authenticated snapshot can share an
older private archive with matching bytes: FULL-durable retention accounting
must protect the newer history's observation time while preserving the reused
file's metadata. Age selection and capacity ordering use that effective
retention time. Do not attach a new hold to an archive already reserved for
policy deletion; retain another verified copy instead. Existing history markers
must receive equivalent authenticated/root-bound accounting before pruning.
Legacy history bootstrap authenticates each exact marker and checks the pinned
root. Reuse a matching identity/hash/length under a held native guard when its
private source is still retainable, then FULL-account the unchanged marker.
Otherwise reconstruct the authenticated bytes through the ordinary kind-4
intent path, preserving the original observation time and current user files.
Missing/corrupt blocks or invalid markers fail visibly with prior protection
and any recovery intent intact. Bootstrap waits for pending application recovery.
Reconstruction also reserves the archive metadata namespace: do not install at
an already indexed path even when its file is absent. Index the invariant
uppercase archive path, backfill older rows in bounded durable batches before
opening the index, and keep the original spelling. This prevents ambiguous
source accounting without quadratic archive enumeration or ASCII-only matching.
Bind new archive retirement authorizations to stable provenance source IDs,
so a completed witness cannot authorize a later archive row which reuses the
same numeric ID and metadata. Legacy orphan protection, explicit private/public
history policy release, owned-object grace/cap selection, FULL-durable deletion
intents and recovery with fresh references remain required before enabling
block sweeping. Unknown, changed, linked, locked or reparse objects are preserved.
Policy reference retirement runs under both folder/reference gates after
authenticated bootstrap and before a fresh snapshot. FULL-delete an exact
accounted marker only after its observation window expires or its private
source has a completed, source-bound policy witness no older than the hold.
Public history additionally requires an authenticated published local row at
its conflict location, proving that ordinary current/deletion tracking has
observed that copy. An unobserved or unpublished public copy keeps its marker.
Release a private provenance source only when no history holds remain and
its age expires or a source-bound policy retirement completes; pending native
retirements defer release. Record the root/source/policy authorization durably,
including for detached sources. Absence alone and legacy unbound witnesses do
not authorize release. New history reuse atomically cancels an earlier source
release. Reference builders exclude explicitly released archive sources and
their staged content, while independently preserving current/needed/history
references. Policy retirement itself never deletes filesystem data.
Owned block cleanup records first-unreferenced observation durably in bounded
batches. A fresh activated reference resets that observation; re-registration
or identity/length changes also reset it. New catalog rows are distinguished
from pre-cleanup rows by migration, so a once-initialized root-bound legacy
window protects every older owned row for ArchiveDays without guessing from
old capture timestamps. Keep unreferenced blocks for ArchiveDays normally;
capacity pressure may retire them after a minimum 24 hours of observed grace.
Capacity uses the configured archive percentage of the root volume and never
evicts referenced or legacy-protected bytes. Report pressure that required
references prevent satisfying. Process at most 1,000 objects per deletion pass.
Hold the filesystem gate then the reference gate through fresh authenticated
coverage, orphan selection and native deletion/recovery. Pending applications,
uncovered archive sources and deferred capture proofs prevent sweeping.
Under a held independent-file identity/hash/length guard, reserve a FULL
deletion intent bound to the exact owned object and orphan observation before
deleting that held object. FULL completion removes only matching ownership,
orphan state and the exact intent after native absence is proven. Recovery
refreshes references: cancel a still-present deletion when references, ownership
or grace have changed; preserve unknown/changed/linked/locked/reparse data.
If deletion already occurred before a new reference arrived, complete recovery
and withhold stale local readiness for rescan/backfill. Never treat missing
bytes as ready. Keep intent metadata bounded and recover before new sweeps.
Incomplete capture expiry is a separate owned-temporary policy, not permission
to discard a corrupt installed block. After at least 24 hours from its flushed
capture proof, an unreferenced strict partial prefix may retire only after
fully authenticated archive/history coverage and no pending application work.
Verify the pinned root, independent proof/data objects, proof checksum and both
native IDs. FULL-reserve an exact proof/root/data/prefix-length/prefix-hash
retirement before deleting the guarded prefix, then remove only that verified
proof and FULL-complete. Unknown, replaced, linked, locked, reparse, oversized
or full-length corrupt objects remain untouched. The prefix was never a ready
published block; its proof records creation before writing, not a completed hash.
An expected hash may remain referenced when a duplicate complete block already
exists. Permit prefix retirement in that case only with an independently
verified, durably cataloged root/hash/length/file-ID replacement held under a
read guard through FULL completion. Bind its ID in the retirement. Revalidate
it on restart; a lost replacement cancels present-prefix retirement, or
withholds readiness if prefix deletion already occurred. Do not let obsolete
duplicate prefixes block all later maintenance indefinitely.
Ordinary capture recovery defers proofs with pending retirements. Recovery uses
fresh references and preserves changed proof/prefix bytes: cancel a still-present
retirement if its expected hash is needed again, or complete already missing
data with local readiness withheld. Retain durable authority if a proof vanished
while its owned prefix is still needed. Rebuild references after capture cleanup
and keep normal block sweeping disabled while any unresolved capture remains.
Share the 1,000-owned-data-operation budget across captures and completed blocks.
Recover old completed-block deletion intents before starting new partial work.
When capture proofs still defer, recovery may cancel a present deletion or
reconcile already absent data, but must not delete present block data. This
keeps readiness truthful and frees journal capacity without bypassing the
capture barrier. Share the 1,000 pending-journal bound across both intent tables.
Historical metadata cleanup follows a fresh fully covered reference snapshot
under both gates. FULL-mark explicitly released content manifests as retiring
before draining at most 1,000 metadata mutations per batch. Active/pending
readers and capture activation exclude retiring manifests; later captures do
not cascade-delete their remaining chunks. The durable retiring state permits
restart continuation without exposing a partially drained active manifest.
Before renewing any history hold by reusing a guarded native copy, verify and
durably recapture its exact signed chunks. A renewed source can then use its
authenticated history/new content while an older retiring manifest finishes.
Preserve native archive rows and attached provenance. Remove detached released
sources only after all content, holds, coverage and completed witness links are
drained; preserve pending or foreign recovery links. Delete only completed,
unlinked, unneeded policy witnesses older than ArchiveDays. Never infer policy
release from absence. Keep byte references, needed work, orphan clocks and
deletion journals unchanged. Report remaining cleanup work for prompt retries.
- **Renames:** the NTFS FileId shows a path change. It is published as delete plus create with the same chunks, so peers rebuild from local data.
- **Locked files:** opened with `FileShare.ReadWrite | Delete`. A sharing violation is retried after 10 s, backing off to 10 min.
- **Start-up speed (later phase):** read the NTFS USN change journal to find what changed while the service was stopped, instead of walking the whole tree.

The v1 scanner enumerates directory metadata and 128-bit file IDs together,
using handle-based Windows directory queries. File reads open relative to a
pinned root and reject descendant reparse traversal. A stored root identity
prevents silently reusing an index for a replaced root. Failed/incomplete
subtrees preserve prior metadata and never synthesize deletions. Reparse
entries and unrepresentable names are local unsyncable metadata, not followed.
Directories publish presence/attribute changes; child-caused directory mtime
changes alone do not create versions. Read-only scans track actual local data
without signing changes; the puller applies their local-change policy.
Upgrading to RW publishes unchanged valid entries that have no signed record.
Pending RO deletions become signed causal tombstones only when prior published
knowledge exists, preserving their observed deletion time. Never-published or
unsyncable removals do not invent tombstones. Missing signed tombstones regain
publication after temporary ignore/rescan suppression, with a fresh sequence
and unchanged edit vector/body. Recoverable SQLite busy/locked/I/O/full/open
errors enter the same scheduler backoff; corruption and invalid databases remain fatal.
Scan diagnostics correlate start/completion/retry/failure events by session,
with numeric visited/changed/hashed/skipped/deferred totals and native errors.
File paths, key material and record bodies are excluded from the journal;
relative-path problems are returned to the engine for local UI presentation.
The chunk lookup table also indexes `path_key`, so replacing a file's chunk
references does not scan the entire folder's chunk table. This is an additive
schema migration; existing databases are backed up by the migration runner.

### Version vectors and conflicts

| Situation | Outcome |
| --- | --- |
| Remote vector dominates local | Apply the remote version. The old local version goes to the archive. |
| Local vector dominates remote | Ignore. The remote peer will pull ours. |
| Vectors concurrent, contents equal | Merge the vectors. Nothing moves. |
| Vectors concurrent, contents differ | **Latest version wins:** newer mtime for live entries, signed deletion time (`changed_ticks`) for tombstones, then higher DeviceId. Every peer picks the same winner. The losing version goes to the archive on every peer that held it, and the UI shows a notice. |
| Losing version bigger than the archive limit (1,000 MB) | Kept as `name.sync-conflict-20261018-142233-ABCD.ext` instead, so it is never dropped silently. |
| Concurrent delete and edit | The later change wins, the delete included. An edit that loses goes to the archive. |
| Directory deleted remotely, new local files inside | Delete the known children. Keep the directory and the unknown files, which then sync back. |
| A winning directory replaces a local file | Retain the file under the ordinary archive/oversize/keep-both policy, then install a guarded empty directory. |
| A winning file replaces a local directory | Move the entire directory, including unknown children, to a no-overwrite sync-conflict sibling before installing the file. A directory tree is not a single file archive entry. RW peers scan/publish the preserved tree; RO peers retain it as a local copy. Preserve effective DACL access/protection and creation time; Windows normalizes directory-only ACL inheritance flags on a replacement file. The retained directory keeps its original ACL. |
| Concurrent live names differ only by leaf case | The existing deterministic conflict winner owns the original name. Preserve every other causally maximal version with a distinct leaf spelling under a stable `.case-conflict-<source-id>` sibling before merging its vector. |
| Name invalid on Windows | Mark the record unsyncable here and show a warning. Don't block the rest of the folder. |

Case handling must retain competing signed versions before a merged vector can
hide the original concurrency. An empty receiver with two remote case spellings
needs both visible payloads, as does a peer that already owns one spelling.
Archives/private block caches alone do not satisfy the `.case-conflict` rule.
Ordinary causally ordered case renames, including an edit accompanying the
rename, must not create spurious case-conflict copies.

Begin with connected-engine regressions for both arrival orders, local-winner
and remote-winner paths and causal rename controls. The preservation design
must bind pending work to authenticated source envelopes and root identity,
retain reference coverage, survive restart/source replacement and reserve
destinations without overwriting. Incoming signed paths cannot be rewritten:
RW publication of a preserved copy requires an ordinary new local record; RO
copies remain local. First-name ownership and subsequent suffix allocation
must remain consistent across gossip/restart and preserve every source
version. The user approved deterministic original-name ownership on 2026-10-19;
literal per-computer packet arrival does not assign ownership. Source IDs hash
the domain `PeerSync case source v1` followed by the original signed body and
signature, independent of forwarding device and native root. Use the existing
bounded sibling-name rules, adding a numeric collision suffix without replacing
unknown objects. Parent-only spelling changes do not create child case copies.

Admission adds immutable source envelopes to a root-bound preservation ledger
in the same transaction as remote rows, needed work and receive cursor. A
pure resolver may return preservation sources with its needed row; storage
validates path/identity bounds, freezes buffers, rejects mismatched duplicate
IDs and applies backpressure at 1,000 pending sources. Completed markers are
separate from that pending limit. Replacing remote rows cannot release the
ledger's original envelope. Accepted index updates and this ledger are FULL
durable before acknowledgement; replay and failed admission cannot partly
advance the cursor. Recompute must discover the same obligations idempotently.

Pending sources hold authenticated block references even if their remote row
is replaced. Pending original and reserved destination paths also inhibit
staging expiry. Normal original-path application/vector merging must wait for
the preservation barrier, including selection, final mutation and writer commit
checks. An arrival while an old operation is in flight must defer or safely
recover that operation without overwriting either version.

Materialization reserves a no-overwrite destination and a kind-specific FULL
root/source/destination/FID-bound intent, assembles and verifies independent
blocks outside the reference gate, and installs only through guarded moves.
Recover before scanning, preserving changed or foreign occupants. RW copies
are ordinary scanner publications, never rewritten incoming signed paths; RO
copies remain local. Guarded visible bytes and required local publication must
be proven before completion releases the original-path barrier. Retain stable
completed-source accounting to suppress duplicate gossip copies, with bounded
eventual metadata retirement. Restart, source replacement, forwarding, occupied
destinations, cancellation and reference/expiry races require tests. Directory
and nested subtree mapping remain mandatory before the parent task closes.

The first materialization slice handles regular-file sources. Recovery kind 7
contains the exact signed source, portable ID, root, collision/destination and
owned GUID temporary/FID, with explicit preparation. Reserve the destination
and intent in one FULL transaction. After guarded installation, atomically
record destination/FID and remove the intent while leaving the source pending.
This allows ordinary scanning without releasing the original-path barrier.
Only a subsequent guarded byte/identity check plus the matching ready local
publication (RW) or local observation (RO) completes that source. Unknown or
changed destinations get another no-overwrite slot; matching content alone
cannot grant native ownership. Incomplete recovery parks owned staging and
keeps the obligation for a later network retry; prepared recovery installs or
accounts only its verified object before scanning. Directory sources remain
pending until authenticated subtree mapping is implemented.

A received case copy can arrive before the original clash is resolved locally.
Reuse it as visible evidence only when its local row is a ready authenticated
publication at the exact portable candidate, its native file ID still matches,
and a guarded whole-file check matches the pending source. FULL-account that
observation against the same local sequence; it grants no deletion ownership.
An unindexed, unsigned, changed or merely same-named occupant is a collision.
The ordinary scanner/publication completion barrier still applies. This avoids
duplicate suffixes from gossip without trusting a filename as ownership proof.

Case retries are per portable source and root, FULL durable with failure time,
attempt count and 10–600 second exponential deadlines. Visit at most the 1,000
pending sources per pass, defer unsupported subtrees and inaccessible sources
without blocking independent work, and include the earliest due case deadline
in the coordinator timer. A backwards wall clock makes old deadlines due;
successful installed/publication accounting clears the retry. Cancellation
leaves recoverable owned staging and does not complete the source.

After a completed source has aged at least 24 hours, bounded FULL metadata-only
compaction may remove its large signed envelope/forwarder fields while keeping
the root/path/portable-ID completed witness. That compact witness suppresses
later exact authenticated gossip and conveys no native deletion permission.
Never compact pending sources, future completion times or a foreign root.

Preservation admission is separate from ordinary needed scheduling. A CPU-only
resolution can admit sources while removing unneeded work; completed RO sources
must not recreate an unchanged original-path task. Treat pending-cap saturation
as recoverable backpressure. After a successful native scan, allow already
admitted sources to install, scan and complete even if full needed recomputation
is capacity-limited. Block ordinary application until that repair completes,
then resume remaining batches; never spin on blocked ordinary deadlines.
Startup/local recomputation may commit a valid prefix within each bounded
batch when preservation capacity is reached. Use a per-path savepoint so the
failing path retains neither partial source admission nor changed needed work;
commit the prefix FULL before reporting recoverable saturation. This permits
progress when unavailable older sources leave fewer than a batch's free slots.
Remote receipt remains whole-batch atomic with its cursor. Other resolver
failures or cancellation roll back the current entire recomputation batch.

Directory case preservation requires a signed description of descendants.
Add optional directory_snapshot_hash and exact directory_snapshot_length
fields to live directory bodies, binding a
Merkle snapshot of exact original signed child envelopes and nested directory
roots. Missing legacy hashes never prove emptiness. Capture bottom-up after
children publish, retain the complete graph in reference coverage and keep
original-path merging blocked until guarded copied members are visibly complete.
Native child-caused directory mtime alone remains ignored; an authenticated
child-manifest change now advances the directory's vector/hash. This extends
the earlier metadata-only publication rule without signing incidental native
mtime changes. Require convergence/crash tests before enabling mutations.
The bounded canonical node/envelope encoding, migration limits and staged
acceptance are specified in docs/proposals/P1-directory-snapshot-protocol.md.
Snapshot builders consume strictly increasing canonical immediate-child keys,
form groups of 128 records, and sort only the bounded links inside each page.
Carry full pages into bounded 128-link Branch buffers at successive levels;
finalize partial tails bottom-up. This fixes deterministic grouping without
retaining or globally sorting an entire directory in RAM. Per-pass persistent
capture/traversal continuation remains required before production activation.
Graph validation uses a FULL SQLite continuation bound to the physical root
and original signed directory source. Persist pending page/record work, seen
context/hash pairs, unique canonical child keys and exact metadata/file-chunk
references. Commit discovered children and completion of their parent work
atomically only after independent metadata bytes are durably accounted. Work
budgets return pending, resume after reopening and never activate partial
reference coverage. Missing metadata, invalid kinds/signatures/parents,
duplicate keys and excessive page nesting retain fail-closed protection.
Touch graph caches when their source is encountered. After a complete fresh
reference activation, mark only caches unused for 24 hours for retirement.
Delete at most 1000 continuation/member/reference/job rows per refresh, using
a persistent retiring flag. Resume existing retirements before graph work;
a newly needed retiring graph waits for bounded cleanup and rebuilds from
its signed root. Never expose partially retired coverage or delete native
blocks merely because graph cache metadata expires.
Resumable capture retains a bounded builder checkpoint containing the accepted
member count, partial Record links and full-page carry links. Validate canonical
base-128 count distribution, link lengths and exact state size before restore.
Only checkpoint successful additions after owned metadata flush; a failed
addition discards the in-memory builder. Bind the eventual durable checkpoint
to root, parent, ordered input manifest and continuation cursor before scanner
activation. Builder checkpoints alone are not complete subtree proofs.
The durable capture draft binds root, original parent spelling/native identity,
ordered envelope manifest/count and accepted prefix/cursor. Flush independent
bytes before FULL compare-and-swap progress; recheck unstable enumeration before
committing. Capture at most 1000 new members per advancement. Pending and
complete-but-unpublished drafts block fresh sweeping until publication or safe
expiry; one row per parent and bounded 24-hour retirement limit abandoned state.
See docs/proposals/P1-directory-capture-continuation.md. Scanner publication
must validate the native binding and atomically release the draft with its row.
For scanner integration retain the previous signed directory row and stage
metadata privately until exact child publications and a replacement root are
ready. Use indexed parent keys, UTF-16BE ordinal sort keys and row/byte-bounded
child batches. Advance trusted local-index drafts between full scans with a
trigger-maintained local mutation epoch (including non-sequence readiness
changes); revalidate changed inputs without discarding an
identical manifest. Keep arbitrary enumerable capture on its two-pass path.
FULL-bind a revalidated manifest to its exact epoch and compare that witness
before progress/publication; reject counter overflow rather than wrapping.
Pending-publication dependencies/barriers and bounded coordinator continuation
are detailed in docs/proposals/P1-directory-scanner-publication.md; they must
prevent mtime/vector feedback and pass convergence before activation.
Retain authenticated signed child membership when local readiness is cleared;
availability changes are not signed deletions. Metadata capture does not set
file readiness or claim available bytes; materialization verifies each chunk.
The next integration slice uses private versioned publication templates while
retaining the previous signed row, direct-child inventory/dependency checks,
bounded coordinator continuation between scans and a compact published-input
cache. No-op roots release drafts without vector/sequence churn. Namespace and
GC barriers, retry/fairness and readonly reconstruction remain mandatory; see
docs/proposals/P1-directory-publication-queue.md before enabling scanner changes.
The native and indexed child inventories share a streaming BLAKE3 fingerprint:
domain `PeerSync directory child keys v1`, canonical parent and direct child
keys as UInt32LE byte lengths plus UTF-16BE bytes in strict ordinal order,
then UInt64LE child count. Read the indexed keys and mutation epoch in one
SQLite read transaction without loading record bodies. Match both digest and
count before binding queue keys to that epoch; this proves membership only,
never signed content, native ownership or local byte readiness.
Keep scanner snapshot staging internal and inactive in production until
incoming-directory and subtree integration passes. Its local-source preview
retains old signed rows and queues complete supported native inventories after
file/tombstone commits. Reuse identical private templates and cached no-op
observations, pause uncertain namespaces, and discard obsolete queue/draft
metadata only after a complete walk proves absence, another type or exclusion.
An incoming authenticated snapshot without a matching local publication cache
is reconstruction-only: never infer a user deletion from not-yet-applied
remote children. Its graph/materialization proof must be integrated before
activation; readonly scans retain their existing unsigned-edit behavior.
Subtree materialization starts with a bounded, reauthenticated projection from
the complete non-retiring original graph into a stable case-copy root, without
rewriting any signed member. Page canonical keys, preserve causal parent-case
aliases, bound envelope memory and flush fetched metadata before continuation.
This projection proves no native ownership/readiness. Follow
docs/proposals/P1-directory-subtree-materialization.md for the subsequent
root/member-bound no-overwrite journal, recovery and publication requirements.
Begin native case-root creation with distinct local intent kind 8: authenticated
original directory/source and physical root, stable collision slot, exclusive
GUID temporary/native identity and prepared state. Require complete graph
coverage, FULL reserve before preparation/rename and FULL identity accounting
before intent release. Preserve changed/occupied temporaries and replay by
native identity. Share a 1000-unit graph budget; root creation alone never
completes subtree preservation or publication.
Member installation uses schema-28 source/destination-root-native-ID keyed
cursors and original graph-link/member-ID/destination rows. FULL kind-9 intent
reservation and installation/cursor accounting bracket guarded no-overwrite
mutation. Reauthenticate local original envelopes during recovery and verify
every parent native ID without quadratic handle retention. Collisions/edits
preserve earlier partial trees and select another root slot; installation
coverage alone never completes publication. Details remain in the subtree
materialization proposal, including namespace/publication activation barriers.
Pending case preservation blocks ordinary incoming source/copy namespace
mutation including descendants and destructive ancestor transitions; keep the
narrow existing missing-ancestor-directory creation allowance. Share exact
ancestor/range logic with late/recovery and FULL commit guards. Hold directory
advertisements at/above/below unfinished copied roots until identity-bound
member coverage is exhausted, preserving signed rows and pausing templates.
Coverage releases only publication holding, never case-source completion.
Complete subtree sources through a separate bounded verification cursor bound
to root native identity, local mutation epoch, root publication sequence and
RW/RO mode. Reauthenticate members, verify guarded native bytes and observed
rows, require current cached signed snapshot/ready records for RW and unsigned
preservation for RO, and FULL-release only after complete stable coverage.
Keep completed RO original graphs/envelopes referenced for ArchiveDays rather
than dropping them through the generic 24-hour case compactor. Expiry authorizes
no native deletion. See the subtree proposal for witness/reset/commit details.
Retire obsolete directory-copy member witnesses in bounded FULL transactions,
sharing at most 1000 member/progress deletions per call. Keep every pending
current-root witness and all witnesses while application recovery is pending.
Completed attempts wait 24 hours after completion; superseded-root members wait
24 hours after their last touch. Remove a superseded or aged completed progress
row only after its members are gone. This metadata-only cleanup never releases
original source/graph retention or mutates visible copies.
Reconstruct received signed directory roots through explicitly tagged private
work. Such work retains the exact received template and can only release it
unchanged after the bounded captured root matches; it never signs an absent
child set. Reconstruction holds GC and parent snapshot publication but permits
ordinary incoming mutations so missing children can arrive. Scanner observations
refresh its native child-key inventory and changed input epochs wake its retry.
Keep ordinary publication barriers and final case completion guards intact.
Enable production staging only after incoming/subtree convergence tests pass.
First wire an internal connected-coordinator preview: stage on complete scans,
run one bounded publication pass after case preparation and before completion,
repair needed paths after publication, and include durable publication retry
deadlines in the puller's wake calculation. Keep the public production path
inactive until connected acceptance passes; preserve existing public constructors.
Once member installation is exhausted, preparation yields scheduling authority
to verification/completion instead of refreshing its retry deadline. Completion
owns pending scan/publication backoff. Connected simulations advance injected
UTC to a reported future deadline; they must not spin past a real backoff or
sleep in wall time to simulate service scheduling.
Before activation, ordinary applied directory/file version changes must also
request preview snapshot reconciliation. Transport-backed metadata fetching must
cover every protected directory root, independently of native copy work and
needed-path drain. Persist exact root/source-bound envelopes and route hints
before replacement can lose them; advance authenticated graphs in bounded work,
with persistent retry and source fairness. Do not fetch over the network while
holding the reference gate or weaken GC coverage to hide missing graph metadata.
Queue admission/retirement and known-source coverage follow the metadata-fetch
proposal; visible copies and signed records remain independent of this cache.
Schema 32 bounds source envelopes at 4096 per folder and admits old local,
selected original/desired and case roots with needed/index updates. Use durable
run ordinals for fair 32-source/8-MiB/1000-graph-unit passes, persistent retry and
24-hour unrequested metadata-work expiry. Signed source owners remain separate;
required missing graphs continue to block GC and re-admit work. Recover retained
RO directory originals through bounded rooted case-ledger pages; existing
`_history-` markers remain file-only. Release completed queue entries only
against the exact complete non-retiring graph. See the proposal for transitions.
Metadata route fallback uses two preferred hints plus six paged known devices,
with a source-specific durable cursor and wraparound, independent of retry UTC
and source run ordinals. Later authenticated forwarders must remain reachable.
Each route has a one-second linked deadline inside the ten-second source
attempt, so a stalled preferred hint leaves time for fallback. Source/caller
cancellation still stops work; transport fetches must honor their token.
Received directory roots whose child envelopes were causally superseded may
rebase only on RW peers after bounded, durable proof of every original direct
child against exact or dominating signed local versions. Bind schema-33 proof
to queue/root/native/capture/graph and the local mutation epoch; missing or
concurrent members remain pending. Final guarded FULL publication consumes the
complete proof, increments the own directory vector once and preserves parent
mtime/history. RO exact reconstruction remains unchanged. Follow
`docs/proposals/P1-directory-reconstruction-rebase.md` before activation.
After the connected/recovery acceptance set passes, public Puller and
TransportFolder coordinators enable bottom-up directory snapshot publication.
Standalone Scanner retains legacy mode because it does not own a publication
worker. Validate public nested publication, case preservation and no-op behavior
with full local checks before CI/main acceptance; see
`docs/proposals/P1-directory-production-activation.md`.
Integration must retain observed directory attributes separately from received
templates, durably defer unavailable legacy/corrupt subtree sources, reconsider
suspended descendants when file ancestors cease blocking them, and bind/pin the
physical root before public index metadata admission. Original signatures,
canonical ancestry, recovery/root guards and session admission generations stay
binding. Follow `docs/proposals/P1-directory-review-hardening.md` and reproduce
the four PR #38 findings before a batched repair.

Automatic directory publication closes its original parent clock over current
authenticated records with the exact same non-vector body and signed snapshot
root, then increments the local counter once. It never absorbs a different
subtree/metadata/spelling as equivalent proof. Shared construction and final
reference-gated validation must agree on the current same-body clock floor;
late changes retry. Follow `docs/proposals/P1-directory-parent-clock-repair.md`
for owned restaging, received reconstruction and quiescence race regressions.
When parent envelopes differ in snapshot root, do not absorb their clocks
without durable causal proof of every eligible original root against the
current signed child input. Preserve bounded progress/graph holds across
vector-only forwarding and revalidate the root set at final commit; see
`docs/proposals/P1-directory-parent-root-set-proof.md`.

The immutable source's first forwarding device is only a routing hint. Current
advertisements cannot discover every retained old member payload after parent
versions advance. Use source/root-bound durable route hints and known-device
cursor paging, one six-device page plus two hints per source attempt; validate
owned response bytes against signed length/BLAKE3 regardless of advertisement.
Bound each candidate to one second plus requested bytes at 256 KiB/second
(at most 33 seconds for 8 MiB), and the source attempt to 300 seconds,
propagate caller cancellation and keep network awaits outside reference gates.
Do not change the portable source ID or weaken recovery/graph/native guards.
Follow `docs/proposals/P1-case-payload-routes.md`, including restart, corruption,
timeout, route-page and retained hierarchy failure regressions.
Prepared recovery treats unclassifiable/reparse destination probes as occupied:
preserve the occupant, park the verified temporary using its held handle and
release the intent for another no-overwrite slot instead of blocking the folder.

Because mtime picks the winner, peers whose clocks differ by more than 10 minutes don't exchange changes, as in Resilio, and the UI says why. A per-folder setting, "Keep both copies", saves every losing version as a conflict copy instead.

Each authenticated peer session retains a frozen Hello UTC sample and the local
monotonic receipt timestamp. Before sending/receiving index data, estimate the
peer's advancing UTC from monotonic elapsed time and compare current local UTC.
Exactly ten minutes is permitted; a larger absolute offset blocks index exchange.
A missing/invalid sample, backwards elapsed time or unrepresentable estimated
UTC blocks exchange as well. This also detects local wall-clock jumps during a
session. The remote implementation enforces its own side; Hello is a clock
sanity check, not a defense against a malicious authorized folder writer.
Disconnect resets admission; a fresh authenticated Hello can restore it.
Expose signed offset ticks and a status reason with the human-readable clock
difference for the control UI. Log transitions, session IDs and numeric offset
seconds without peer names, identifiers, nonces, keys or file data. The UTC Hello
and decision model are P1 foundations; actual index admission and rendered UI
remain required parts of P1.5.5 before its checkbox can close.

A valid authenticated Hello exchanges both UTC samples even if the receiver's
clock guard already blocks index exchange. Each endpoint then evaluates its
own signed offset (peer UTC minus local UTC); the remote endpoint's exception
must not masquerade as the caller's local clock measurement. Index and block
operations retain their existing guards. Hello still validates protocol,
identity and nonce before updating clock state.

The control status source reads immutable session-bound snapshots rather than
reconstructing state from logs. Keep the authenticated peer identity and local
session correlation ID separate from untrusted Hello fields. On disconnect,
admission resets immediately, but a closed session's last skew/invalid reason
and numeric offset remain available for display. A fresh valid session clears
the corresponding closed-peer warning. Bound recent closed history to 64
peers, bound each snapshot to 1,000 rows and signal truncation explicitly.
Active sessions remain host-owned and must be drained. Display snapshots never
grant admission. Typed source/lifecycle checks precede control-pipe and rendered
UI wiring; those remain mandatory before P1.5.5c or its parent can close.

An authenticated peer-bound index facade supplies the source identity from its
transport context, never from Hello. It checks clock admission before receiving
data, after acquiring the reference gate, and in the writer transaction both
before changes and immediately before returning for commit. The writer callback
does no diagnostic I/O and cannot reenter storage. A changed clock rolls back
records, needed work, cursor and range metadata together. Materialized outgoing
pages are checked before reading and again before handoff. Bound pages freeze
their record list and enforce cursor/order/count limits. Transport integration
must use this facade; raw engine/storage entry points are trusted local
primitives, not authenticated network admission. Disconnect resets the session
sample, including admission of queued work.

Clock integration validation is phased by the user's explicit 2026-10-20
approval. P1 retains the peer-bound engine admission facade, immutable Hello
and transactional/handoff checks, public identity-bound in-memory integration,
typed runtime lifecycle, actual authorized local pipe with owner/kernel PID
verification, and inspected real WPF numeric skew/recovery/failure renders.
Actual installed PeerSync service supervision and certificate-bound production
QUIC/TCP/folder-handshake integration are unverified and remain mandatory at
P2.5.7 and Gate P2. This resolves the forward dependency without changing clock
policy, pipe security or explicit unavailable production status. The default
client must be exercised against the fixed installed service/account, including
running/stopped/missing and wrong-owner/PID/cancellation/caller authorization.
Real authenticated peer clock skew must block all index exchange, display the
numeric reason and recover through the actual control UI after reconnect.
Retain native evidence using generated peers and injected peer clock samples;
do not alter the host clock or overwrite an existing service installation.

The source design's mtime rule takes precedence over the draft checklist's
blanket `changed_ticks` comparison: a newly noticed old offline file must not
beat a genuinely newer edit. Tombstones use their signed deletion timestamp
because they have no live mtime. Causally dominated candidates are removed
before choosing among concurrent maximal versions; merge their vectors by max,
without incrementing a device counter for a merge alone. Equal timestamps and
DeviceIds use the exact signed body as a final stable tie-break. Matching file
contents still choose deterministic metadata (path case, attributes, mtime).
Only RW peers sign a new merged envelope; RO peers retain original signatures.
Candidate causality always uses the vector in the signed record. Validate an
RO cache's dominating logical knowledge, but merge that knowledge only after
winner selection; it must never make old contents concurrent with their signed
causal descendant. Re-signing a merged vector preserves unknown fields inside
existing vector entries as well as on the enclosing body. Reject unsupported
attribute bits before storing a verified version. Conflict-name truncation
uses a safe fallback stem if the truncated basename becomes a Windows device.
Remote index records, the sender cursor and affected needed rows commit in one
writer transaction. Verify envelopes before submission; then compute affected
path winners from the transaction's current local/all-remote snapshot. Replays
do not change rows or regress cursors. Local changes can recompute only affected
paths, with a full rebuild available after startup/settings changes. Ignored,
unsyncable and pending-rescan paths are withheld from application. RO local
changes remain needed but blocked until OverwriteLocalChanges is enabled; an
exact local match can adopt the original signature without rewriting data.
An additive remote-path index supports per-path winner queries across devices;
the device-first primary key alone cannot serve that access pattern efficiently.

### Applying remote changes

The pull coordinator consumes bounded durable work descriptors computed from
authenticated decisions in the same transaction as needed rows. Descriptors
cache stage, size, mtime and a version/target fingerprint, so priority queries
do not load every signed body. Directory creation precedes files; files use
configured size/age priority with stable path ties; file deletions precede
deepest-first directory deletions. The applier rechecks the actual decision.
Persist each sender's monotonically advertised target cursor before accepting
destructive scheduled work. A received cursor must reach that target before
its tombstones apply; missing knowledge blocks deletion. Recheck under the
reference gate through deletion, without holding that gate across downloads.
Index-generation reset handling belongs to the P2 transport; never silently
lower an outstanding target for the same generation.
Persist root/fingerprint-bound retries only while that exact needed work is
current. Back off 10, 20, 40, 80, 160, 320 then 600 seconds, capped at 600;
new work invalidates the old delay and removed needed rows cascade retries.
Clock rollback must not produce an unbounded wait. Needed hashes remain
protected while work waits. Downloads route by the source selected inside the
applier, not a stale outer snapshot. Recover before startup scan, rescan kept
unknown/conflict data outside mutation gates, and continue independent work
after deferred targets. Case-clash and hierarchy coordination remain required
parts of the complete puller, not implied by queue metadata alone.

The host owns one pull coordinator per folder. Its bounded passes recover pending
intents, scan at startup, rebuild legacy needed metadata once, and then consume
up to 1,000 eligible entries. Filter cached deletion stages awaiting sender ranges
before LIMIT so waiting deletions cannot starve complete independent work. The
applier still checks actual work under its gates. A fresh unbound index with no
intents can bind and scan; unbound recovery state fails closed. All coordinator
scans share the process-wide rescan budget. Later scans return changed paths for
incremental needed recomputation. Coalesce preserved/changed-target scans once
per batch and perform them outside mutation gates. Follow-up history scans and
transient scan failures request another pass, with bounded delays on failure.
Recover failed staged operations before attempting another target; if recovery
itself is unavailable, stop that pass and retry the folder. Preserve exact-work
backoff through restart and continue other eligible paths after ordinary failures.
The cancellable loop wakes on coalesced index/local notifications, the next
eligible durable retry or the periodic scan deadline. Watcher/host notifications
request scans explicitly; waiting for work holds no mutation or scan gate.

Scheduled deletion intents persist their barrier requirement in local recovery
formats (kind 5 file/type transitions and kind 6 directories; legacy kinds 1/3
retain direct-call semantics). Staging abandonment needs no range barrier, but
prepared deletion recovery reacquires the reference gate and checks the sender's
current target through all remaining destructive mutation and commit. Incomplete
knowledge preserves the journal and target while index receipt remains available.
Live timestamps unrepresentable by Windows mutation APIs are blocked per item,
without terminating independent work. Incremental scans include native-cache-only
paths and filter invalid wire names before needed selection. Mark needed repair
pending before each scan, clear it only after needed recomputation succeeds, and
rebuild on the next pass after partial scan/recompute failure; fresh coordinators
already rebuild at startup. Never infer an unchanged second walk repaired a
partially committed first walk.

Filesystem mutation primitives open every directory component relative to a
pinned root, reject reparse traversal and hold directory leases that disallow
rename while the operation is active. Creates use exclusive-create semantics;
archive/conflict moves use handle-based no-overwrite renames into a pinned
destination directory. Private `.psp` paths and generated temp suffixes have
separate internal validation, never a public remote-path escape hatch.

The atomic existing-target replacement primitive must retain its verified
target guard across installation. Validate native FileRenameInformationEx
replacement/POSIX semantics with that guard held before using it in the
coordinator. Preserve the old object through a no-overwrite archive link/copy
before the atomic replacement. Ordinary no-overwrite moves remain appropriate
for new targets. Unsupported filesystem behavior must fail without reducing
the atomic-replacement or no-lost-version requirements.

Replacement preparation reads the original file's DACL/inheritance protection
and creation time through its held handle and applies them to the sibling
temporary file before moving the original. New files inherit their parent.
Validate a remote mtime against Windows FILETIME representation before any
destructive mutation; apply times before the synchronized read-only/hidden
attributes. Native metadata and ACL helpers borrow handles without closing the
caller's guard. Owner/group/audit sections are not part of the synchronized ACL.

1. Create new directories, shallowest first.
   Parent integration must also cover receiving nested tombstones on a fresh
   node where the former parent is absent or is now a file. Such a descendant
   has no physical object to remove: preserve every existing ancestor and user
   object, retain the sender-range barrier and durable metadata rules, and
   converge the signed deletion without attempting to create/traverse a parent
   file. Test this through connected production folder engines, including
   retained old child bytes and restart/concurrent parent changes.
   Probe absence using the pinned root and no-reparse component handles. Only
   missing components or ordinary-file ancestors establish absence; access,
   sharing and reparse errors remain failures. Complete such tombstones through
   the existing FULL intent/completion path without native deletion, holding
   component guards and the range barrier. Re-probe on recovery and preserve a
   concurrently created descendant using the ordinary changed-target path.
   Request a fresh scan after metadata-only completion; it observes concurrent
   creation without removing that new data. Retained originals still require
   their existing identity/hash/accounting checks.
2. Download files in the folder's priority order: default, smaller first, larger first, older first or newer first. Resilio offers the same five.
3. For each file, assemble it in an exclusively created hidden `GUID.!psp`
   sibling next to the target, from local chunks first, then network chunks.
   This supersedes the predictable `name.ext.!psp` example. A new file created
   there inherits that directory's permissions. Persist ownership using the
   pinned root identity and file ID before assembly; reuse parked bytes only
   when those identities still match. Never truncate or delete unowned names.
   Verify every chunk and then the whole-file hash.
   Existing temporary bytes are also reusable only after chunk verification.
   Parked staging uses a FULL-durable catalog tied to the live apply intent,
   root/file identity, original target, GUID sibling name, first parked time,
   kind, native change stamp and exact file length/hash. Capture under the
   existing exclusive handle or a verified reopened guard. Re-parking an unchanged
   fingerprint preserves age; newly settled bytes/native changes get a new grace period. Resume only
   unchanged cataloged objects; changed/foreign objects are preserved. Legacy
   unaged pointers remain compatible for verified reuse but never authorize age
   cleanup until adopted under a verified apply/recovery context.
   Every recovery/defer branch that already owns the temporary handle must
   pass that handle into fingerprint/protection capture; an exclusive reopen
   would fail sharing and must not downgrade edited prepared bytes to legacy
   reuse. Reusing a cataloged case-alias sibling keeps its native identity and
   catalog spelling, but records the new intent's temporary path under the
   current target-parent spelling so the strict recovery format remains valid.
   Expiry requires at least 24 hours, no needed work for the target and no active
   apply. Stream verification outside the reference gate, then recheck under
   filesystem/reference gates before a FULL deletion intent and native removal.
   Recovery rechecks ownership and new needs. Delete only guarded exact files or
   empty owned directories, never recursively/by filename. Protect changed or
   foreign objects, preserve needed prefixes and bound each pass to 1,000 rows.
   Persist a root-bound catalog cursor so needed/locked objects cannot starve
   later entries. Scan at most 1,000 witnesses per pass, including skipped rows,
   and request continuation when a page is full. Verify files outside the
   reference gate while retaining native guards, then recheck age, current needs,
   exact witness and absence of active apply in the FULL reservation. Prepared
   retirements are recovered first. Inaccessible preparations may be canceled
   durably without deleting bytes, retaining their witness for a later retry and
   freeing bounded journal capacity. Confirmed absence can retire metadata only;
   a foreign replacement is preserved. Protected/legacy objects never gain
   deletion permission through enumeration or filename matching.
   Cold recovery revokes any unfinished staging deletion permissions before
   application recovery/scanning; it does not delete using a possibly stale
   needed set. A fresh successful scan/recompute precedes expiry. Missing rows
   are reconciled afterward. Cursor pages include all parked rows and advance
   across ineligible entries; completed cycles wait an hour, full pages continue
   immediately and transient deferrals retry with the coordinator's backoff.
   Local index entries are hints: reject reparse traversal, verify the actual
   bytes and try at most 64 candidates per chunk before network fallback.
   Copy transport data into an owned, bounded chunk buffer before hashing it.
   Assembly never installs a target or publishes index metadata. Debug events
   distinguish temporary/local reuse from verified network chunks with counts,
   timing and a session ID; they contain no paths, hashes or content.
4. Re-check the target just before replacing. If it changed since the database last saw it, scan it now and re-run the conflict rules.
5. Use the user-approved recoverable handle moves (2026-10-19), superseding the
   original `File.Replace` example. Keep the verified original and directory
   guards held; preserve its DACL/creation time on the fully verified, flushed
   temporary file. Persist a FULL-synchronous intent bound to the root, target
   and temporary file identities before moving the old handle to an exclusive
   archive/conflict name. Move the temporary handle into the vacant target
   without overwriting. Final installation is atomic; archival plus installation
   has a brief missing-target interval visible to external applications.
   Serialize scanning/application and recover pending intents before scanning.
   Finish from verified temporary data, or restore the retained original if the
   target is vacant. Never overwrite an unknown concurrent target. Commit the
   local/chunk/sequence/archive/needed state and intent removal atomically with
   FULL durability. Apply mtime before read-only/hidden attributes. Failure and
   cancellation preserve all versions and enough identity-bound recovery state.
   If an unknown target wins a race, account for verified retained originals,
   mark stale local metadata for rescan and remove the intent in one durable
   transaction. Keep directory and original-file guards through that commit.
6. Apply deletions last by moving files to the archive, then remove emptied directories deepest first.
   Directory application has its own bounded, signed recovery intent. Create
   parents shallowest first and bind the directory handle identity before
   setting metadata or requesting deletion. Never recursively delete a tree.
   Apply individual signed child tombstones through the regular file path
   before the parent, after receiving the advertised sender index range; a
   partially received index must not cause premature parent resurrection.
   A parent record is not a substitute child signature.
   If a parent is still nonempty, keep it and its children, record the logical
   signed tombstone and request a normal scan. RW scanning then publishes the
   surviving directory and unknown children as local descendants; RO scanning
   keeps local changes unpublished under its normal overwrite policy. Native
   nonempty checks protect a child created concurrently with deletion. A changed
   identity/type is preserved and rescanned. Recovery dispatches directory
   intents alongside file and restore operations before scans are permitted.
   Parent-path case follows the corresponding directory records. Within an
   already matched case-folded path key, only a change to the entry's own leaf
   spelling is a case edit. Changing a directory's spelling must not invent
   edits to unchanged descendants or rewrite their original signed bodies.
   Type transitions use owned sibling directory/file staging and the same
   journaled two-move protocol. Bind the old node's kind and identity; preserve
   whole directory objects without recursive traversal/deletion. In the durable
   completion transaction, withhold stale live descendant metadata and remove
   descendant needed work beneath a moved directory until a normal rescan.
   A currently installed, signed live regular-file ancestor also suspends live
   descendant pull work, including late index pages. Retain remote envelopes and
   case obligations; never manufacture descendant tombstones from the parent.
   Resolve the nearest such ancestor in the same needed-rewrite transaction and
   authenticate it in the engine. Fresh file completion withdraws live descendant
   work atomically, retaining deletion stages. When application or local snapshot
   publication changes a signed live regular-file parent into a live directory,
   rebuild needed work after the required scan so retained child versions resume
   normal conflict selection. A previously absent parent has suspended no child
   work and does not require this extra scan/rebuild.
   Incoming snapshot equality/rebase requirements remain independent.

### Archive, ignore rules and special files

- **Archive:** `.psp\archive\` keeps replaced and deleted files for 30 days, up to 1,000 MB per file, plus a total cap (default 10% of the volume). A daily task prunes it. Restore is in the UI from phase 5.
  Pruning shares the folder operation gate and waits for pending recovery.
  Expire old entries first, then remove the oldest remaining versions until the
  cap holds. Use the actual hosting volume capacity, independent of user disk
  quotas. Delete only indexed archive files whose guarded bytes still match
  the recorded size/hash; pin every ancestor, reject reparse traversal and keep
  changed or locked entries for retry. If the oldest eligible version is blocked, defer cap-only eviction of
  newer versions; independent age-expired versions can still be pruned. Remove
  filesystem data before its index row so an interrupted cleanup leaves a reconcilable missing-file row, never
  unaccounted retained data. Missing indexed files are reconciled on retry.
  Do not recursively sweep unknown archive files or directories. A persisted
  successful-run timestamp schedules daily cleanup across host restarts;
  deferred cleanup retries with bounded 10–600 second backoff. Restoring content
  remains a normal local edit, distinct from forwarding an archived signature.
  Restore verifies the indexed source under a held guard, copies it into owned
  sibling staging, and uses the same recoverable installation protocol as a
  download. Preserve the current target, including unscanned local edits;
  restore-as-copy never overwrites an occupied destination. Local restore
  intents have an explicit unsigned local-operation format, separate from
  signed remote records, and recovery dispatches both before scanning. On
  completion, durably account for retained files and mark prior local metadata
  for rescan while removing the intent. The normal scanner then generates a
  fresh local version/counter for RW access, or an unpublished local edit for
  RO access. Use the current local mtime and archived read-only/hidden flags;
  preserve the replacement target's DACL/creation time. Keep the source archive.
  Reused staging must not carry replacement metadata into a new destination.
  When no target remains, refresh its DACL/inheritance and creation time from
  an exclusively created sibling metadata reference with native delete-on-close;
  then apply the desired mtime/attributes. The reference contains no user data.
- **Ignore rules:** `.psp\ignore` uses gitignore-style patterns (`*`, `?`, `**`, `#`, a leading `/` anchors to the root), case-insensitive on Windows. A slash inside a pattern also anchors it; a trailing slash matches directories and their descendants. `**` as a whole component spans zero or more directories; trailing `/**` matches descendants, not the parent itself. Rules may use Windows backslashes as separators. `!` negates an earlier match, but children cannot be re-included while their parent remains excluded. Escape an initial `#` or `!` with a backslash for a literal name. Wildcards match Unicode scalar values. This is the stated glob subset, without bracket character classes. The defaults ignore `Thumbs.db`, `desktop.ini`, `~$*`, `*.tmp`, `$RECYCLE.BIN` and `System Volume Information`. Built-in path rules always exclude root `.psp`, temp suffixes and invalid paths. Like Resilio's IgnoreList, rules are local to each device.
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

The shared folder-key protector lives in Crypto so Storage can call it without
depending on the service host. Preserve fixed entropy `PeerSync folder key v1`.
DPAPI plaintext is a versioned binary frame containing role, FolderId and raw
key material; unprotect validates the expected folder/role and re-derives its
identity. Temporary plaintext is cleared. State database folder writes accept
key objects and always protect internally; listing folders exposes no cipher
or key material. Explicit key retrieval is audited. Machine-scope DPAPI does
not isolate users who can read the blob: the state-directory ACL is essential.
Folder settings keep the documented defaults, reject unknown/duplicate JSON
fields, and validate bounded host lists and ranges (rescan 1–86,400 seconds,
archive age 1–36,500 days, nonnegative archive size, volume cap 1–100%). Host
entries contain only DNS/IP and port, without credentials, paths or queries.

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

Diagnostic probes may append to a PID-named log folder after Windows reuses a
process ID. Verification must select the exact invocation using process lifetime,
role and run ID; historical events must neither satisfy nor fail its assertions.
Keep earlier logs for diagnosis and retain the existing timeout/progress checks.

App and service debug runs keep separate GUID directories under their respective
log roots. Shared startup/periodic/close retention removes only inactive owned run
directories: cap eligible inactive runs at eight and 100 MB and expire their
debug files after 24 hours. Active writers retain per-run rotation and are never
deleted to meet a quota. Unknown/unmarked content and inaccessible/linked/reparse
objects are preserved with a deferred/pressure diagnostic. Pin roots/ancestors
and owned run directories, use relative no-reparse handles, verify ownership
markers and remove exact guarded objects, never recursive pathname deletion.
Concurrent processes keep distinct journals and coordinate the shared sweep.
Close the journal before releasing its native active-writer lease; then sweep
the newly inactive run before releasing the root. Bound contention waits and
defer inaccessible work. Preserve unmarked legacy directories, since a filename
alone cannot establish their ownership or inactivity.

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
Engine diagnostics are best effort: unavailable/disposed logs or rejected
event formatting cannot turn a committed sync operation into a failure or
replace its original exception/cancellation. Keep strict Write for diagnostic
validation tools; engine paths use TryWrite and expose a dropped-event count.

Cross-machine diagnostic refinement (2026-10-18): retain managed HRESULT,
native socket error and QUIC transport/application error numbers as separate
metadata. Failed probes report the operation stage and transferred bytes;
the first completed data unit emits progress immediately, followed by bounded
five-second intervals while data advances. Stream setup can consume part of a
short authenticated transfer deadline, so initial progress must not require five
payload seconds. Collection must reject a bundle
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

The clock-status slice implements GetStatus plus status-bearing WatchEvents
before the remaining control operations. Frames carry a service-instance ID,
monotonic sequence, explicit production-sync availability, folder/device/session
identities, connection state, clock reason and optional signed offset ticks.
Bound each response to 1,000 clock rows and 256 KiB, signal truncation, and bound
concurrent event subscriptions. Slow consumers await transport backpressure;
cancellation releases subscriptions. The source is the runtime's actual
TransportFolder snapshots; an empty runtime must not invent peers or imply
production sync is available.

Only the server identity receives CreateNewInstance on the pipe; Administrators
and the installed PeerSync Users group receive client read/write rights. Inspect
the caller's identified Windows token for every RPC. The client verifies the
pipe owner against the configured service account and the kernel-reported
pipe-server PID against the running SCM service PID. Owner SID alone cannot
authenticate an extra server instance sharing the pipe descriptor. Reject an
identity mismatch before sending RPC data. Fixture endpoints may supply their
known process/SID directly; the production app resolves them through SCM and
exposes no arbitrary endpoint/owner override.

For a standard user's **Authorize this account** action, the elevated app
helper submits `AuthorizeUser` through the administrator-only RPC, then stops
and starts the Windows service through SCM and waits for RUNNING. This rebuilds
the pipe ACL and per-RPC caller policy from the durable SID list without a
sign-out. Recycling the control host inside the same Windows-service process
ended that process on a clean Server 2025 guest, so the RPC no longer requests
an in-process host restart.

The app initially queries status, then consumes WatchEvents and reconnects after
disconnect. Bind updates to the current client generation and service instance,
ignore stale sequences, and never preserve an old success after a connection
failure. Render clock reasons and numeric minutes from typed status, including
missing/invalid samples and disconnected failures, and clear warnings on fresh
accepted state. Test actual local named-pipe identity, stream lifecycle and
runtime snapshots, then render the real WPF controls and retain images for
inspection. Service installation/configuration, full production engine hosting
and the rest of the control API remain their required later tasks.
The initial WPF status view owns one cancellable reconnect loop and marshals
all model updates onto its dispatcher. Each attempt clears prior successful
rows and assigns a new client generation; its first valid snapshot binds the
service epoch, subsequent snapshots must keep that epoch and increase sequence.
Late results or errors from old attempts cannot change the active view. Stream
completion is a disconnect, including an empty clean completion. Treat 15 seconds
without a stream update as failure, cancel and drain the pending read before
disposing its enumerator. Retry failures
with a bounded delay and release the channel/stream on cancellation or close.
Present clock reasons, signed numeric minutes (peer UTC minus local UTC),
connected/disconnected peer state, truncation and explicit production availability.
Render and inspect actual WPF controls for skew, recovery and service failure;
internal test factories must not expose a production arbitrary endpoint switch.
App diagnostics correlate each connection attempt and record bounded counts,
RPC status, HRESULT and true native error codes. Inspect at most eight nested
causes, including gRPC's debug exception, without serializing raw error messages,
paths, peer IDs, account SIDs or keys. Wrapped pipe-identity failures must retain
an actionable identity reason in the UI and sanitized diagnostic cause metadata.

The named-pipe configuration follows the installed .NET 10/Grpc 2.84 packages
and [Microsoft's named-pipe guidance](https://learn.microsoft.com/en-us/aspnet/core/grpc/interprocess-namedpipes?view=aspnetcore-10.0),
including separate server-instance and client rights. Kernel PID verification
also protects the local client from additional-instance impersonation.

The shared client resolves only the installed `PeerSync` service and fixed
`PeerSync.Control` pipe. Query SCM with configuration/status read rights, require
Running and a nonzero PID, resolve its configured logon account to a SID, and
fail closed on missing/stopped/changing configuration. Bound native configuration
buffers to the documented 8 KiB and validate native strings within that buffer.
Each pipe connection uses identification-level impersonation, verifies owner
and kernel server PID before returning the stream to HTTP/2, and has a three-
second cancellable connection deadline. A service restart creates a fresh client
generation and SCM lookup. Raw fixture endpoints remain internal test seams.

The status server uses an explicit named-pipe-only host and rechecks caller
token membership in Administrators or the installer-created `PeerSync Users`
group for each RPC. Its status runtime registers actual TransportFolder objects
using their derived folder IDs, without transferring engine ownership. Empty
status-only hosting reports production availability false. Limit WatchEvents
to 32 subscriptions, emit at one-second intervals, await writes and release the
slot on cancellation/failure. Installation, service-account provisioning and
production folder/network supervision remain P2 work.

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
| `PeerSync.Control.Contracts` | Class library | `control.proto`, generated RPC stubs, typed status mapping and shared authenticated local-pipe client/SCM endpoint resolver; the UI/CLI control dependency, with no Engine or Storage dependency |
| `PeerSync.App` | WPF app | UI, tray icon, folder ACL grant |
| `PeerSync.Rendezvous` | ASP.NET Core | Tracker and relay, Windows service on port 99 |
| `PeerSync.Cli` | Console app | `psp.exe status, add, join, peers, dump-index`, probe for scripting and debugging |

Outside `src\`: `installer\` (Inno Setup script PeerSync.iss), and `tests\` with unit, multi-node integration, fuzz (`SharpFuzz`) and benchmark (`BenchmarkDotNet`) projects.

Core's signed-record envelope owns immutable copies of body/signature bytes.
Protocol's signing factory references Crypto and Core and handles generated
Protobuf records; Core has no Crypto/Protocol dependency. Path rules perform
pure lexical validation and root containment. Filesystem callers must also
reject reparse-point traversal when applying records; lexical containment
alone cannot stop a junction from leaving the root.

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

Directory-metadata tests that exercise rebase after receiving a root must
allow the worker's bounded source deadline to defer one attempt on a loaded
host. Use an injected clock to advance persisted backoff through bounded
retries, then require the signed graph to be complete before the local child
edit is rebased. Keep the production source deadline and graph checks intact;
the Server 2025 P3 PR failure and focused repair are recorded in
`docs/verification/P3-pr47-ci-repair-2026-10-22.md`.

The engine exchanges Hello, bounded index pages/range targets and immutable
blocks through `IPeerTransport`. The in-process fabric grants each registered
node its identity and creates a separate handler for each connection; request
payloads cannot choose their authenticated source. It injects directional
latency, deterministic drops and partitions on request and response delivery.
Requests run independently with a bounded outstanding limit, so an index wait
cannot block a chunk response. Disconnect cancels and drains requests before
disposing that connection's handler. This trusted simulation identity context
does not replace P2 TLS identity or folder authentication. Index pages that
advance beyond the previously announced target require a fresh sender high-water
announcement before commit; a bounded page cursor is not the sender's range end.
Disconnect invalidates an admission generation, so a fresh Hello cannot revive
queued work from an older session. Treat connection-local cancellation/disposal
as a retryable source failure while preserving caller cancellation and folder
shutdown, and make every concurrent disposal caller await the same drain task.
Clock admission is
checked by both engine endpoints, including immediately before index commits
and block/page handoff. Existing callback-based appliers remain local primitives;
the connected engine path routes remote bytes through the transport interface.

The first property harness uses three to seven actual NTFS roots, SQLite
indexes, scanners, signature validation, recoverable appliers and published
blocks. Reproducible generated cases edit, rename and delete files while links
partition, drop deliveries and heal. It records every locally published payload,
then checks byte-identical visible trees after healing and searches current,
archive and conflict bytes for every recorded version. Private block caches
alone do not count as preserved user versions. Save failing cases and operation
traces for direct replay and promotion to checked-in regression fixtures. Keep
bounded smoke sampling separate from the mandatory 1,000-case CI and two-day
gate evidence, and keep case/hierarchy/stale-temp work visible while the initial
flat-path scenario is expanded.
Add a separately versioned hierarchical scenario over the public coordinator:
nested/case/kind/rename/delete operations, recursive visible-tree and native
loss oracles, complete graph coverage and durable queue drain after healing.
Preserve flat seed replay; keep bounded smoke distinct from measured 1000-seed
CI/two-day/100000-file gates. Follow
`docs/proposals/P1-hierarchical-property-scenarios.md`.

Gate P1's thousand-seed/two-day evidence uses a bounded opt-in campaign,
separate from PR smoke and the local 100000-file corpus. Pin one green source,
split 1000 flat seeds across five Windows shards, add one hierarchy sample per
shard and run all six retained hierarchy regressions once per day. Verify
actual unique counts, complete native/queue/reference/loss oracles and two
consecutive UTC completion dates; do not claim 1000 hierarchical cases from
these explicit counts. Scheduling starts disabled and activation permits only
two attempts, with no automatic budget extension. Follow
`docs/proposals/P1-bounded-ci-campaign.md`.

On 2026-10-21 the user explicitly changes the required consecutive UTC dates
from five to two. This changes the calendar acceptance policy only: each day
still requires 1000 distinct flat seeds, five hierarchy samples, all retained
regressions, maintenance and every production oracle. Existing actual October
20/21 runs may qualify on their unchanged green source pin after independent
artifact and authoritative GitHub audits. A policy-only history-validator
update does not relabel or reset those tested binaries. Reduce the active
campaign ceiling to its second attempt and retire activation/scheduling after
acceptance. See `docs/requirements/P1-two-day-campaign.md`.

The user also explicitly approves the reporting-only exception for October
21 run 39582029908. Its planner and all five hosted test jobs pass on attempt
1 with complete source/binary/method/count/date evidence; only the final
reporter is denied by the Actions budget before runner allocation (ID 0,
no steps). Accept the report reconstructed by the exact pinned-source daily
verifier while preserving the failed overall workflow and its original
annotation. This exception does not cover failed tests, missing evidence,
reruns, mixed sources or an executed failing reporter. Ordinary merged
PR/main CI remains required for the tooling change. See
`docs/proposals/P1-two-day-report-budget-recovery.md`.

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

Run the complete required workflow on pull-request updates, pushes to `main`
and explicit manual dispatch. Do not also run it for feature-branch pushes:
the same update otherwise starts independent push and PR matrices, and
per-ref concurrency cannot cancel that duplication. Keep every required job,
the full fuzz duration, publishing and evidence. Combine related local work
before submitting a new head, and inspect the causal failure before retrying.
The required Server 2022 security job runs the complete native Engine suite.
Server 2025 runs every Engine case except the duplicate 1,001-conflict startup
capacity stress method; the full method remains required on Server 2022 and
in default local verification. All native case-copy recovery, collision,
attribute and cancellation tests still run on both Servers. Record this exact
partition and per-test/long-running timing output. The x64 packaging job runs the remaining unit suites,
simulation and native probes; avoid a third duplicate Engine execution there.
Keep all five required jobs, full 1,001-object boundary tests, strict durability
and 15-minute limits. The canonical local runner includes Engine by default;
an explicit CI partition switch records its omission in verification evidence.
Collect all required jobs before treating the partitioned workflow as passing.
After the immutable build, overlap each Server job's no-build Engine
run with its independent P0 and cross-account security probes. The latter publish
only Core/SpikeSupport probe outputs, not active Engine test output. Capture
stdout/stderr/TRX and both exit results, bound waits, and terminate the owned
test process on failure/cancellation. Keep all checks, 600-second fuzz and
15-minute limits. This avoids exhausting the budget on serial setup/probes
before native Engine tests start; do not blindly rerun timed-out old heads.
Native probe clients must wait for actual server readiness rather than fixed
startup sleeps. Bind a fresh readiness file to the server PID and retain strict
connection/RPC deadlines and identity checks. A deliberately delayed real pipe
server must reproduce the old race and pass the readiness-based path. Record
bounded sanitized RPC/native causes so a later failure is diagnosable.
Keep the complete 100,000-operation watcher stress check in the required x64
canonical job. Server security jobs omit that duplicate disk stress while
retaining all other native/cross-account
probes. Their evidence explicitly records the watcher omission. Running the
same stress concurrently with flush-heavy Engine fixtures exhausted its own
deadline on Server 2022; do not increase deadlines or drop workflow coverage.
After readiness/watcher fixes, Server 2025's Engine still exceeded its eleven
minute child deadline while every P0/security probe passed. A prior partial
Server 2025 TRX lacked the capacity method and fifteen later cases in that
class; the same full 720-case head passed on Server 2022. Do not repeatedly pay
to run this generic saturation boundary twice; preserve it as required once
and keep the per-operation/native recovery matrix on both Windows versions.
P0 diagnostic bundles use self-contained JIT publication for both architectures.
Do not spend each CI job compiling six temporary probes into ReadyToRun images:
those bundles exercise protocol/diagnostic behavior, and can use the included
.NET runtime on a test PC without installing the SDK. Keep locked restore,
all probe outputs, signatures and native runtime checks. Production service,
app and CLI installer packaging retains its ReadyToRun requirement below.

### Native Server verification critical path

Run 39411518907 reaches the Server 2025 Engine child's eleven-minute deadline
after 934 successful results, leaving 68 of its required 1002 cases unfinished.
Its P0/cross-account probes pass; the unchanged complete 1003-case scope passes
on Server 2022. Preserve this as incomplete CI, without retrying an unchanged
head, dropping more tests or increasing the fifteen-minute job ceiling.

Use an explicit native-verification solution containing Engine.Tests and every
P0 spike required by the Server runner, instead of building unrelated WPF,
packaging and other test projects before starting Engine. All required test
and spike outputs are built before the immutable no-build Engine child starts;
independent account-service publication may continue as before. Normal x64 /
ARM64 packaging and canonical verification retain their full build scopes.
Bound the complete Server verification script from before its build, reserving
setup/upload/cleanup headroom inside the unchanged job ceiling; derive the
Engine wait from that remaining shared budget rather than adding a fixed
child deadline after an unmeasured solution build. Preserve process cleanup,
stdout/stderr/TRX, all native probes and the existing single duplicate capacity
exclusion on Server 2025. Record build and remaining-wait timings in success
and failure evidence. Reproduce the hosted incomplete scope, measure the
unchanged local runner, validate the narrowed build closure and execute the
complete native runner locally before one batched CI update. Hosted acceptance
still requires all five jobs and actual Server 2022/2025 results.

## Packaging and deployment

One signed Inno Setup 7 installer per architecture (`PeerSync-Setup-<version>-x64.exe` and `-arm64.exe`) installs the service and the app for all users. It needs administrator rights once, and none for daily use after that. Inno Setup has no built-in service support, so the script drives `sc.exe` and `netsh` itself, while the app grants folder ACLs through the Windows security API; Setup stops the service before replacing files. Each self-contained publish has assemblies with overlapping filenames, so its files stay in a dedicated `{app}\service`, `{app}\app` or `{app}\cli` directory.

### What the installer does (Inno Setup)

1. **`[Setup]`:** a fixed `AppId` GUID (never changed, so a newer setup upgrades in place), `PrivilegesRequired=admin`, `MinVersion=10.0.20348` (Server 2022; Windows 11 is 10.0.22000, so Windows 10 is refused), `ArchitecturesAllowed` and `ArchitecturesInstallIn64BitMode` set to `x64compatible and not arm64`, or `arm64` for the Arm64 build. `CloseApplications=yes` lets Restart Manager close the tray app.
2. **`[Files]`:** self-contained, ReadyToRun builds of the service, app and CLI go to their dedicated subdirectories under `{app}` (`%ProgramFiles%\PeerSync\`). Only EXE/DLL payloads use `signonce`; non-PE files are copied without Authenticode signing.
3. **`PrepareToInstall` (Pascal Script):** install the bundled Microsoft-signed Visual C++ v14 Redistributable for the target architecture before replacing service files. NSec's native libsodium requires `VCRUNTIME140.dll` on clean Windows. If the prerequisite requests a reboot, stop Setup and rerun it after restart. Then, if the service exists, run `sc.exe stop` and poll until it reports STOPPED, for up to 30 s. If it won't stop, Setup shows an error instead of replacing files in use.
4. **`[Run]`, in order, hidden:**
   1. `sc.exe create` on first install only: start `auto`, account `NT SERVICE\PeerSync`. The installer switch `/SERVICEACCOUNT=LocalSystem` overrides it through `{param:ServiceAccount|...}`.
   2. `sc.exe failure`: restart after 60 s, 60 s, then 5 min.
   3. `net localgroup "PeerSync Users" /add`, then add the installing user (`{username}`) to it.
   4. `{app}\service\PeerSync.Service.exe --init` (elevated): creates `config`, `state` and `logs` under `%ProgramData%\PeerSync` with their ACLs (by SID, so it works in any language), creates the device certificate and lets the service account use its private key, and writes default settings. It skips anything already done, so upgrades run it again safely.
   5. `netsh advfirewall firewall add rule` for inbound traffic to `{app}\service\PeerSync.Service.exe`. A program rule covers the random listening port and LAN discovery in one rule.
   6. `sc.exe start PeerSync`, then optionally open the app (`postinstall`, skipped in silent mode).
5. **`[Dirs]`:** `{commonappdata}\PeerSync` with `uninsneveruninstall`, so settings and databases survive upgrades and uninstalls.
6. **`[Tasks]` and `[Registry]`:** an optional "Start the tray app when I sign in" task writes the HKLM `Run` value. Phase 6 adds the `peersync://` URL handler under `Software\Classes`.
7. **`[UninstallRun]`:** `sc.exe stop`, `sc.exe delete`, delete the firewall rule and the local group, each with a `RunOnceId`.

**Script outline** (abridged historical example; the authoritative, exit-code-checked script is `installer/PeerSync.iss`. `ISCC /DArch=arm64` switches the architecture and source folder):

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

### Local P1 scale evidence

The five-node 100000-file gate uses an explicit measured local corpus runner,
real native files/indexes and public connected engine transport, with final
recursive native-byte equality, durable queue drain, reference coverage and
overwritten-version preservation. Keep small preflights and normal property
smoke distinct from full corpus acceptance. No large default/scheduled CI job
is implied. Follow `docs/proposals/P1-scale-corpus-validation.md` and preserve
failed generated fixtures, progress metrics and exact source provenance.

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

### Metadata-only tombstone progress under directory publication

A staged directory mutation barrier must permit a scheduled authenticated newer
regular-file tombstone for an already published deleted local file only after
complete sender-range, case-barrier, bound-root and guarded native-absence proof.
Keep operation/reference gates and expected-sequence durable application; recheck
absence after preparation. Native occupants and live/unknown local rows retain
the barrier. This metadata-only progress can satisfy a child reconstruction's
causal proof without discarding queues or weakening directory graph/GC proofs.
See docs/proposals/P1-publication-tombstone-progress.md for the retained regression
and required controls.

### Connected production maintenance validation

Before Gate P1, simulation must exercise the real archive/history/reference/block
maintenance APIs around public connected transfers, including actual expired
archive removal and eligible owned-block eviction. Use injected peer clocks,
retain pending/current references and unknown objects, then prove delayed peer
transfer, native convergence and global preservation of every published version.
The delayed peer's fresh archive preserves the earlier version after the updated
peers' older archives expire; the native loss oracle remains unchanged.
See docs/proposals/P1-connected-maintenance-simulation.md. Host timer supervision
remains in its existing service phase; this test invokes the production worker.

### Campaign first-attempt allocation and evidence

The two-attempt campaign resource bound applies at each job allocation boundary,
including failed-job reruns that reuse successful planner outputs. Campaign plan,
shard and report jobs require github.run_attempt=1 before runner allocation.
Daily and standalone history validation independently require integer attempt 1.
Explicit manual preflight reruns remain supported and non-gating. See
 docs/proposals/P1-campaign-rerun-budget-repair.md for regression controls.

Hosted rerun validation uses a separate manual Linux-only allocation probe.
Its plan/shard/report allocation expressions must exactly match the campaign
workflow, with drift checked in normal CI. A successful synthetic planner emits
campaign or preflight outputs; one deliberate first-attempt shard failure permits
GitHub's failed-job rerun while reusing that planner. Attempt two must allocate
no campaign shard/report runners, whereas preflight must execute and succeed.
Retain authoritative per-attempt job/step metadata and cached planner identity.
These diagnostic runs never execute product tests, allocate Windows runners,
modify activation variables, consume campaign run numbers or count as days.

### Unchanged directory publication convergence

Main CI hierarchy seed 1608819303 exposes identical visible trees and empty
needed queues while directory publications continue advancing local sequences
on every healed round. Preserve the exact generated failure as a permanent
regression and inspect its signed metadata, original envelopes and publication
proofs. Repair the production cause without extending healing rounds, removing
pending-work/reference requirements, suppressing generated cases or weakening
native version survival. Require deterministic baseline reproduction, focused
authority/metadata controls, canonical local verification and green PR/main CI.
Existing live scale runs retain their immutable sources and remain independent
evidence; any repaired-source applicability must be assessed explicitly.

The retained signed records identify a directory-root feedback class split by
ChangedTicks even when path, mtime, attributes and author agree. Parent
root-set proof may group these causal timestamp variants only after validating
every authenticated original root and proving all its direct children covered.
Keep the selected publication's native metadata and signed authority intact.
Keep ModifiedBy in the class: independently preserved case copies retain their
original author boundaries. The direct same-body fallback stays strict. Version the proof-class digest so
persisted witnesses from the old classification must be rebuilt. Incomplete,
retiring, unavailable or changed-child graphs still withhold publication.

### Bounded CI simulation cohorts

The ordinary CI boundary job retains its five-job workflow, 50-minute ceiling,
600-second four-target fuzz and complete thirteen-method Simulation scope.
PR #42 exhausted that ceiling after eleven passing methods; the canceled seed
passes unchanged in a focused replay. Run the seven baseline methods and six
retained regressions as two owned concurrent processes against immutable build
outputs. Keep the default local/campaign Simulation runner unchanged. Charge
build and execution to one 45-minute script budget, reserve the remaining job
time for setup/evidence/cleanup, and terminate only owned process trees on error.

Use a unique evidence directory and separate cohort TRX directories. Accept
only the exact thirteen unique successful methods across both fresh reports,
with consistent complete counters and unchanged generated case, maintenance,
native/reference/queue/version oracles. A missing, duplicate, failed or partial
cohort is a failure. Preserve stdout/stderr and source/binary hashes. Local
controls/full execution and actual PR/main CI remain mandatory; this runner-only
change does not create a new thousand-seed campaign or waive any acceptance.
See docs/proposals/P1-bounded-ci-simulation-cohorts.md.

### Correctness-first implementation phasing

On 2026-10-21 the user explicitly approves: "Defer CI timing blocker; continue
implementation" after directing an end to further performance tuning. Permit
qualified P1 acceptance and P2 implementation using the completed local/native,
two-date campaign, full-corpus and unchanged-source hosted correctness evidence.
Ordinary CI's timeout remains visible verification debt, not a successful run.
This narrow exception permits the requirement/tooling merge and phase progress;
failed product assertions still require repair. Preserve all tests, native
durability/oracles and later service/LAN/NAT gates. Complete current-source
verification remains mandatory before release. See
docs/proposals/P1-correctness-first-implementation-phasing.md.

### Workspace NTFS Simulation fixtures

Implementation stopped on 2026-10-21 at the user's direction to stop further
performance tuning and complete the system. The storage hypothesis below was
documented but not implemented. Preserve it as historical investigation only;
it is not a correctness prerequisite or a claimed fix.

Run 39598885501 proves two cohorts alone do not meet the hosted budget: all
seven baseline methods pass, but five retained cases consume almost forty
minutes before the last case is stopped at the shared deadline. There is no
new failed product assertion. Preserve that failed result and all prior proof.

Keep unchanged Simulation sources/oracles and run each cohort with child-only
TMP/TEMP pointing to a unique workspace-owned NTFS directory. Record actual
original/selected paths, filesystem/volume information and small Flush(true)
measurements on both locations. Verify a fresh child resolves the selected
temporary path before executing tests. Neither a particular drive letter nor
a speedup is assumed: GitHub images/storage can change, and hosted timings
must establish the result. Keep FULL/native durability, all thirteen methods,
600-second fuzz, five jobs and existing time limits. The default campaign
runner and accepted dates remain unchanged. Never use a RAM disk, disable
flush/security or alter user/global temporary environment settings.
See docs/proposals/P1-workspace-ntfs-simulation-fixtures.md.

### Subtree deletion and preserved-copy convergence

PR #41 CI generates hierarchy seed 794669444: subtree deletion and later
preserved-copy edits leave different visible trees and repeated failed apply
items after all 24 healing rounds. Preserve the complete generated fixture,
signed originals and journals, reproduce on the submitted source, and retain
the seed permanently. Diagnose the precise apply/recovery failure before
choosing a repair. Keep healing bounds, case-source ownership, native identity,
authenticated metadata, reference/queue and global version-survival guards.
Do not merge a failed CI run or replace a failing generated case with a smoke.

A retained nonempty directory that is logically tombstoned is a new local
revival when RW scanning republishes its surviving children. Its published
directory mtime must reflect that revival and exceed the recorded deletion's
change time; reusing an older incidental native mtime lets concurrent replicas
reassert the old tombstone indefinitely. Preserve ordinary live-directory mtime
stability and file mtimes. Keep staged revival templates stable across repeated
walks, and retain all native/graph/ownership guards. An unrepresentable revival
time must preserve data and report an error rather than publish invalid ticks.
