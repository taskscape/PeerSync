using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Settings;
using PeerSync.Core.Sync;
using PeerSync.Crypto;
using PeerSync.Engine.Archive;
using PeerSync.Engine.Native;
using PeerSync.Engine.Sync;
using PeerSync.Protocol.Signing;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Hashing;

internal sealed record DirectoryPublicationPass(IReadOnlyList<string> ChangedPaths, int Visited, int Deferred, bool More, bool RequiresScan)
{
    public bool RequiresNeededRebuild { get; init; }
}

// Bounded continuation of scanner-owned private templates. It never walks the
// native tree; each inventory was supplied by a complete scanner observation.
internal sealed class DirectoryPublicationWorker(string rootPath, FolderIndex index, FolderKeys keys, byte[] device,
    FolderSettings settings, TimeProvider clock, DiagnosticJournal? journal = null)
{
    private readonly byte[] source = device is { Length: 32 } ? device.ToArray() : throw new ArgumentException("Invalid publication source.", nameof(device));
    internal Action<string>? Checkpoint { get; init; }
    internal async Task<DirectoryPublicationPass> RunAsync(int maximumMembers = 1000, int maximumDirectories = 64, CancellationToken token = default)
    {
        if (maximumMembers is < 1 or > 1000 || maximumDirectories is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maximumMembers));
        using var gate = await FolderOperationGate.AcquireAsync(index, token).ConfigureAwait(false);
        var changed = new List<string>(); int remaining = maximumMembers, visited = 0, deferred = 0; bool scan = false, rebuild = false;
        foreach (var initial in index.ReadDueDirectoryPublications(clock.GetUtcNow().UtcTicks, maximumDirectories))
        {
            if (remaining == 0) break;
            token.ThrowIfCancellationRequested(); visited++; int offered = remaining; Guid session = initial.Version; string stage = "dependencies";
            var queued = initial;
            Stopwatch? timing = journal is null ? null : Stopwatch.StartNew();
            try
            {
                if (index.HasIncompleteDirectoryCaseCopy(queued.Path)) { stage = "case-copy-members"; await Retry(null).ConfigureAwait(false); continue; }
                if (index.HasPendingChildDirectoryPublication(queued.Path)) { await Retry(null).ConfigureAwait(false); continue; }
                stage = "membership"; long epoch = index.ReadDirectoryCaptureEpoch();
                if (queued.KeysEpoch != epoch)
                {
                    var inventory = index.ReadDirectoryChildInventory(queued.Path, token);
                    if (inventory.Count != queued.ExpectedChildren || !inventory.Hash.AsSpan().SequenceEqual(queued.ExpectedKeysHash))
                    { await Retry(null).ConfigureAwait(false); continue; }
                    Checkpoint?.Invoke("before-key-bind");
                    queued = await index.BindDirectoryPublicationKeysAsync(queued, inventory.Epoch, clock.GetUtcNow().UtcTicks, token).ConfigureAwait(false);
                }
                StageTime("prepare");
                stage = "capture"; var before = index.ReadDirectoryCapture(queued.Path);
                var capture = new DirectorySnapshotCapture(rootPath, index, keys, source, journal) { Checkpoint = Checkpoint };
                var draft = await capture.AdvancePublishedUnderGateAsync(queued.Path, remaining, token).ConfigureAwait(false);
                StageTime("capture", draft.Accepted);
                // Empty/completed roots also consume a scheduling unit, so one
                // pass is bounded even if no new member bytes were captured.
                long accepted = before is not null && before.InputHash.AsSpan().SequenceEqual(draft.InputHash) ? before.Accepted : 0;
                bool resumeRebase = before?.Hash is not null && before.InputEpoch == draft.InputEpoch;
                if (!resumeRebase) remaining -= (int)Math.Clamp(draft.Accepted - accepted, 1, remaining);
                if (draft.Hash is null)
                {
                    journal?.TryWrite(new("directory.publication_pending", "directory-publication") { SessionId = session, Level = DiagnosticLevel.Debug,
                        ItemsVisited = draft.Accepted, ItemsChanged = Math.Max(0, draft.Accepted - accepted) });
                    continue;
                }
                stage = "publication";
                var previous = index.LocalFiles.Find(queued.Path); var template = VersionView.Open(queued.Template, keys, source);
                if (queued.ReconstructionOnly && template.ExportBody().DirectorySnapshotHash.Length != 32)
                    throw new InvalidDataException("Reconstruction requires a signed snapshot root.");
                LocalFileRow? publication = null;
                bool identical = false;
                if (previous is { Type: 1, Deleted: false, Record: not null, Flags: LocalFileState.None })
                {
                    var prior = VersionView.Open(previous.Record, keys, source); var body = prior.ExportBody();
                    identical = prior.Path == queued.Path && prior.MtimeTicks == template.MtimeTicks && prior.Attributes == template.Attributes
                        && previous.Attributes == queued.Attributes && previous.FileId is not null && previous.FileId.AsSpan().SequenceEqual(queued.NativeId)
                        && body.DirectorySnapshotHash.Span.SequenceEqual(draft.Hash) && body.DirectorySnapshotLength == draft.Length;
                }
                if (!identical)
                {
                    if (!keys.CanWrite) { await Retry(null).ConfigureAwait(false); continue; }
                    if (queued.ReconstructionOnly)
                    {
                        if (remaining == 0) continue;
                        stage = "rebase";
                        DirectoryRebasePass proof;
                        try { proof = await DirectoryRebaseProof.AdvanceAsync(rootPath, index, keys, source, queued, draft, remaining, token).ConfigureAwait(false); }
                        catch { remaining = 0; throw; } // Failure may follow a fully persisted offered prefix.
                        StageTime("rebase", proof.Visited);
                        remaining -= proof.Visited;
                        journal?.TryWrite(new(proof.Complete ? "directory.rebase_complete" : "directory.rebase_pending", "directory-publication")
                        { SessionId = session, Level = DiagnosticLevel.Debug, ItemsVisited = proof.Visited, ItemsDeferred = proof.Waiting ? 1 : 0 });
                        if (!proof.Complete) { if (proof.Waiting) await Retry(null).ConfigureAwait(false); continue; }
                        Checkpoint?.Invoke("rebase-proven");
                    }
                    else if (template.ExportBody().DirectorySnapshotHash.Length != 0) { await Retry(null).ConfigureAwait(false); continue; }
                    stage = "parent-roots";
                    DirectoryRebasePass parents;
                    try { parents = await DirectoryParentRootProof.AdvanceAsync(rootPath, index, keys, source, queued, draft, remaining, clock, journal, token).ConfigureAwait(false); }
                    catch { remaining = 0; throw; }
                    StageTime("parent_roots", parents.Visited);
                    remaining -= parents.Visited;
                    journal?.TryWrite(new(parents.Complete ? "directory.parent_roots_complete" : "directory.parent_roots_pending", "directory-publication")
                    { SessionId = session, Level = DiagnosticLevel.Debug, ItemsVisited = parents.Visited, ItemsDeferred = parents.Waiting ? 1 : 0 });
                    if (!parents.Complete) { if (parents.Waiting) await Retry(null).ConfigureAwait(false); continue; }
                    var body = DirectoryPublicationClock.Template(index, keys, source, queued, clock.GetUtcNow().UtcTicks, journal);
                    body.DirectorySnapshotHash = ByteString.CopyFrom(draft.Hash); body.DirectorySnapshotLength = checked((uint)draft.Length!.Value);
                    var record = SignedRecordFactory.Create(body, keys);
                    publication = new(queued.Path, 1, 0, body.MtimeTicks, queued.Attributes, false, body.ChangedTicks, null,
                        body.Vv.ToDictionary(entry => entry.Device, entry => entry.Counter), record, queued.NativeId.ToArray());
                    if (previous is not null)
                    {
                        stage = "history"; using var tree = new SafeFileTree(rootPath);
                        scan |= await new SnapshotHistory(index, keys, source, settings, journal, clock).EnsureAsync(tree, previous, publication, null, token).ConfigureAwait(false);
                    }
                }
                if (remaining == offered) remaining--; // Completed captures still consume one scheduling unit.
                StageTime("publication");
                stage = "commit";
                var committed = await capture.CommitQueuedUnderGateAsync(queued, draft, publication, clock.GetUtcNow().UtcTicks, token).ConfigureAwait(false);
                StageTime("commit");
                if (committed.Changed)
                {
                    changed.Add(committed.Row.Path);
                    rebuild |= previous is { Type: 0, Record: not null };
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception or CryptographicException or SqliteException or InvalidOperationException)
            { await Retry(error).ConfigureAwait(false); }

            void StageTime(string phase, long members = 0)
            {
                if (timing is null) return;
                journal!.TryWrite(new("directory.publication_time_" + phase, "directory-publication")
                { SessionId = session, Level = DiagnosticLevel.Debug, ElapsedMilliseconds = timing.Elapsed.TotalMilliseconds,
                    ItemsVisited = members });
                timing.Restart();
            }

            async Task Retry(Exception? error)
            {
                token.ThrowIfCancellationRequested(); deferred++;
                var retry = await index.RetryDirectoryPublicationAsync(queued, clock.GetUtcNow().UtcTicks, token).ConfigureAwait(false);
                journal?.TryWrite(new("directory.publication_retry", "directory-publication") { SessionId = session, Level = DiagnosticLevel.Debug,
                    ErrorCode = stage + "." + (error?.GetType().Name ?? "pending"), Exception = error, HResult = error?.HResult,
                    NativeError = error switch { SqliteException sqlite => sqlite.SqliteExtendedErrorCode, Win32Exception native => native.NativeErrorCode, _ => null },
                    ItemsDeferred = 1, RetryInMilliseconds = (int)Math.Clamp((retry.NextTicks!.Value - retry.FailedTicks!.Value) / TimeSpan.TicksPerMillisecond, 0, 600000) });
            }
        }
        var root = index.Meta.Find("_root-identity")?.Value;
        long now = clock.GetUtcNow().UtcTicks;
        bool more = root is { Length: 24 } && index.ReadNextDirectoryPublicationTicks(root, now) is long next && next <= now;
        journal?.TryWrite(new("directory.publication_pass", "directory-publication") { SessionId = Guid.NewGuid(), Level = DiagnosticLevel.Debug,
            ItemsVisited = visited, ItemsChanged = changed.Count, ItemsDeferred = deferred });
        return new(changed.AsReadOnly(), visited, deferred, more, scan) { RequiresNeededRebuild = rebuild };
    }
}
