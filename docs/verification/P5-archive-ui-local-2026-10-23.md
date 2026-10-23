# P5.5 archive UI local check, 2026-10-23

The Archive tab's live WPF visual tree displayed a selected folder's
`notes.txt` version, its localized 1 MiB size, a truncation notice, and
visible Restore and Restore as copy actions. The test also verified that
the version row was realized and visible. The focused case passed 1/1;
the non-render App suite passed 17/17. This fixture supplies a control
response to the view model and checks the actual `MainWindow` controls;
it is not an installed-service restore test.

Pixel rendering remains open. This Windows session was disconnected
(`quser` reported session 1 as `Disc`), and `RenderTargetBitmap` returned a
fully transparent bitmap for both the new Archive view and the repository's
existing clock-status render test. The existing clock test failed at its
opaque-pixel assertion under the same conditions. No screenshot from this
session is accepted as UI evidence; repeat the rendered and installed
checks in an active interactive desktop before closing P5.5.
