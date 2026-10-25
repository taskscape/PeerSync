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

A later physical first-PC upgrade [verified forced dashboard closure](P3-open-dashboard-upgrade-repair-2026-10-24.md),
so the current third-PC kit is now
`artifacts/deploy/PeerSync-OutsidePC-1.0.23.0-x64-upgrade.zip` (166,410,038
bytes, SHA-256
`51CA6DCC25B059CC579AEE97CCECCFFA555987045E701782906D2603AA7E9A24`).
Its embedded development-signed 1.0.23.0 setup has SHA-256
`A3CAF857296B42175A455ADB46F51651B8CFF9C6DB738BD318C6E32FFCA1CCC0`,
matching the standalone signed installer; the ZIP contains exactly six named
entries and no private key or folder capability. The reproducible builder is
`build/field/Build-OutsidePcKit.ps1`, and the collector now says explicitly to
transfer the output ZIP independently when folder sync is stalled. The sender
used Tailscale Taildrop to send this exact new kit to the third PC; the command
exited 0. Recipient-side hash, installation, actual socket diagnosis and file
convergence are still unverified. Tailscale remains an overlay path; a native
NAT result requires disabling its peer route during that separate test.

The third PC later installed 1.0.23.0, submitted a sanitized diagnostic
bundle and participated in the [five-minute native mobile-hotspot relay
check](P4-native-wan-overlay-isolation-2026-10-24.md). For the next field
upgrade, `artifacts/deploy/PeerSync-OutsidePC-1.0.25.0-x64-upgrade.zip`
(166,373,276 bytes, SHA-256
`3E2A40ADA8E2BB8D343F9C01FCBC3AD5785EB3D06129161FA69480D91438DA52`)
contains the signed 1.0.25.0 client installer, its public code-signing
certificate and manifest, the collector, probe and README. The builder
verified the installer hash and Valid development signature. Tailscale
Taildrop sent this ZIP to the third PC and exited 0 on 2026-10-25. The
recipient's 1.0.25.0 installation and fresh three-PC convergence remain
unverified; the earlier native WAN result used installed 1.0.23.0.

On 2026-10-25 the newer
`artifacts/deploy/PeerSync-OutsidePC-1.0.26.0-x64-upgrade.zip` was built from
the signed client installer at source revision `0153358b5b41f8bb58bcd8334d65b616a839f51f`.
The six-entry kit is 166,396,000 bytes with SHA-256
`DA2973BA478EAFAA25A4957D855C7AE68785FEC3971382125E934C48E6C6ACBB`.
Its embedded installer hashes exactly to the standalone signed setup's
`BBB1C5C06F7ECB6D99140571C852E5EF9407F522CEB14FFE9514615CB0EF6041`;
the builder verified its manifest, public certificate and Valid development
signature. It contains no folder key or private key. Tailscale identified the
online third-PC target by the same observed mobile public address used in the
hotspot probe; `tailscale file cp` to that target exited 0. This is sender-side
delivery only. The two LAN PCs already run this version and passed
[fresh Add/Join in both directions](P3-fresh-add-join-physical-2026-10-25.md).

The third-PC operator reported running the extracted 1.0.26.0 setup and then
ran this installed-file and SCM check on that PC on 2026-10-25:

```powershell
(Get-Item 'C:\Program Files\PeerSync\service\PeerSync.Service.exe').VersionInfo.FileVersion; (Get-Service PeerSync).Status
# 1.0.26.0
# Running
```

This verifies the installed service executable's reported version and Running
state on the third PC. The recipient ZIP hash, installed Authenticode status,
rendered notification behavior, fresh three-PC convergence and native WAN
retest on 1.0.26.0 remain unverified. The earlier overlay-isolated mobile-WAN
transfer used installed 1.0.23.0.
