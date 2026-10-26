# Clean Windows 11 VM boot attempt — 2026-10-26

Gate P3 still requires a complete installer upgrade, keep-data uninstall and
remove-data uninstall on a clean Windows 11 VM. This attempt does **not** pass
that gate; PeerSync setup has not run in this guest.

The disposable Hyper-V Generation 2 guest
`PeerSync-P3-Windows11-Eval-20261026` has Secure Boot enabled with the
Microsoft Windows template and virtual TPM enabled. Its Windows 11 Enterprise
Evaluation 26H2 image came from the official evaluation ISO with SHA-256
`BC3F24086EBADC94489066B5AD78089E2CF5C3491E90E790BB81A2B199C10E38`.
`DISM /Apply-Image /Compact /CheckIntegrity` exited 0 into a dynamic VHDX.
Offline inspection found `Windows\System32\winload.efi` present and
uncompressed, version `10.0.26100.9444`.

Hyper-V's `GetVirtualSystemThumbnailImage` instrumentation captured the exact
boot screens, without depending on a VMConnect session. A minimal manually
configured BCD reached Windows Boot Manager, which reported
`\Windows\System32\winload.efi` with status `0xc000000e`:
[minimal BCD boot screen](P3-win11-vm-bcd-loader-2026-10-26.png). A distinct
EFI VHDX built from the image's valid `BCD-Template`, with the OS partition
and EFI loader explicitly set, advanced to the blue recovery screen but
reported `0xc0000001`:
[template BCD boot screen](P3-win11-vm-template-recovery-2026-10-26.png).
The offline `Windows\Panther` directory had no new setup log. The VM returned
to Off and its virtual disks were detached from the host after inspection.
The original boot VHDX files remain available for recovery; the current VM
configuration uses only `system.vhdx` and `boot-template.vhdx`.

The next attempt must boot through normal Windows Setup or repair the guest
boot chain from guest-side WinPE, then prove the clean install and required
PeerSync lifecycle. Neither this offline image application nor a physical
Windows 11 client substitutes for the clean VM gate.
