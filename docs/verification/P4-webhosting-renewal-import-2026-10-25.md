# P4 win-acme WebHosting renewal fallback, 2026-10-25

The physical server's first installation exposed a win-acme certificate in
`LocalMachine\WebHosting` with a working machine private key, while Rendezvous
requires that certificate in `LocalMachine\My`. A public `.cer` copy lost the
key association. Copying the Windows certificate context with `X509Store.Add`
made the existing server certificate usable in `My`, but a later win-acme
renewal could repeat the mismatch.

`Import-RendezvousWebHostingCertificate.ps1` now selects a current certificate
with the configured DNS name, Server Authentication EKU, a usable machine
private key and a valid sign/verify challenge. It compares the usable
`WebHosting` and `My` candidates by expiry and copies only a newer
`WebHosting` certificate context into `My`. It rechecks the imported key with
the existing grant preflight. No private key is exported. Setup invokes this
helper before its certificate check and before stopping an older service; the
win-acme post-renewal hook invokes it before granting the service key ACL and
restarting the service. The service loader still reads `LocalMachine\My`, as
required by the plan. An absent or unusable `WebHosting` candidate leaves the
existing `My` path to the original preflight.

All three changed PowerShell scripts parsed with zero errors. The helper's
absent-store branch ran under Windows PowerShell 5.1 with exit 0 and no output
on the development host. Inno Setup 7.1 compiled the changed installer,
including the new helper in both installed and renewal-hook locations, to an
**unsigned validation output** under
`artifacts/installer-validation-webhosting/`. That output contains the older
staged 1.0.25.0 service and must not be deployed. The existing signed
installer outputs were not overwritten.

The development host initially had no `LocalMachine\WebHosting` store. The
first native control correctly failed at the missing store and cleaned up its
unique test certificate. An empty standard `WebHosting` store was then created
through `X509Store.Open(ReadWrite)`. The Windows PowerShell 5.1 native control
passed: it moved a unique one-day, nonexportable server-authentication
certificate into `WebHosting`, imported its context into `My`, verified the
matching machine key through the grant preflight, and confirmed that a second
import was idempotent. `WEBHOSTING_IMPORT_PASSED` was printed. Follow-up reads
found zero test certificates in either store and zero certificates in the
`WebHosting` store. The test's exact certificate and key were removed.

An attempt to remove the now-empty test-created `WebHosting` store was rejected
by automatic approval review (`blocked by policy`). Its root and empty
`Certificates`, `CRLs` and `CTLs` subkeys remain on the development host; no
alternate deletion path was attempted. This native check covers Windows store
copy and key association, not the physical server's win-acme renewal. The
current-source signed setup and the configured post-renewal hook still need
installed validation on a Windows Server before claiming this repair deployed.
The native control remains in `build/Test-RendezvousWebHostingImport.ps1` for
reproduction with a unique `.invalid` certificate. Do not substitute the
local compile or synthetic certificate for a real renewal.
