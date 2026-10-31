# Clean Windows 11 interactive uninstall, 2026-10-26

On the disposable `PeerSync-P3-Windows11-Eval-20261026` VM, the
development-signed PeerSync 1.0.28.0 client was installed after the earlier
[clean-guest lifecycle](P3-clean-windows11-lifecycle-2026-10-26.md). A real
console-session interactive Inno uninstaller was driven through its visible
dialogs. The ignored retained records are under
`artifacts/vm/windows11-eval-20261026/interactive-uninstall/`, including a
rendered prompt image, UI Automation tree, per-button action records, Inno
logs and independent final-state JSON. The guest was shut down afterward.

UI Automation read the prompt text directly from the `PeerSync Uninstall`
dialog: “Also remove PeerSync settings, databases, logs and the device
certificate? Your synced folders and their files are not touched.” The Yes
and No button window styles identified **No as the default**; the rendered
prompt was also visually inspected. Both choices were clicked in separate
interactive uninstall flows, and both completed without a restart.

| Choice | Installed action and final guest state |
| --- | --- |
| Yes, remove data | Inno log ran `PeerSync.Service.exe --remove-data`. Final state: uninstaller and service absent, installation and ProgramData absent, zero device certificates, and `PeerSync Users` group absent. Log reports success and `Need to restart Windows? No`. |
| No, keep data | Inno log has no `--remove-data`. Service and installation absent, ProgramData retained with ten files, and the 256-byte native sentinel retained SHA-256 `40AFF2E9D2D8922E47AFD4648E6967497158785FBD1DA870E7110266BF944880`. Log reports success and no restart. |

The retained remove-flow and keep-flow logs have SHA-256
`D4CD3F9C8B561B3C1A99D227F135E2C1B56D2F6F091D19E8BC1F1758E3E035D5`
and `69837C33C0386D5CB68A80C461436B24FE223DA129E579DCC48A040C656F7616`,
respectively. The guest's final-state files were read independently after
each uninstall; no conclusion relies on the click action alone.

This passes the clean Windows 11 interactive remove/keep-data portion of
P3.3.3. The clean Server 2022 **interactive** prompt and app-only unassisted
physical tester flow remain open, so Gate P3 is not complete. The work used
a development-signed package, not a public release signature.
