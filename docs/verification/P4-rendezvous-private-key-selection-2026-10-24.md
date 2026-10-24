# Rendezvous private-key selection, 2026-10-24

During physical server setup, a certificate copied to `LocalMachine\My`
reported `HasPrivateKey=True` but `GetRSAPrivateKey` failed with Windows
`NTE_BAD_KEYSET` (`0x80090016`). Metadata alone could therefore select a
certificate that Kestrel could not use. The installer grant helper now checks
each current matching machine certificate in expiry order, requires its key
file in the machine store, signs a challenge, and verifies it with the public
key. Runtime selection independently signs and verifies before choosing the
certificate. A stale or inaccessible candidate falls through to a usable
matching certificate. The deployed preflight adds
`usableMachinePrivateKeyForCaller` and `server-certificate-key-unusable` without
including the key file path in its JSON.

Release `PeerSync.Rendezvous.Tests` passed 24/24 with no skips. The tests cover
RSA fallback from a newer public-only candidate and ECDSA name matching.
Both installer scripts parsed under Windows PowerShell 5.1. The grant helper's
read-only `-CheckOnly` path selected a present localhost Server Authentication
machine certificate and signed and verified successfully. These local checks
do not prove the physical server has installed the revised helper or that its
service account can open the Let's Encrypt key after the next real renewal.

The x64 Rendezvous 1.0.24.0 development installer was built from commit
`628d8e7ed011bf237e99560c3d7c11584d812969`. Inno Setup 7.1 compiled
successfully. Authenticode reports Valid for the setup, and its SHA-256
`632CE35C4171A4075A99B899FFF46DE110B7C4F25BB3687AAC0A230D12EF5419`
matches the manifest; the setup is 66,257,048 bytes. The manifest records a
dirty worktree solely because the pre-existing untracked `Sync.zip.tmp` remains
outside this change. The signed setup was sent to the server node `i9-win-iis`
through Tailscale Taildrop, which exited 0. The recipient's installation and
post-upgrade local preflight remain unverified. This development signature is
not a public release signature.
