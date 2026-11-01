# PeerSync desktop interface implementation and visual review

The refined second design is implemented as a native WPF skin. Existing folder and service command handlers, key capabilities, restore semantics, preference validation, notifications, and tray lifecycle remain in use. The service, engine, and control contracts have no source changes.

## Functional scope

- Overview: add, join, per-folder pause/resume, folder actions, approval and overwritten-version counts, service connection and recovery controls.
- Folder details: devices, fingerprints, approval and transport, folder activity, both existing version sources, refresh, replacement restore, restore as copy, and all existing folder settings.
- Device approvals: the existing approve, deny, and block commands and fingerprint confirmations. Folder paths replace opaque folder IDs in the request table; command IDs remain unchanged.
- Preferences: the existing device name, listening port, discovery servers, transfer limits in integer bytes/s, LAN discovery, port mapping, relay, and diagnostics fields. Settings not exposed by the UI are preserved when saving.
- Sharing: the existing read-only default, write-key reveal confirmation, encrypted-only export, copy, and QR generation. Capability restrictions are preserved.
- Tray: timed pause, indefinite pause, resume all, open, hide on close/minimize, and explicit exit.

Open folder, Clock details, and selecting KiB/s or MiB/s display the exact message **Not supported yet**. Unit selection resets to bytes/s without changing the entered value. No Explorer launch, clock-status integration, rate conversion, completion estimate, or new engine capability is implemented.

## Visual references and evidence

Approved source images are under `C:/Users/offic/.codex/generated_images/01a193fa-e785-7052-9ecc-56ea8bd10e6b/`:

| Screen | Approved source | Rendered implementation | Comparison |
| --- | --- | --- | --- |
| Overview | `exec-a45a9126-58fa-4cce-8b2a-50f7ffd4c312.png` | [overview.png](artifacts/refined-ui/overview.png) | [overview-comparison.png](artifacts/refined-ui/overview-comparison.png) |
| Versions | `exec-81dfd277-b706-4fc7-a71d-bf9830f60fcd.png` | [versions.png](artifacts/refined-ui/versions.png) | [versions-comparison.png](artifacts/refined-ui/versions-comparison.png) |
| Settings | `exec-7b81e644-5344-4f51-9762-ad5374b687a7.png` | [settings.png](artifacts/refined-ui/settings.png) | [settings-comparison.png](artifacts/refined-ui/settings-comparison.png) |

The main-window fixture is 1440 × 1024 WPF units. Its client area renders at 1425 × 986 pixels using `RenderTargetBitmap` at 96 DPI. Source images are 1487 × 1058 pixels. Comparison sheets remove the source's 48-pixel title bar and scale each client image to 1000 pixels wide, preserving its aspect ratio. These are native WPF renders; browser viewport and CSS density do not apply.

The overview reference has its folder menu open; the full implementation screenshot has it closed. The matching open-menu state is captured separately in [folder-menu.png](artifacts/refined-ui/folder-menu.png) and compared in [menu-comparison.png](artifacts/refined-ui/menu-comparison.png). Menu labels and activation are also checked by UI tests. The source and implementation use different sample folder names, counts, dates, and preference values. The implementation derives names from real folder paths and uses existing local date formatting.

Additional captures: [small folder settings](artifacts/refined-ui/folder-settings-small.png), [unavailable service](artifacts/refined-ui/service-unavailable.png). Render files are reproducible test artifacts in the ignored `artifacts/` directory.

## Review and corrections

1. **Typography:** an initial selected-tab style inherited semibold text into the entire page. Selection styling now targets only the tab label. Segoe UI, 16-DIP body controls, distinct headings, and muted secondary text remain consistent across the screens.
2. **Layout:** the initial fixed status column sat too far right. Folder names and status now share proportional columns, with fixed-width actions. List content no longer stretches its border through unused vertical space. Forms scroll at the minimum window size.
3. **Capture quality:** client-only renders initially omitted the window background. The shell now explicitly paints its background, so captured content is opaque and accurately represents the window.
4. **Menus:** the initial popup used the small default menu font. It now inherits the app font and uses larger native menu-item padding. All six actions remain visible and keyboard-accessible.
5. **Live state:** a snapshot's temporary empty collection could return an open folder to Overview. Collection-driven overview updates now run after the snapshot replacement completes; a regression test keeps the folder view open across a peer-status update.

Post-correction captures were reviewed against the approved screens. No blocking clipping, overlap, missing workflow, or unreadable control was found in the exercised states. The native WPF menus, checkboxes, combo boxes, and grid formatting intentionally retain platform behavior. Their density and minor decoration differ from the generated mockups.

## Required visual checks

- **Fonts and typography:** readable native control sizes; heading hierarchy; normal body weight; path truncation with tooltips; wrapping status and explanatory text.
- **Spacing and layout:** common navigation, page margins, aligned folder actions, grouped settings, and scrollable content. Minimum-size checks exercise 740 × 560 windows and keep navigation/action controls reachable.
- **Colors:** teal primary actions, white surfaces, subtle separators, muted secondary text, amber waiting/service notice, and pale selection. Status always includes words.
- **Assets:** the folder icon uses the Windows Segoe Fluent Icons font with the Segoe MDL2 fallback. No generated illustration or simulated file preview is added. QR images still come from the existing key exporter and QR generator.
- **Copy:** connection is not labeled completion; service status is distinct from folder status; restore-as-copy and replacement remain separate; diagnostics text retains the existing privacy explanation.

## Validation

- Release solution build: **passed, zero warnings and zero errors**.
- `PeerSync.App.Tests`: **32 passed, zero failed**. This includes the existing controller, clock/engine fixture, archive rendering and route rendering checks, plus eight added UI/presentation checks.
- Added checks cover navigation, unsupported-action messages, both version sources and selection-dependent recovery controls, full preference round-tripping, unsupported units, validation, minimum-size access, live approval/service status, peer refresh stability, key capabilities/QR, and close-to-tray behavior.
- Existing archive visual tests and both installed-client archive probes use the new Versions navigation and restore labels. Probe projects compile in the solution build.
- All 14 existing folder/service command handler bodies were compared with the original revision and are unchanged. Preference parsing and update fields preserve the original rules.
- `git diff --check`: passed.

The installed application was not replaced or restarted. Opt-in installed-service probes, other repository test suites, manual high-contrast testing, and a live two-PC sync session were not run for this UI-only change. Local fixture rendering does not establish installed-machine or network acceptance.

## Follow-up polish

The generated mockups use larger display typography and more decorative action/status icons than the native implementation. The current skin favors consistent standard controls and readable data. This is visual polish only; no extra engine functionality is needed.

final result: passed
