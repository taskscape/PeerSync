# Fresh physical Windows 11 setup prerequisite — 2026-10-26

The development-signed `PeerSync-Setup-1.0.28.0-x64.exe` was launched on the
fresh physical Windows 11 PC `LARKBOX-X-1` through the field upgrade runner.
The package ZIP SHA-256 was
`C676C539105A9C001B690B88A0E459353244DE8D8C048A7218ACA2D2FB5C0182`;
the setup SHA-256 was
`9B763AB40D19B7602EB6EEDA1E1C012AFC92806CF282CC264F14D09ADB3AB853`.
The runner verified the development signer and trusted its public certificate
on that test PC. This is not a public release signature.

The first setup stopped before PeerSync was installed. Its Inno Setup log
records `vc_redist.exe /install /quiet /norestart` returning `3010`, followed
by `PrepareToInstall failed: The Microsoft Visual C++ runtime requires a
restart` and `Need to restart Windows? Yes`. Setup exited `8`. The separate
field collector confirmed that both the PeerSync service and installed
service executable were absent after the failed setup. The operator must
restart this PC and rerun the same verified package; a successful install
and subsequent sync have not yet been observed.

The upgrade runner now treats setup exit codes `8` and `3010` as a
`restartRequired` result with a concrete rerun instruction. The original
1.0.28.0 ZIP does not include that later runner text; this change applies to
future field kits.

This physical-PC prerequisite finding supplements the clean Windows 11 **VM**
lifecycle required by Gate P3. It does not close that gate.
