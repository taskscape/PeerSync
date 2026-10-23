# Signed update restart recovery, 2026-10-23

The SYSTEM helper retained a `.ready` slot when Setup returned 3010 (restart
required). On its next daily run the existing `--verify-update` preflight
rejected the now-current version, leaving the slot in place. The service
deliberately refuses to download another offer while any ready slot exists, so
this was a permanent update stall after an otherwise successful reboot.

`--classify-update` now validates the administrator's publisher pin, signed
manifest, expiry, setup length and SHA-256 before returning only `CURRENT` or
`NEWER`. The helper uses that result before copying or launching Setup. A
current package is retired only after the PeerSync service reaches Running,
the installed service file version meets the signed offer and the scheduled
task still runs as SYSTEM from the installed helper. It rechecks the exact
GUID-named non-reparse slot under the pending root immediately before removal.
An invalid, tampered, expired or otherwise unhealthy package remains for
administrator diagnosis. Newer packages still follow the original protected
copy, Authenticode/chain and silent-Setup path.

Focused local Release test: `UpdateManifestVerifierTests` passed 18/18,
including current/newer classification, the existing strict newer-only
preflight, and a tampered setup. Windows PowerShell 5.1 parsed the updated
helper without errors; `git diff --check` passed. This does not exercise a real
3010 installer, reboot or SYSTEM task. Those installed-host checks remain open.
