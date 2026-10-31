# Live folder policy replacement, 2026-10-23

The control API previously saved changed folder settings while the active
transport, scanner and scheduler continued using their construction-time
policy. Only predefined hosts were applied immediately. The dashboard told
the user to restart the entire service for approval, ignore, archive, scan
interval or transfer-priority changes.

An active non-host-only change now uses the same drain sequence as a manual
pause: remove the folder from peer membership, close and await its sessions,
stop its scheduler, await pending rescans, then dispose its engine. The new
settings are saved while the folder is paused. Resume opens a new scanner,
coordinator and schedule with those settings, registers status and requests
peer recovery. The control operation holds the folder-management gate across
the handoff, so another control mutation cannot interleave. Other registered
folder engines continue running, though shared peer sessions may reconnect.
Host-list-only changes retain their lighter live resolver refresh.

LAN discovery can start or stop when the active folder set changes. Tracker
announcements and predefined-host resolution refresh after replacement. If
the engine reopens but network policy refresh fails, the control reply marks
`RestartRequired` and logs a sanitized failure. If reopening fails, the new
policy remains durably paused instead of exposing a partially configured
engine; the user can fix the cause and resume. If saving the new policy fails
after the old engine drained, the host reopens the prior policy before
reporting the error. Injected test clocks are retained when a folder or
network runtime is recreated.

Local Release evidence:

- The verified named-pipe control test changes approval, archive age, ignore
  rules, rescan interval and transfer priority. It confirms a new live engine,
  saved values, and actual scanner exclusion/inclusion of native files.
- The installed two-host QUIC test changes archive policy during an approved
  session, verifies a subsequent native-byte transfer after reconnection,
  and retains the blocked-device rejection check.
- The complete Service suite passes 57/57, zero failures and zero skips.
  An earlier run passed 56/57 because its new positive scan fixture used
  `.tmp`, which the default ignore policy excludes; changing the fixture to
  `.txt` made the focused and complete suite pass. No production change was
  made for that test error.

The code has not yet been installed as a new SCM package, and the physical
two-PC setting-change/reconnection check remains open. This local result
does not close P2's physical gate or P5's release gate.
