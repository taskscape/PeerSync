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
