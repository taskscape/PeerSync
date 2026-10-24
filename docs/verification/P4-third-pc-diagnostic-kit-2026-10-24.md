# Third-PC upgrade and diagnostic kit, 2026-10-24

The outside-PC kit builder produced
`artifacts/deploy/PeerSync-OutsidePC-1.0.19.0-x64-upgrade.zip` (166,806,013 bytes,
SHA-256 `132A077A7B29A8F2A1B398A932419413E663361377FDCAD5A8894983C6618461`).
It contains the development-signed client installer, its public signer
certificate and manifest, a diagnostic collector, the read-only outside-network
probe and an operator README. It contains no private key, folder key or server
installer. The ZIP's embedded installer hash matches the original signed
1.0.19.0 installer, and the probe parsed without errors. The README gives the
shorter upgrade path and collector step for an already joined PC.

This is a direct-transfer kit for the third PC. After installing 1.0.19.0,
double-clicking `Collect-PeerSync-Diagnostics.cmd` in the extracted folder
creates a sanitized `PeerSync-diagnostics-*.zip` beside it. The diagnostic ZIP
can then be returned by a trusted channel independent of PeerSync. The helper
was previously exercised by a local double-click-equivalent smoke; the 1.0.19.0
installed CLI separately collected a 100,000-event live bundle from the first
PC. The third PC has not yet run this kit or supplied its bundle. Its earlier
`SocketException` cannot be assigned a cause from the notification alone.

An updated direct-transfer kit is now available as
`artifacts/deploy/PeerSync-OutsidePC-1.0.21.0-x64-upgrade.zip` (166,890,230
bytes, SHA-256 `F038F63660A5AF1422409D48305B7A45F39531A36E099124221348EA78C32A3A`).
It contains the 1.0.21.0 development-signed client setup, matching public
signing certificate and manifest, the same diagnostic collector, the probe
and an updated README. The ZIP has six entries; the embedded setup's SHA-256
matches the signed standalone installer. It contains no private key or folder
capability. The [first-PC 1.0.21.0 field check](P4-tracker-session-correlation-2026-10-24.md)
observed one tracker stream through two refreshes and 11 minutes without the
old 319-second reset. That is reason to use this newer kit for the next third-PC
upgrade; it does not prove the third PC's socket failure is resolved. The third
PC has not yet installed 1.0.21.0 or returned its sanitized bundle.

The latest direct-transfer kit is
`artifacts/deploy/PeerSync-OutsidePC-1.0.22.0-x64-upgrade-exitapp.zip`
(166,834,855 bytes, SHA-256
`958664E87ABEA62F1C3B2B854C9BD1827252DB6EE143233186D6F13C6A2134B3`).
Its embedded 1.0.22.0 installer matches the signed standalone setup's SHA-256
`461489100474B6A58E1D6A3C0542CFBDC56A4B91217CB0460EA6CD14F24CC5F1`.
The README now explicitly instructs the operator to choose `Exit app` from
PeerSync's tray menu before setup: an open dashboard process blocked the
second-PC upgrade. This kit also includes the new long-session retry-accounting
fix. It remains a development-signed test package. The third PC has not yet
installed it, supplied a diagnostic bundle, or demonstrated joined-file
convergence.

The third PC's Tailscale node was online and offered as a Taildrop target.
`tailscale file cp` sent the exact 1.0.22.0 upgrade ZIP above to that node and
exited 0 on 2026-10-24. This is an independent package-delivery path, not a
PeerSync file-convergence result. The sender cannot read the third PC's
Downloads folder with its current Windows credentials, so recipient-side hash,
installation and diagnostic collection still require field confirmation.
