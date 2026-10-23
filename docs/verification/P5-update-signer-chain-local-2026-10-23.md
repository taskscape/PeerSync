# P5.7 update signer chain check, local evidence, 2026-10-23

The SYSTEM update helper previously accepted a `Get-AuthenticodeSignature`
`Valid` result and an exact administrator pin, without explicitly requesting
online revocation for the publisher certificate chain. It now invokes
`installer/Assert-UpdateSetupSignature.ps1` on its protected copy before
starting Setup. That check requires the PE Authenticode signature, exact
publisher-certificate SHA-256 pin, current code-signing EKU chain, online
revocation for every non-root certificate, no ignored chain errors and a
20-second URL retrieval limit. An unavailable or negative chain result stops
installation and leaves the pending package for diagnosis.

Microsoft documents [online X.509 chain validation](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.x509certificates.x509chainpolicy.revocationmode),
[the chain-wide result](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.x509certificates.x509chain),
and [the retrieval timeout](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.x509certificates.x509chainpolicy.urlretrievaltimeout).

Local Windows PowerShell 5.1 checks used the already trusted,
development-signed `artifacts/installer/PeerSync-Setup-1.0.11.0-x64.exe`
(SHA-256 `CF78612E11EA3D8FE38B8DA7FF3F3B1BD28A54A3ECAD1B24FB0C1AE09FBBCA2A`).
Its Authenticode status was `Valid`; the explicit code-signing chain build
returned true. The new script accepted that setup with its signer pin and
rejected the same setup with a different 64-hex pin. Its PowerShell parser
reported no syntax errors. The currently trusted self-signed development
signer is a root, so this positive check cannot exercise non-root revocation.

The same x64 published payload compiled into an unsigned development Inno
installer with the new helper file entry and `--no-compression`; compiler
exit code was zero and the output was 591,285,050 bytes. The unsigned output
is not an install or release test. `build/Run-InstallerCi.ps1` now invokes
the verifier on each freshly signed development installer while its temporary
trust is installed, and checks wrong-pin rejection on x64. Hosted CI did not
run because of the Actions budget; the new script was not installed in the
disposable VM.

A release-CA certificate with an actual revoked serial, an unavailable CRL or
OCSP endpoint, and a separate trusted signer still require installed-host
negative checks. The release signer and public feed are not provisioned, so
P5's release gate remains open.
