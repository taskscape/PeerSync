# P5 local signed-installer CI parity, 2026-10-23

GitHub Actions run
[39892770246](https://github.com/taskscape/PeerSync/actions/runs/39892770246)
denied every job before starting because an Actions budget prevents further
use. I therefore ran the repository's installer CI script locally against
source revision `6fd1c6427cc0a1f95f8c1eb0974ec5e73222ff16`:

~~~powershell
./build/Run-InstallerCi.ps1
~~~

It completed with exit code 0 and the final message `Signed development x64,
Arm64 and Rendezvous installers built and verified.` The script performs a
locked restore, self-contained ReadyToRun publishes, Inno 7.1 compilation,
temporary development Authenticode signing, timestamps, signature and
publisher checks, Microsoft VC++ redistributable verification, and manifest
hash checks. On each newly signed client setup it ran the Windows PowerShell
5.1 online signer-chain verifier; x64 also rejected a deliberately wrong
publisher pin. The rendezvous setup and signed payload checks completed.

| Development installer | SHA-256 |
| --- | --- |
| `PeerSync-Setup-1.0.0.0-x64.exe` | `4A9FA10691B691DB3FBDB6F39125CA3716D6433664BAD77BFC9C72EBE75CE5B0` |
| `PeerSync-Setup-1.0.0.0-arm64.exe` | `7B3519DD9785E6FC6765B62B85E7A9F415CB066FC0A92CF0474A73DEB31AE5B0` |
| `PeerSync-Rendezvous-Setup-1.0.0.0-x64.exe` | `99C4D72E5B3D55D241CED5191E10AC6986B2F666423399C38A4C41CA43A2E1D8` |

The three generated manifests record `signed=true`,
`localDevelopmentOnly=true`, `publicReleaseSigning=false` and the same source
revision. `workingTreeDirty=true` reflects the preexisting untracked
`Sync.zip.tmp`; no tracked source changes were present during the build. The
temporary signing certificate thumbprint was
`A5E1F98F8F961B9E12F16E5660675AA43151E686`; after the script exited it
was absent from both `LocalMachine\Root` and `LocalMachine\My`.

This is local packaging parity, not a hosted Actions pass or an installed
upgrade. The current-source signed installers have not run on a clean Windows
PC or Server VM. The release publisher, revoked-CA negative case and public
feed remain open.
