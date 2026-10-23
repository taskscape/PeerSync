# Signed update restart recovery, 2026-10-23

The SYSTEM helper retained a `.ready` slot when Setup returned 3010 (restart
required). On its next daily run the existing `--verify-update` preflight
rejected the now-current version, leaving the slot in place. The service
deliberately refuses to download another offer while any ready slot exists, so
this was a permanent update stall after an otherwise successful reboot.

`--classify-update` now validates the administrator's publisher pin, signed
manifest, expiry, setup length and SHA-256 before returning only `CURRENT` or
`NEWER`. The helper uses that result before copying or launching Setup. A
current package is retired only after a protected SYSTEM-owned receipt records
Setup exit 3010 and proves that Windows booted after Setup started, the PeerSync service reaches
Running, the installed service file version meets the signed offer and the
scheduled task still runs as SYSTEM from the installed helper. It rechecks the exact
GUID-named non-reparse slot under the pending root immediately before removal.
The service-writable slot and protected copy are cleaned without recursive
deletion: only their expected ordinary files are removed, and any unexpected
child leaves the directory for administrator inspection.
An invalid, tampered, expired or otherwise unhealthy package remains for
administrator diagnosis. A missing attempt receipt also requires
administrator disposition. Newer packages still follow the original protected
copy, Authenticode/chain and silent-Setup path.

Focused local Release test: `UpdateManifestVerifierTests` passed 18/18,
including current/newer classification, the existing strict newer-only
preflight, and a tampered setup. `build/Test-UpdateRestartRecovery.ps1` passed
under Windows PowerShell 5.1 using the real boot timestamp: a protected
attempt dated after boot was rejected, an unfinished attempt was rejected, one
dated before boot with a recorded 3010 outcome was accepted,
and a damaged unrelated receipt did not block it. A subsequent unrun control
case has been added for unexpected nested children and nonrecursive cleanup;
its result belongs to the consolidated verification pass. The helper parsed
without errors and `git diff --check` passed for the earlier change. These controls do not exercise a real
3010 installer, reboot or SYSTEM task. Those installed-host checks remain open.
