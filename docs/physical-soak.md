# Physical LAN and WAN soak evidence

Use this procedure for the still-open Gate P2, P4 and P5 checks in `plan.md`.
The tree tool measures evidence; it does not perform sync, generate random
operations, change network conditions, or turn a virtual-machine check into a
physical gate result. Keep the two development packages and field collector
versions identical during a single run. Record each physical PC's model,
Windows version, network adapter/link speed, service and installer versions,
the corpus generation command and hashes, and UTC start/end times separately.

For Gate P2, use three physical PCs and a 50 GB mixed corpus including large
files, 100,000 small files and deep trees. Record each random create/edit,
rename, delete, permission change, sleep/resume and Wi-Fi switch with UTC time
and the affected PC. For Gate P4, additionally record the actual home NAT,
mobile/CGNAT, direct-block and relay conditions; collect the correlated client
and central diagnostics bundles. Do not treat a private overlay address as a
native NAT result. Keep full local logs and share only the sanitized evidence
bundle after review. Gate P5 needs the release candidate on physical LAN and
WAN; development-signed packages cannot close it.

## Capture each hour

Choose a fresh run GUID and copy it to every PC. At each UTC hour, wait until
all peers have finished syncing and pause test writes long enough for a stable
scan. On each PC, run the packaged script from **Windows PowerShell 5.1** with
the same GUID and literal hour value copied from the first PC, a distinct
label, and the PC's actual synchronized
folder. The report directory must already exist and be outside that folder.
Use a new output file for every hour:

If local execution policy blocks an unsigned helper, open a new PowerShell
process with `powershell.exe -NoProfile -ExecutionPolicy Bypass` for this test
session. Verify the script SHA-256 against the field-kit README first; do not
change machine-wide execution policy.

```powershell
$run = [guid]'11111111-2222-3333-4444-555555555555' # replace once per soak
$hour = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:00:00Z')
& .\tools\Measure-PeerSyncTree.ps1 -Root 'C:\PeerSync-Test\Share' `
  -RunId $run -MachineLabel 'pc-a' -HourUtc $hour `
  -OutputFile "C:\Temp\PeerSync-soak\pc-a-$($hour.Replace(':','')).json"
if ($LASTEXITCODE -ne 0) { throw 'Tree capture failed; retain its report.' }
```

Use `pc-b` and `pc-c` on the other machines. The same tool source SHA-256 must
run on all peers. Capture includes relative names, directory shape, file sizes
and SHA-256 of full file contents, omitting only the root `.psp` metadata
directory. It refuses reparse points and detects some concurrent mutations;
failed scans must be investigated and repeated after quiescence. It writes only
counts and digests, not file names or contents, in the shareable JSON report.
The report cannot identify *which* path differs, so inspect local indexes and
sanitized diagnostics on the machines if a comparison fails. A digest match is
not a substitute for zero deferred/failed transfers or the operation log.

Copy the three reports for an hour to an evidence workstation. Compare them
with a new output name:

```powershell
& .\tools\Measure-PeerSyncTree.ps1 -Snapshots @(
  'C:\Temp\PeerSync-soak\pc-a-hour.json',
  'C:\Temp\PeerSync-soak\pc-b-hour.json',
  'C:\Temp\PeerSync-soak\pc-c-hour.json') `
  -MinimumMachines 3 -OutputFile 'C:\Temp\PeerSync-soak\compare-hour.json'
if ($LASTEXITCODE -ne 0) { throw 'Peers differ or evidence is incomplete.' }
```

This rejects failed/missing snapshots, duplicate machine labels, a different
run or hour, differing tool versions, mismatched tree hashes/counts and scans
whose start times are more than 30 minutes apart. Capture the first hour and
the hour exactly 72 hours later, giving at least 73 consecutive hourly
comparison reports for a 72-hour soak. Audit the full ordered set:

```powershell
$reports = @(Get-ChildItem 'C:\Temp\PeerSync-soak\compare-*.json' |
  Sort-Object Name | Select-Object -ExpandProperty FullName)
& .\tools\Measure-PeerSyncTree.ps1 -Reports $reports -MinimumHours 72 `
  -RequiredMachines 3 -OutputFile 'C:\Temp\PeerSync-soak\soak-audit.json'
if ($LASTEXITCODE -ne 0) { throw 'Soak evidence is incomplete.' }
```

The audit requires a single run ID, the same tool and machine set, 73 or more
consecutive UTC hours, at least 72 hours between the first and last recorded
comparison times, and a passing comparison with three or more peers at each
hour. A passing audit establishes only tree agreement at those checkpoints.
Inspect the operation,
service and diagnostic evidence for data loss, errors, resets and recovery,
and retain the full reports and environment record with the gate decision.
If a machine sleeps across a scheduled check, do not silently fill that gap;
record the gap and repeat the full timed gate when required.
