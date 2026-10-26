# P3 folder access warning, 2026-10-26

The first join on the physical third PC failed with a bare `NotFound` while the selected folder existed. The installed service could not open it until the `NT SERVICE\PeerSync` Modify grant was applied. The CLI grant repair is recorded in [P2 CLI folder service ACL](P2-cli-folder-service-acl-2026-10-26.md); this follow-up closes the related source feedback gaps.

- WPF Add and Join grant service access before sending the RPC. Their background scan now reports inheritance-protected files as well as directories, without changing those child ACLs or traversing reparse points. Both paths show the bounded manual-access warning after a successful RPC. The first 100 affected paths are retained, and the warning labels further entries when truncated.
- A missing or service-inaccessible root maps to a path-free `NotFound` detail that names folder access as the possible cause. The CLI prints that sanitized detail instead of only the status code.
- An ACL fixture verifies the protected file and directory warning data and that their protected flags remain set. A real named-pipe test verifies the `NotFound` detail, absence of the private path and no folder registration after failure.

Release solution build with `--no-restore`: zero warnings and errors. Focused App and Service regressions: 1/1 each. Full App suite: 24/24; Service control-pipe class: 16/16; CLI suite: 8/8. These are local source checks. The changed WPF warning has not yet been rendered in an installed app on a standard-user PC, and the repaired CLI/service are not yet deployed to the third PC. The physical three-PC corpus and three-hour gates remain open.
