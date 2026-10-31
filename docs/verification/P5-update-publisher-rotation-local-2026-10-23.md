# P5.7 publisher rotation guard, local check, 2026-10-23

An already-staged `.ready` package prevents `UpdateDownloader` from fetching
another offer. After the administrator changes the publisher pin, the SYSTEM
preflight rejects that old package. The prior behavior could leave updates
stalled without a clear policy-change error.

`Set-UpdatePolicy.ps1` now refuses a different publisher while a `.ready`
slot exists, preserving the current policy and the old package for review.
The administrator guide records the safe disposition and re-enable sequence.
The guard does not remove pending files or change machine trust.

The Windows PowerShell 5.1 fixture overrides `ProgramData` within its process
and creates a temporary public test certificate. It verified that rotation
with an old ready slot fails without changing the policy bytes, then succeeds
after that fixture slot is removed. The fixture was removed. Command:

~~~powershell
& "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File .\build\Test-UpdatePolicyRotation.ps1
~~~

The C# cross-component test stages an old signed offer, changes policy to an
independent signer, confirms old preflight denial and zero new feed requests
while the old slot remains, disposes that test slot, then confirms the new
signed offer stages and passes preflight. It also confirms the new slot fails
if policy is rolled back to the old signer. The focused test passed 1/1; the
full Service suite passed 56/56 with zero warnings or build errors.

This is local development-signature validation. A release signer, its
revocation evidence, public feed, and installed-host rotation remain open.
