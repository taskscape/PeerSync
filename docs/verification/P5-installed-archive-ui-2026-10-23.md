# P5.5 Archive UI against the installed service, 2026-10-23

The local `PeerSync` SCM service was upgraded from the preserved P2 package to
the current self-contained service package at source revision
`5d514c6b7b8670cb77e42b9427e1e773876a0eab`. The previous package remains
at `C:\PeerSync-Test\service-package-backup-20261023-p5-archive`; service
configuration, state and test folders were not replaced. The service was
Running after the upgrade. The installed service executable's SHA-256 is
`B3AB92805C37F7D344EB193EC72FCDBD06F0D1F90DC89A5A2BDA57345F391911`.

The opt-in `PeerSync.InstalledProbe --archive-ui` case ran on the active RDP desktop
using the current App assembly (SHA-256
`6BB1D666E26C32A45DCD5BB2D1CD2FFA7684653895F36773FC879F922F54AA9C`).
It granted the service access to a fresh disposable folder, added it through
the verified installed-service pipe, waited for the native `notes.txt` index
entry, changed `before` to `after`, and waited for the actual archive response.
It displayed that response in `MainWindow`, selected the Archive row, invoked
Restore, and checked that the native file again contained `before`. It then
selected the row again, invoked Restore as copy, entered a relative destination
in the real prompt and checked that `restored-copy.txt` also contained
`before`. Both control operations completed; the test removed its service
folder registration and retained the native result under
`C:\PeerSync-Test\archive-ui-7599942dc382476ea929cb4fa49d7c83`.

The reviewed [pixel screenshot](archive-tab-installed-service-2026-10-23.png)
shows the live Archive row, archived date, `6 B` size and both action buttons.
Small versions previously displayed as `0,0 MiB`; the App now formats bytes,
KiB and MiB at the corresponding thresholds. The screenshot is 970×700 pixels
and has SHA-256
`A06C70553BF84D9105877268EE8D9C8EEEF252366C3D31BDA276DDFD163513AB`.

The first harness run restored the original file but stopped before the copy
prompt because the test had not reselected the Archive row after the UI
refreshed the list. After fixing that harness step, the UI path passed; after
the size-display change and screenshot assertion it passed again. It was then
moved to the existing opt-in installed-service probe so the canonical App
runner stays free of host-dependent skips. The final probe returned exit 0
with `originalRestored=true`, `copyRestored=true`, and `pixelRender=true`.
The regular App runner passed all 21 cases with zero failures or skips.
The executable command was:

```powershell
& spikes\InstalledServiceProbe\bin\Release\net10.0-windows10.0.20348.0\PeerSync.InstalledProbe.exe --archive-ui
```

This checkpoint checks the real UI code and control calls against the
installed service; the App assembly came from the Release test build. The
subsequent [signed installed-client check](P5-installed-client-archive-2026-10-23.md)
adds a separate-process installed App package. Physical two-PC and P5 release
gates remain open.
