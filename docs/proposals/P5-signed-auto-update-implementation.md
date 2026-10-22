# P5.7 signed auto-update implementation design

This is an implementation design for P5.7, not an exception to any release
gate. The release signing identity and distribution URL do not yet exist, so
automatic installation must remain disabled until an administrator pins the
intended publisher and enables it. The existing locally trusted development
certificate can exercise the path in a disposable VM only.

## Trust boundary

The installed service runs as `NT SERVICE\PeerSync` and can modify its normal
`config` and state directories. It must not be allowed to choose the update
signer or turn updates on by itself. Put `update-policy.json` in a separate
ProgramData directory whose ACL grants SYSTEM/Administrators full control
and the PeerSync service SID read only. The dashboard's `UpdateSettings` RPC
does not alter this policy. It contains `enabled`, an HTTPS manifest URL and
the SHA-256 digest of the publisher certificate. Missing or invalid policy
means updates are off, with a safe diagnostic event. Administrator changes
require an elevated operation and an explicit restart/reload.

The manifest carries a detached RSA-PSS/SHA-256 signature over the exact
UTF-8 bytes of a small JSON payload. The verifier uses the administrator-
pinned publisher certificate's public key and checks that certificate's
SHA-256 digest before parsing fields. The payload includes a
strict version, HTTPS setup URL, setup SHA-256 and maximum allowed size. A
valid manifest cannot downgrade or reinstall the current version. The
service checks at startup and at most once per day, uses bounded HTTP
timeouts and response sizes, and downloads to a new file under an
update-pending directory with only the service/SYSTEM/admin ACL. It verifies
the hash and writes a durable pending record. No URL, path, key or manifest
body enters ordinary diagnostics.

## Privileged handoff

The virtual service account must not gain administrator rights merely to
run Setup. The client installer creates a daily Task Scheduler action that
runs a short-lived updater helper as SYSTEM from its protected Program Files
location. The helper treats the service-writable pending package and record
as hostile input: it rechecks the signed manifest, publisher pin, size/hash,
version and Authenticode trust; copies the setup into a SYSTEM-only staging
directory; verifies that copy again; then runs
`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART` and records the exit code. Setup
stops the service itself. The helper may remove only its own verified staging
files, and never removes a failed pending package before diagnostics can be
collected. Disable/removal of the scheduled task is part of uninstall.

The helper must reject a valid signature from a different trusted publisher,
a revoked/untrusted chain, modified PE bytes, stale versions, path traversal,
redirects to non-HTTPS URLs and a package changed between validation and
execution. The signer pin lives outside service-writable storage. No
`schtasks /run` right is granted to the service; the daily SYSTEM task reads
pending work on its own schedule.

Microsoft references:
[RSA-PSS verification](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.rsa.verifydata),
[WinVerifyTrust Authenticode policy](https://learn.microsoft.com/en-us/windows/win32/api/wintrust/nf-wintrust-winverifytrust),
[SYSTEM scheduled tasks](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/schtasks-create).

## Verification before enabling

Unit tests cover malformed, oversized, unsigned, wrong-signer and modified
manifests and version ordering. Installer tests inspect the task principal,
protected paths and uninstall behavior. A disposable installed VM uses a
development-signed setup to prove download, helper verification, silent
upgrade, service restart and a no-op when disabled. A tampered setup and a
different trusted development signer must both be rejected in the VM. A
publicly trusted release signer, its rotation process and the final HTTPS
distribution endpoint remain release prerequisites.
