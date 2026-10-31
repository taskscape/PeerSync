# Repository instructions

## Versions and main commits

- Before creating **every commit destined for `main`**, increase `<Version>`
  in `Directory.Build.props` above the version in its main-branch parent.
  This applies to fixes, documentation, merges, and release follow-ups too.
- Use a numeric `major.minor.patch` version. Increment the patch unless the
  change warrants a minor or major release. Never reuse or decrease a version.
- `Directory.Build.props` is the version source for assemblies, default
  installer builds, CI package names, and release metadata. Use
  `build/Get-Version.ps1` rather than hard-coding a release version elsewhere.
- An explicit installer `-Version` override is for compatibility testing or
  an intentional package build; it does not replace the main commit's bump.
- Verify the changed behavior before committing. Publish directly to `main`
  only when requested; preserve unrelated working-tree changes.

## Installer compatibility

- Attended and manually launched silent installers must allow reinstalling,
  upgrading, and downgrading to the package's version without an override.
- An absent/unreadable previous executable must not prevent repair of a
  leftover service registration. Keep service/process exit, account,
  certificate, signature, and prerequisite checks in place.
- Preserve settings, identity, databases, and synced files during replacement.
  The unattended signed-update offer policy is separate from manual setup.
