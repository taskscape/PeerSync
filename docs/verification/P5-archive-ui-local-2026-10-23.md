# P5.5 archive UI local check, 2026-10-23

The Archive tab's live WPF visual tree displayed a selected folder's
`notes.txt` version, its localized 1 MiB size, a truncation notice, and
visible Restore and Restore as copy actions. The test also verified that
the version row was realized and visible. The focused case passed 1/1;
the non-render App suite passed 17/17. This fixture supplies a control
response to the view model and checks the actual `MainWindow` controls;
it is not an installed-service restore test.

The initial pixel-render attempt was inconclusive. This Windows session was disconnected
(`quser` reported session 1 as `Disc`), and `RenderTargetBitmap` returned a
fully transparent bitmap for both the new Archive view and the repository's
existing clock-status render test. The existing clock test failed at its
opaque-pixel assertion under the same conditions. No screenshot from this
session is accepted as UI evidence. The active-desktop rerun follows below;
installed verification remains a separate P5.5 check.

## Active-session rendered check

Later on 2026-10-23, `quser` reported RDP session 1 as `Active`. The existing
clock-status pixel-render case passed, confirming that this desktop can
produce opaque WPF pixels. A new Archive render case then selected the actual
`MainWindow` Archive tab, required a realized version row, the formatted
archived date, visible Restore and Restore as copy buttons, and the
truncation notice. Its `RenderTargetBitmap` contained opaque, dark and light
pixels above the assertions. I inspected the saved [Archive screenshot](archive-tab-render-2026-10-23.png):
the path `notes.txt`, local archived date, `1,0 MiB` size and both actions are
legible, with no overlap or clipping. The screenshot uses a synthetic model
response; its service-connecting banner is expected and is not an installed
service result.

The focused Archive render case passed 1/1, and the complete Release App
suite passed 21/21 with zero failures and zero skips. The local Windows
`PeerSync` service was running but its installed source revision was
`87a247fe5051009a20d6850d4ca526626bb7c724`, before the P5 Archive
implementation, and `C:\Program Files\PeerSync\app\PeerSync.exe` was absent.
Therefore an installed Archive restore action remains unverified; no update
or restore was attempted against that older service.
