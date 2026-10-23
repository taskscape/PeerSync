# Installed folder startup recovery order, 2026-10-23

The installed host has a watcher/periodic scanner separate from its transport
pull coordinator. Previously it exposed each folder to that scanner before the
coordinator's first pass. A prepared file-apply intent therefore made the
scanner reject the walk, and a folder without an authenticated peer could
remain in scanner retries without ever invoking the coordinator's recovery.

Folder construction now drains root-bound staging retirements and file/directory
apply intents before status registration, network exposure or watcher startup.
The first transport pass repeats the recovery check, covering work that may
arrive between construction and that pass. Failed recovery prevents the folder
from being exposed; no scan or peer mutation is allowed to bypass it. Debug
events report the count recovered or a sanitized failure category.

An installed-host regression creates a prepared native file-apply intent after
moving the original into its archive. It then starts the host without a peer,
checks that the verified remote bytes are recovered, that the intent is gone,
and that the host's first successful scan follows the recovery event with no
scanner failure. The focused test passes locally. After changing the new
test's log reader to use a shared file handle, the complete Engine suite
passed 1,044/1,044 and the complete Service suite passed 57/57. The first
Service run had 56 passes and only that test's log-sharing failure; the final
run had zero failures and zero skips.

This verifies local host ordering. A current package running under the actual
SCM account, including a crash/restart injection, is separate deployed evidence.
