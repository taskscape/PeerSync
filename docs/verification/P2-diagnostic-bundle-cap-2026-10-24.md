# Bounded diagnostic bundle under live log volume, 2026-10-24

The installed first PC's `psp diagnostics` failed before writing a ZIP with
`IOException: The diagnostic bundle event limit was exceeded`. Its log root
contained four structured journal files with more than 100,000 recent valid
events. This exposed an operational gap in P2.5.6: the third PC could also
fail to collect evidence after a long or noisy run. Staging the collector only
inside the joined folder was circular when sync itself was under diagnosis.

The collector now scans the same 24-hour allowlisted journal input, retains
the latest 100,000 sanitized events with room for up to 20,000 recent warning
or error events, and writes them in timestamp order. It preserves its file,
event-size and uncompressed-byte ceilings. It reports valid events omitted by
the bounded selection in the command result and `environment.json`; a nonzero
omission count means that absence from the ZIP is not proof an event never
occurred. Invalid and expired counts stay separate. Source file paths, raw
fields, FolderIds, keys and network addresses are not copied into the default
bundle. A local CLI command that writes to `%TEMP%` works independently of
folder sync; it is documented in [the diagnostics guide](../diagnostics.md).

The Release Core suite passed 152/152, including a 100,002-event case that
retained an older socket warning, kept the newest ordinary event and reported
two omissions. CLI passed 6/6 and Rendezvous passed 21/21. TRX evidence is
under `artifacts/tests/diagnostic-bundle-cap/`.

The current-source CLI then collected the first PC's live service journals
successfully. It included 100,000 events from four source files, reported
2,522 omitted, zero expired and zero invalid, and produced a 2,056,168-byte
ZIP. The sample contained 269 warnings, zero errors, monotonic UTC timestamps
and no `remoteAddress` field. Its SHA-256 was
`EEA73302509BE2E8A742316B321683208E2778B43E8459473D616DA6FF66C205`;
the local archive is retained under
`artifacts/field/diagnostic-bundle-cap-20261024/`. This is a first-PC
collection, not a diagnosis of the third PC's `SocketException`. That PC
still needs its own fresh bundle and the updated collector before P2.5.6 or
the physical WAN gate can close.
