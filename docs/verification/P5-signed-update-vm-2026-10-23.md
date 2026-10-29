# P5.7 signed update: disposable Server 2025 VM, 2026-10-23

This evidence is for the implementation path, not a release signer or public
feed. The guest was PeerSync-P3-Server2025-Clean, initially running signed
development build 1.0.6.0. A pre-p5-updater-20261023 Hyper-V snapshot
preserves that baseline. The development publisher certificate was imported
into the guest's LocalMachine Root and TrustedPublisher stores so that
Authenticode could validate it. No private key was copied into the VM.

## Installer recovery and task registration

The first 1.0.7.0 silent upgrade copied binaries but left the service stopped
and had no update task. The setup log traced it to PowerShell 5.1 rejecting a
direct SwitchParameter-to-Int32 cast in Update-Helper.ps1. The Inno process
returned exit code 0 despite its ssPostInstall exception. After fixing the
cast, a rebuilt development-signed 1.0.7.0 setup returned 0, left PeerSync
Running, installed service version 1.0.7.0, and registered the
PeerSyncUpdateInstall daily task as SYSTEM with the installed helper path.

An isolated Inno Setup 7 probe confirmed that GetCustomSetupExitCode is
called after a suppressed ssPostInstall exception and can return 9. The
production script now returns 9 unless all post-install actions complete.

## Scheduled helper

New-UpdateManifest.ps1 created an RSA-PSS/SHA-256 detached manifest for a
development-signed 1.0.8.0 installer. The installed administrative policy
script pinned the same public certificate and enabled a local test URL.
The 1.0.7.0 service --verify-update command accepted the exact staged
manifest and setup.

The update was manually staged in the VM rather than downloaded from a live
HTTPS feed; this specifically exercised the scheduled privileged handoff.
Changing one setup byte caused the SYSTEM task to return 1. The 1.0.7.0
service remained Running, and the failed .ready directory remained for
diagnosis. After restoring the exact signed package, the SYSTEM task
returned 0, installed 1.0.8.0, left the service Running, and removed the
verified pending and trusted-copy directories.

With the policy disabled and a dummy .ready directory present, the updated
helper returned 0, kept 1.0.8.0 Running and left the pending directory
untouched. The old installed helper was temporarily replaced in this
disposable VM to check this disabled-policy behavior before final packaging.
The policy and trusted-copy directories had protected ACLs: SYSTEM and
Administrators held FullControl, the service SID had ReadAndExecute only on
the policy directory, and the service SID had no trusted-copy access.

A final 1.0.9.0 development-signed package contained the exit-code fix and
post-install version/task checks. From installed 1.0.8.0, the scheduled
SYSTEM task installed the signed 1.0.9.0 offer, returned 0, left PeerSync
Running, removed the verified ready slot, and retained its SYSTEM principal.
The installed helper SHA-256 matched the final source file.
The Release Service test project passed 54/54 tests after rebuilding,
including manifest, policy, downloader and preflight cases. The installed
HTTPS-feed test follows below.

## Installed HTTPS feed

The VM hosted an HTTPS feed bound only to localhost:9443 using a short-lived
test TLS certificate trusted by the guest. The reproducible server script is
[ServeUpdateFeed.ps1](../../spikes/ServeUpdateFeed.ps1). It served the exact
development-signed 1.0.10.0 setup and its RSA-PSS manifest. After PeerSync restarted,
the installed service requested the manifest and setup, staged one .ready
directory, and emitted a downloaded setup whose SHA-256 matched the signed
source. The installed --verify-update preflight returned 0. The existing
SYSTEM task then returned 0, installed service version 1.0.10.0, left the
service Running, and removed both the ready and trusted-copy directories.
This tests the real service HTTP handler and updater, not a fake response.

After that upgrade, the feed still advertised the now-current version.
The downloader was changed to treat an authenticated current-version offer
as a quiet no-op before downloading the setup. Downgrades remain invalid;
the rebuilt Release Service suite passed 55/55 with a one-request assertion
for this case. A development-signed 1.0.11.0 package was then offered through
the same installed HTTPS path. The service staged it, and the SYSTEM helper
installed it with task result 0, leaving PeerSync Running and no pending
ready slot. A service restart with that unchanged feed produced exactly one
new GET for /manifest.json, no setup GET, no ready slot and an informational
service.update_no_new_offer event.

After validation, the administrative update policy was disabled, the
temporary feed process stopped, both localhost:9443 TLS bindings removed,
and the test TLS certificate removed from the guest Root and My stores.
PeerSync remained Running at 1.0.11.0. The publisher's public development
certificate remained in the separate update policy and development trust
stores used for Authenticode validation.

## Still open

- A public distribution URL and release signer have not been provisioned;
  the completed installed HTTPS test used localhost and development trust.
- The negative nonzero-exit path was checked with an isolated Inno Setup
  probe, not by deliberately breaking the final signed installer in the VM.
- A revoked certificate, unrelated trusted signer and certificate rotation
  require more installed-host testing before release.
- Physical two-PC, NAT, performance and soak gates remain open in plan.md.
