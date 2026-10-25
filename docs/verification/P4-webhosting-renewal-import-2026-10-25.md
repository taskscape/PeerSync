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

The development host has no `LocalMachine\WebHosting` store. A native control
created a unique one-day self-signed certificate in `My` but could not move it
to the absent store; its `finally` cleanup removed the test certificate, and
a follow-up store inspection found zero matching test certificates. The
native import/renewal path therefore remains **unverified**. Run
`build/Test-RendezvousWebHostingImport.ps1` on a disposable Windows Server
with an existing `WebHosting` store, then test a current-source signed setup
and the configured win-acme post-renewal hook on the physical server before
claiming this repair deployed. The test script uses a unique `.invalid` name,
checks import and idempotence, and removes only its exact test certificate and
key. Do not substitute this local compile for a real renewal.
