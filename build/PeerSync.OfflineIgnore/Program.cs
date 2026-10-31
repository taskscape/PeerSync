using System.Buffers.Binary;
using System.Security.Principal;
using System.Text.Json;
using PeerSync.Core.Ignore;
using PeerSync.Storage.FolderDb;
using PeerSync.Storage.StateDb;

if (args.Length is not (3 or 4) || args.Length == 4 && args[3] != "--apply")
{
    Console.Error.WriteLine("Usage: PeerSync.OfflineIgnore <state-db> <folder-root> <run-guid-N> [--apply]");
    return 2;
}

if (!Guid.TryParseExact(args[2], "N", out Guid run) ||
    !Path.IsPathFullyQualified(args[0]) || !Path.IsPathFullyQualified(args[1]))
{
    Console.Error.WriteLine("State path and folder root must be absolute, and run ID must be a GUID in N format.");
    return 2;
}

string statePath = Path.GetFullPath(args[0]);
string folderRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[1]));
if (!File.Exists(statePath) || !Directory.Exists(folderRoot))
    throw new IOException("Existing state database and folder root are required.");

// Callers stop the installed service on every participating PC first. Refuse
// even a dry run when the local service process could own the state database.
if (System.Diagnostics.Process.GetProcessesByName("PeerSync.Service").Length != 0)
    throw new InvalidOperationException("Stop the installed PeerSync service before changing offline folder policy.");

string rule = $"/PeerSync-GateCorpus/{run:N}/";
string pathToExclude = $"PeerSync-GateCorpus/{run:N}/example.bin";
await using var state = await StateStore.OpenAsync(statePath);
FolderDescriptor folder = state.ListFolders().Single(item =>
    string.Equals(item.Path, folderRoot, StringComparison.OrdinalIgnoreCase));
string indexPath = Path.Combine(Path.GetDirectoryName(statePath)!, "folders", Convert.ToHexString(folder.Id) + ".db");
if (!File.Exists(indexPath)) throw new IOException("Expected existing folder index is missing.");
await using var index = await FolderIndex.OpenAsync(indexPath);
byte[] rootIdentity = index.Meta.Find("_root-identity")?.Value
    ?? throw new IOException("Folder index has no bound root identity.");
int runRows = 0, runLiveRows = 0;
string runPrefix = $"PEERSYNC-GATECORPUS/{run:N}/".ToUpperInvariant();
var pending = index.ReadApplyIntents().ToArray();
int runIntents = pending.Count(item => item.PathKey.StartsWith(runPrefix, StringComparison.Ordinal));
bool allPendingRunHistory = pending.All(item => item.PathKey.StartsWith(runPrefix, StringComparison.Ordinal)
    && item.Payload.Length >= 4 && BinaryPrimitives.ReadInt32LittleEndian(item.Payload) == 4);
bool allPendingIndexed = pending.All(item => index.LocalFiles.Find(item.PathKey) is { Deleted: false, Size: > 0 });
long pendingIndexedBytes = pending.Sum(item => index.LocalFiles.Find(item.PathKey)?.Size ?? 0);
bool unaccountedHistory = index.HasUnaccountedHistory(rootIdentity);
long freeBytes = new DriveInfo(Path.GetPathRoot(folderRoot)!).AvailableFreeSpace;
if (args.Length == 4 && (unaccountedHistory || !allPendingRunHistory || !allPendingIndexed || pending.Length > 1
    || pendingIndexedBytes > 2L * 1024 * 1024 * 1024 || freeBytes < pendingIndexedBytes + 4L * 1024 * 1024 * 1024))
    throw new InvalidOperationException("Offline policy change requires no unaccounted history, at most one bounded run-history recovery intent, and disk headroom.");
foreach (var row in index.LocalFiles.ReadAll())
{
    if (!row.Path.ToUpperInvariant().StartsWith(runPrefix, StringComparison.Ordinal)) continue;
    runRows++;
    if (!row.Deleted) runLiveRows++;
}
string prior = folder.Settings.IgnoreRules;
string updated = prior.Split('\n', StringSplitOptions.TrimEntries).Contains(rule, StringComparer.OrdinalIgnoreCase)
    ? prior : prior.TrimEnd('\r', '\n') + (prior.Length == 0 ? "" : "\n") + rule;
var matcher = new IgnoreMatcher(updated);
if (!matcher.IsIgnored(pathToExclude) || matcher.IsIgnored("PeerSync-GateCorpus/another-run/example.bin"))
    throw new InvalidOperationException("The proposed rule does not isolate the exact run.");

if (args.Length == 4 && updated != prior)
{
    using var identity = WindowsIdentity.GetCurrent();
    string actorSid = identity.User?.Value ?? throw new InvalidOperationException("Missing Windows actor SID.");
    await state.UpdateFolderAsync(folder.Id, folder.Settings with { IgnoreRules = updated }, folder.Paused, actorSid);
}

FolderDescriptor saved = state.ListFolders().Single(item => folder.Id.SequenceEqual(item.Id));
if (args.Length == 4 && saved.Settings.IgnoreRules != updated)
    throw new IOException("Persisted ignore rule did not match the requested policy.");
Console.WriteLine(JsonSerializer.Serialize(new
{
    schema = "peersync-offline-ignore-v1",
    folder = saved.Path,
    run = run.ToString("N"),
    rule,
    wasPaused = folder.Paused,
    paused = saved.Paused,
    changed = updated != prior && args.Length == 4,
    applied = args.Length == 4,
    exactRunIgnored = new IgnoreMatcher(saved.Settings.IgnoreRules).IsIgnored(pathToExclude),
    runRows,
    runLiveRows,
    pendingIntents = pending.Length,
    runIntents,
    allPendingRunHistory,
    allPendingIndexed,
    pendingIndexedBytes,
    freeBytes,
    unaccountedHistory
}));
return 0;
