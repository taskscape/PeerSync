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
