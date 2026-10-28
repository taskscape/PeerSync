# Physical LAN and WAN soak evidence

Use this procedure for the still-open Gate P2, P4 and P5 checks in `plan.md`.
The tree tool measures evidence; it does not perform sync, generate random
operations, change network conditions, or turn a virtual-machine check into a
physical gate result. Keep the two development packages and field collector
versions identical during a single run. Record each physical PC's model,
Windows version, network adapter/link speed, service and installer versions,
the corpus generation command and hashes, and UTC start/end times separately.

For Gate P2, use three physical PCs and a 9 GiB mixed corpus including large
files, 50,000 small files and deep trees. Record each random create/edit,
rename, delete, permission change, sleep/resume and Wi-Fi switch with UTC time
and the affected PC. For Gate P4, additionally record the actual home NAT,
mobile/CGNAT, direct-block and relay conditions; collect the correlated client
and central diagnostics bundles. Do not treat a private overlay address as a
native NAT result. Keep full local logs and share only the sanitized evidence
bundle after review. Gate P5 needs the release candidate on physical LAN and
WAN; development-signed packages cannot close it.

For a native WAN run, first place the outside PC on its independent physical
uplink and verify its public rendezvous route. The packaged
`tools/Start-NativeWanSoakIsolation.ps1` can keep Tailscale available for
management while blocking **only** the installed PeerSync service on that
adapter. Use a fresh run GUID, an existing joined folder and evidence directory,
and the same UTC first hour as the scheduled soak launchers. Run its
`-PreflightOnly` check before scheduling any work. Set
`-DisallowedUplinkPrefix` to the LAN prefix the PC is leaving; if the physical
route still uses that LAN, preflight fails. It also requires a hardware uplink,
running service, a test tree within 10 GiB, and correlated TCP/UDP 99 probes.
After the soak tasks are staged, run the helper elevated **2–10 minutes before**
hour 0, without `-PreflightOnly`, and retain its JSON report:

```powershell
$run = [guid]'11111111-2222-3333-4444-555555555555' # same soak run ID
$first = '2026-10-27T13:00:00Z' # same future first capture hour
$root = 'C:\PeerSync-Test\Share' # outside PC's joined folder
$kit = 'C:\PeerSync-Gate-Evidence\soak-kit\tools'
$reports = 'C:\PeerSync-Gate-Evidence\soak' # existing; outside the share
& "$kit\Start-NativeWanSoakIsolation.ps1" -RunId $run `
  -FirstHourUtc $first -Root $root -OutputDirectory $reports `
  -KitDirectory $kit -ExpectedUplinkAlias 'Wi-Fi' `
  -DisallowedUplinkPrefix '192.168.0.' -PreflightOnly
# Once the other peers' soak tasks are registered and the first hour is near,
# rerun the same command elevated without -PreflightOnly.
```

The helper installs two run-specific firewall rules, restarts PeerSync to
drain earlier overlay sessions, checks the rules and physical route, and
registers a SYSTEM recovery task. The active workload still stops at hour 3.
Recovery removes the two rules after the final read-only capture grace period,
restarts PeerSync and writes a separate recovery JSON. Check both reports,
the task exit, selected transport and service logs. A successful setup or
probe alone does not establish native WAN file transfer; the required hourly
native-tree and operation evidence still decides the gate. If setup fails,
the helper removes its rules and task and attempts service recovery. Never
reuse the run ID for an incomplete test.

Before running any packaged unsigned helper, verify its SHA-256 against the
field-kit README. If local execution policy blocks it, open a new PowerShell
process with `powershell.exe -NoProfile -ExecutionPolicy Bypass` for this test
session; do not change machine-wide execution policy.

## Create the mixed LAN corpus

On the first physical PC, the default 9 GiB corpus requires at least 23 GiB
free on its volume: 9 GiB for native content, 9 GiB for PeerSync's local
blocks, and 5 GiB of headroom. The generator checks the exact available bytes;
the other two PCs also need room for native and block copies. The generator
creates 9 GiB of random file content: 50,000
small files, 24 nested levels with a file at each level, and eight large
files. Its small local JSON report counts content bytes and files; the
run-specific `owner.json` marker adds a little metadata outside that total.
The 9 GiB default leaves at most 1 GiB for later random-operation files;
existing shared content reduces that headroom. Check the whole-tree preflight
and retain the actual remaining amount in the field report.
The random content is intentionally hard to compress. The generation script
never removes an existing tree and refuses a reused run ID, reparse paths,
an output inside the synchronized folder, and insufficient free space.
Keep `Get-PeerSyncTestTreeContentBytes.ps1` beside the generator in the
extracted `tools` directory. Before creating a run, the generator measures
existing synchronized content (excluding PeerSync's root `.psp` metadata)
and refuses a 9 GiB run if that content plus the new corpus and ownership
marker would exceed 10 GiB. This preflight catches old installer and test
files left in a reused share. Move no files during the paused preflight;
choose a fresh test folder or archive old test artifacts outside the share
before generating. The final tree capture independently enforces the cap.
Use `-PreflightOnly` with a new run GUID and unused report path to print the
existing, projected and free-space byte counts without creating files.

Pause this test folder in the app on the first PC before generation so peers
do not consume partially written large files. Create the report directory
outside the synchronized root, then run the verified field-kit script in
Windows PowerShell 5.1:

```powershell
$ErrorActionPreference = 'Stop'
$run = [guid]::NewGuid()
$root = 'D:\PeerSync-Test\Share' # replace with the actual joined folder
$reports = 'C:\Temp\PeerSync-corpus'
New-Item -ItemType Directory -Force -Path $reports | Out-Null
$report = Join-Path $reports "corpus-$($run.ToString('N')).json"
& .\tools\New-PeerSyncGateCorpus.ps1 -Root $root -RunId $run `
  -OutputFile $report
if ((Get-Content $report -Raw | ConvertFrom-Json).status -ne 'complete') {
  throw 'Corpus generation did not complete; keep the folder paused and inspect the partial run.'
}
```

Require `status: complete`, `createdFiles: 50032`, and
`createdContentBytes: 9663676416` in that report. Preserve the command,
report, run ID and script SHA-256. Resume the folder and record the UTC time;
require all three peers to settle within three hours, then use the tree
measurement tool below on all PCs with the same
run ID and UTC hour. If generation stops partway through, leave the partial
subtree for inspection; start a new run ID only after deliberately resolving
the old one. The small-fixture control for this tool is recorded in the
[generator verification](verification/P2-physical-corpus-generator-2026-10-24.md).

## Run random operations

For an unattended three-hour operation window, the packaged
`Invoke-PeerSyncPhysicalSoak.ps1` starts the hourly capture loop and runs
bounded operation batches on one PC. Stage the launcher and its four companion
scripts together in a directory outside the synchronized folder. Use the same
fresh run GUID and future UTC hour on all three PCs, with distinct labels and
existing local output directories. Run each launcher in Windows PowerShell
5.1 before that hour. `-PreflightOnly` checks the tree's 10 GiB content limit,
tool hashes, paths and timing without starting the soak:

```powershell
$run = [guid]'11111111-2222-3333-4444-555555555555' # replace once
$first = '2026-10-27T13:00:00Z' # replace with the same future UTC hour
$root = 'C:\PeerSync-Test\Share' # replace on each PC
$reports = 'C:\PeerSync-Gate-Evidence\soak' # outside the share; create first
& .\tools\Invoke-PeerSyncPhysicalSoak.ps1 -Root $root -RunId $run `
  -MachineLabel 'pc-a' -FirstHourUtc $first -OutputDirectory $reports `
  -PreflightOnly
# After all three preflights return ready, rerun the same line without
# -PreflightOnly on each PC; keep each launcher process running.
```

The launcher records its own SHA-256 and companion-tool hashes in a unique
JSONL journal, keeps child stdout/stderr outside the share, and refuses a
duplicate run/label journal. It starts four hourly snapshots at hours 0–3,
runs random batches only during minutes 35–50 of each active hour, after the
hourly scans finish, and stops
operations at hour 3. Each batch has a 15-minute or remaining-window bound;
failure stops its exact child process and retains the log. The final read-only
hour-3 capture can finish after operations stop. A `complete` launcher record
does not itself pass the gate: collect all three machines' four snapshots and
run the batch comparator below. Record sleep/resume, Wi-Fi changes, permission
changes and native route evidence separately. If a machine restarts or a
scheduled hour is missed, preserve the partial journals and treat that run as
incomplete.

For unattended physical PCs, an elevated administrator can register the
packaged `Register-PeerSyncPhysicalSoakTask.ps1` instead of keeping a console
open. Give every PC the same future UTC hour and run ID, and its own label and
folder path. The registrar runs the launcher preflight, starts its SYSTEM task
20 minutes before hour zero, and retains task stdout/stderr and a registration
receipt outside the share. Register all three before the launch time and
verify each task is `Ready`. Its 4 h 30 min task limit covers the 20-minute
lead, three-hour active window and final read-only capture grace. A task
registration or `Ready` result is not a soak pass; inspect each task result,
launcher and capture journals, operation logs, physical network/power records,
and four hourly comparisons after the run.

The manual batch and capture-loop commands below remain available for a
controlled recovery or focused check. Do not run them concurrently with the
launcher for the same run and label.

The kit's `tools/Invoke-PeerSyncSoakOperations.ps1` creates and changes files
only under `PeerSync-Soak\<run-guid>\<machine-label>` inside the specified
synchronized folder. Its per-machine ownership marker prevents a different
run or label from taking over an existing workload folder. It never deletes
other folders or files. Each invocation records a JSONL operation trail in an
existing local directory **outside** the synchronized folder. Keep these
trails, including any failed operation, with the gate evidence. The tool uses
a bounded file count and random create, overwrite, append, rename and delete
operations. Do not use it on irreplaceable data; the test subtree will sync
and occupy space on all peers.
Keep the tree-budget helper beside this script too. Before each batch, the
script measures the current tree and reserves the maximum possible growth of
that batch on each of three PCs. Concurrent unrelated writes can still race
this metadata preflight, so the hourly full-content tree capture remains the
authoritative 10 GiB assertion.

From each physical PC, use the same run GUID but a distinct machine label.
This example runs repeated short batches through the final capture hour while
its PowerShell session remains open. Create the log directory first and use
the same `$run` as in the hourly capture commands below:

```powershell
$run = [guid]'11111111-2222-3333-4444-555555555555' # replace once per soak
$first = '2026-10-23T11:00:00Z' # same future UTC hour used by the capture loop
$label = 'pc-a' # pc-b and pc-c on the other machines
$root = 'C:\PeerSync-Test\Share'
$logDirectory = 'C:\Temp\PeerSync-soak-operations'
New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
$end = [DateTime]::Parse($first).ToUniversalTime().AddHours(3)
while ((Get-Date).ToUniversalTime() -lt $end) {
  $minute = (Get-Date).ToUniversalTime().Minute
  if ($minute -lt 15 -or $minute -ge 55) {
    Start-Sleep -Seconds 60 # shared quiet window for hourly hashing
    continue
  }
  & .\tools\Invoke-PeerSyncSoakOperations.ps1 -Root $root -RunId $run `
    -MachineLabel $label -LogDirectory $logDirectory -Count 5
  if ($LASTEXITCODE -ne 0) { throw 'Operation batch failed; retain its JSONL.' }
  Start-Sleep -Seconds 300
}
```

If the console stops during a reboot, restart with the same GUID, label and
root, and retain all batch logs; record the downtime. The script does not
perform sleep/resume, Wi-Fi switches or permission changes. Do those
deliberately and record their UTC times and outcomes separately. An
unattended loop that stops or loses hours cannot by itself pass the gate.
If hashing the full corpus takes longer than 15 minutes on any peer, reduce
the workload within the 10 GiB ceiling and restart the timed gate. Every
capture must start within 15 minutes of its nominated UTC hour.

## Capture each hour

Choose a fresh run GUID and copy it to every PC. At each UTC hour, leave a
common quiet window so all peers finish syncing and no test writes interrupt
the full-content scan. On each PC, run the packaged script from **Windows
PowerShell 5.1** with the same GUID and literal hour value copied from the
first PC, a distinct label, and the PC's actual synchronized folder. The
report directory must already exist and be outside that folder. Use a new
output file for every hour.

For unattended capture, start this in a **separate** PowerShell 5.1 session
on each PC before the same future UTC hour. Copy the identical first-hour
value to all PCs. The loop waits for each hour, writes a new snapshot and a
local JSONL journal, and stops with a failed record if an hour is missed or
the tool changes. `-Intervals 3` requests four hourly snapshots spanning the
three-hour scheduled operation window. A
stopped console or a long sleep can leave a gap; keep the journal and repeat
the timed gate as needed:

```powershell
$run = [guid]'11111111-2222-3333-4444-555555555555' # same as operation loop
$first = '2026-10-23T11:00:00Z' # replace with the same future UTC hour on all PCs
$label = 'pc-a' # distinct on each PC
$root = 'C:\PeerSync-Test\Share'
$reports = 'C:\Temp\PeerSync-soak'
New-Item -ItemType Directory -Force -Path $reports | Out-Null
& .\tools\Invoke-PeerSyncSoakCaptureLoop.ps1 -Root $root -RunId $run `
  -MachineLabel $label -FirstHourUtc $first -OutputDirectory $reports `
  -Intervals 3
if ($LASTEXITCODE -ne 0) { throw 'Hourly capture stopped; retain its journal.' }
```

For a manual capture or a focused check, invoke the underlying tool directly
with the same GUID and literal hour value on all PCs:

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
whose start times are more than 15 minutes apart. Capture all four
consecutive hourly checkpoints at hours 0, 1, 2 and 3. Stop random
operations at hour 3; the final read-only capture and report comparison
may finish afterward.

After collecting all machine snapshots and capture-loop journals, use the
packaged batch comparator to run every hourly comparison and the elapsed-time
audit in one command. Give each machine's snapshot directory and label in the
same order. Its summary, hourly reports and audit stay in an existing local
output directory outside the synced roots:

```powershell
$run = [guid]'11111111-2222-3333-4444-555555555555'
$first = '2026-10-23T11:00:00Z' # use the actual common first hour
& .\tools\Compare-PeerSyncSoakReports.ps1 -RunId $run -FirstHourUtc $first `
  -MachineLabels @('pc-a', 'pc-b', 'pc-c') `
  -InputDirectories @('C:\Temp\collected\pc-a', 'C:\Temp\collected\pc-b',
    'C:\Temp\collected\pc-c') -OutputDirectory 'C:\Temp\PeerSync-soak-results' `
  -Intervals 3 -MinimumHours 3 -MinimumMachines 3
if ($LASTEXITCODE -ne 0) { throw 'At least one hourly comparison or the audit failed.' }
```

The following direct audit command is useful when hourly comparison JSONs
were already produced separately:

```powershell
$reports = @(Get-ChildItem 'C:\Temp\PeerSync-soak\compare-*.json' |
  Sort-Object Name | Select-Object -ExpandProperty FullName)
& .\tools\Measure-PeerSyncTree.ps1 -Reports $reports -MinimumHours 3 `
  -RequiredMachines 3 -OutputFile 'C:\Temp\PeerSync-soak\soak-audit.json'
if ($LASTEXITCODE -ne 0) { throw 'Soak evidence is incomplete.' }
```

The audit requires a single run ID, the same tool and machine set, four
consecutive nominated UTC hours spanning three hours, captures starting
within 15 minutes of each nominated hour, and a passing comparison with
three or more peers at each hour. The three-hour limit applies to the active
operation window; the final read-only capture and report comparison may
finish afterward. A passing audit establishes only tree
agreement at those checkpoints. Inspect the operation, service and diagnostic
evidence for data loss, errors, resets and recovery,
and retain the full reports and environment record with the gate decision.
If a machine sleeps across a scheduled check, do not silently fill that gap;
record the gap and repeat the full timed gate when required.
