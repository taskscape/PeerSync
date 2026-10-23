# Packaged field evidence collector, 2026-10-23

`installer/Collect-DeploymentEvidence.ps1` now ships in both Inno setups. It
creates a new local directory containing `deployment-evidence.json` and a
sanitized diagnostics ZIP. The summary records SCM state/account, installed
binary version/hash/Authenticode signer, crash-policy audit, and client
listener ownership when applicable. The Client role can make a correlated
HTTP/2 and HTTP/3 probe without retaining the observed address; the
Rendezvous role runs its packaged local preflight. Child commands have a
configurable 10–180-second timeout, both output pipes are drained while
waiting, and unexpected JSON or oversized output becomes a bounded issue
code. The command does not edit service settings or upload artifacts.

The initial development-signed 1.0.15 packages exposed a Windows PowerShell
5.1 process-wrapper defect: `Start-Process -PassThru` supplied a null
`ExitCode` after a bounded wait, so completed child commands were marked as
failures even though their JSON and ZIP were present. This was a collector
false negative, not a failed service, preflight or probe. The wrapper now owns
a `System.Diagnostics.Process`, drains stdout/stderr asynchronously, enforces
the timeout, reads a reliable exit code, discards raw output after parsing,
and rejects output over 4 MiB. The corrected source ran successfully in both
guests before final packaging.

Development-signed 1.0.16 x64 client and rendezvous installers built from
revision `24ae22395f1e43ebdcf5b16e50615513a6df47c2` with timestamped,
valid Authenticode signatures. Their respective SHA-256 hashes were
`F7556B9EDF28C10ACA616B825416A893DC492278952C5B18529241D187C1A06D`
and `CA4A89FCB335A3A1A15519EAE29BEAC94CFB5E802B7E2958CF70949AFEC6D98A`.
Both manifests state `workingTreeDirty: true` because an unrelated untracked
`Sync.zip.tmp` was left untouched; all tracked source files had been committed
before the builds. The package manifests and build logs are retained under
`artifacts/installer/`.

On the disposable Server 2022 guest, the client package matched the host
hash and valid signature, upgraded silently with exit 0, retained a Running
`PeerSync` service, and installed the exact repository collector with SHA-256
`3A0FCC5D5EA035D7747CB88A6F0B16C6C0C7AFE53A3FF16408F1E6A4F229B065`.
The packaged Windows PowerShell 5.1 Client command exited 0 with zero issues,
confirmed the expected service account and TCP/UDP listener ownership,
reported WER exclusions, and reached the installed Server 2025 central
server over correlated HTTP/2 and HTTP/3. Its sanitized ZIP had six entries
and SHA-256
`F274F8BD4B076513A91F21A48E00F6B11E4BC07779881B31DEF4266E6D8D566C`.

On the disposable Server 2025 guest, the rendezvous package matched its host
hash and valid signature, upgraded silently with exit 0, retained a Running
`PeerSyncRendezvous` service, and installed the same exact collector script.
The packaged Rendezvous command exited 0 with zero issues. Its local
preflight reported `localReady: true` and validated TLS, the crash-policy
audit found WER exclusions, and its sanitized two-entry ZIP had SHA-256
`DE6EEE235F89D8AF581006B0BC105606B92E0251BD4527BD74E10CB9AC900BCF`.

Copies of the two JSON summaries and ZIPs are under
`artifacts/vm/field-collector-1.0.16/`; installed package and command logs
remain under `C:\Temp\PeerSync` in each guest. A local ZIP/JSON review
confirmed each bundle hash and found none of the screened two-guest test
paths, file names, capability field names or VM addresses. This is a bounded
privacy check on these artifacts, not an independent security review.

These are installed virtual-machine results using a development signer and
the VM-only test trust/hosts mapping documented in the
[two-guest central and relay check](P4-two-guest-central-relay-2026-10-23.md).
They do not prove physical two-PC sync, provider TCP/UDP 99 admission, home or
mobile NAT behavior, WAN relay caps, 72-hour soaks or release signing.

A local transfer kit at `artifacts/deploy/PeerSync-P5-field-kit-1.0.16.0.zip`
contains the two exact signed development setups, their public `.cer` and
manifests, the scoped firewall helper, the hourly tree tool, and the operator
guides. The tracked `build/Build-PeerSyncFieldKit.ps1` builder accepts the
package version and checks manifest/package hashes, source revision and the
matching public certificate before creating the ZIP; `python -m zipfile -t` passed.
Its SHA-256 is
`2A3DE4DB5374305AFCCDE5CC7B18ACB8651F33C159754B9C484F13DE61FAE36B`.
The kit is local and ignored by Git; it has not been published as a release.
The [tree-tool control](P5-physical-soak-evidence-tool-2026-10-23.md)
records the exact packaged script hash and installed two-guest result.
