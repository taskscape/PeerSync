using PeerSync.Core.Ignore;
using System.Diagnostics;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Paths;
using PeerSync.Core.Settings;
using PeerSync.Core.Sync;
using PeerSync.Crypto;
using PeerSync.Storage.FolderDb;
using Envelope = PeerSync.Core.Sync.SignedRecord;

namespace PeerSync.Engine.Sync;

public sealed record NeededDecision(VersionView? Winner, Envelope? DesiredRecord,
    IReadOnlyDictionary<ulong, ulong> Vector, bool RequiresApply, bool RequiresContent,
    LoserFate LocalLoserFate, string? BlockedReason = null);

/// <summary>Verified, incremental selection. The caller serializes filesystem apply/scans;
/// index receive and affected needed rows are committed by the database writer atomically.</summary>
public sealed class NeededSet
{
    private readonly FolderIndex index;
    private readonly FolderKeys keys;
    private readonly byte[] ownDevice;
    private readonly FolderSettings settings;
    private readonly IgnoreMatcher ignore;
    private readonly DiagnosticJournal? journal;

    public NeededSet(FolderIndex index, FolderKeys keys, byte[] ownDevice, FolderSettings? settings = null,
        IgnoreMatcher? ignore = null, DiagnosticJournal? journal = null)
    {
        ArgumentNullException.ThrowIfNull(index); ArgumentNullException.ThrowIfNull(keys); ArgumentNullException.ThrowIfNull(ownDevice);
        if (ownDevice.Length != 32) throw new ArgumentException("Device identifier must contain 32 bytes.", nameof(ownDevice));
        this.index = index; this.keys = keys; this.ownDevice = ownDevice.ToArray();
        this.settings = FolderSettings.FromJson((settings ?? new()).ToJson());
        this.ignore = ignore ?? new();
        this.journal = journal;
    }

    public async Task<RemoteIndexResult> ApplyIndexUpdateAsync(byte[] sourceDevice, long afterSequence, long lastSequence,
        IReadOnlyList<Envelope> records, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(sourceDevice);
        sourceDevice = sourceDevice.ToArray();
        if (records.Count > 1000) throw new ArgumentException("Remote index batch exceeds 1000 records.", nameof(records));
        Guid session = Guid.NewGuid();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var rows = new List<RemoteFileRow>(records.Count);
            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var view = VersionView.Open(record, keys, sourceDevice);
                rows.Add(new(sourceDevice.ToArray(), PathRules.Key(view.Path), view.Vector, record));
            }
            var result = await index.ApplyRemoteIndexAsync(sourceDevice, afterSequence, lastSequence, rows, ToNeededRow, cancellationToken).ConfigureAwait(false);
            journal?.TryWrite(new(result.Applied ? "index.receive_complete" : "index.replay", "index")
            { SessionId = session, ItemsVisited = records.Count, ItemsChanged = result.AffectedPaths, ElapsedMilliseconds = stopwatch.ElapsedMilliseconds });
            return result;
        }
        catch (Exception error)
        {
            journal?.TryWrite(new("index.receive_failed", "index") { SessionId = session, Level = DiagnosticLevel.Warning,
                ErrorCode = error.GetType().Name, HResult = error.HResult, ItemsVisited = records.Count, ElapsedMilliseconds = stopwatch.ElapsedMilliseconds });
            throw;
        }
    }

    public Task<int> RecomputeAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default) =>
        index.RecomputeNeededAsync(paths, ToNeededRow, cancellationToken);

    public Task<int> RebuildAsync(CancellationToken cancellationToken = default)
    {
        var paths = index.LocalFiles.ReadAll().Select(r => r.Path).Where(PathRules.IsValid)
            .Concat(index.RemoteFiles.ReadAll().Select(r => r.PathKey)).Concat(index.Needed.ReadAll().Select(r => r.PathKey));
        return RecomputeAsync(paths, cancellationToken);
    }

    /// <summary>Use a current transaction snapshot when making a decision that will be committed.</summary>
    public NeededDecision Decide(FolderPathSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var empty = new Dictionary<ulong, ulong>();
        if (snapshot.Local is { Flags: not LocalFileState.None })
            return new(null, null, empty, false, false, LoserFate.None, "local-scan-pending-or-excluded");
        var candidates = new List<VersionView>();
        if (snapshot.Local?.Record is { } localRecord)
        {
            var local = VersionView.Open(localRecord, keys, ownDevice);
            if (keys.CanWrite && VersionVector.Compare(local.Vector, snapshot.Local.Vector) != VvOrder.Equal)
                throw new InvalidDataException("RW cached vector differs from its signed version.");
            if (!keys.CanWrite && VersionVector.Compare(snapshot.Local.Vector, local.Vector) is VvOrder.Concurrent or VvOrder.DominatedBy)
                throw new InvalidDataException("Cached vector does not include its signed version.");
            candidates.Add(local);
        }
        foreach (var remote in snapshot.Remote.OrderBy(r => Convert.ToHexString(r.DeviceId), StringComparer.Ordinal))
        {
            var view = VersionView.Open(remote.Record, keys, remote.DeviceId);
            if (VersionVector.Compare(view.Vector, remote.Vector) != VvOrder.Equal)
                throw new InvalidDataException("Remote cached vector differs from its signed version.");
            candidates.Add(view);
        }
        if (candidates.Count == 0) return new(null, null, empty, false, false, LoserFate.None);
        var winner = ConflictResolver.SelectWinner(candidates);
        if (PathRules.Key(winner.Path) != snapshot.PathKey) throw new InvalidDataException("Record does not match the stored path key.");
        if (winner.Type == 2 || ignore.IsIgnored(winner.Path, winner.Type == 1))
            return new(winner, null, empty, false, false, LoserFate.None, winner.Type == 2 ? "unsupported-type" : "ignored");
        var vector = ConflictResolver.MergeVectors(candidates);
        if (snapshot.Local is { } known) vector = VersionVector.Merge(vector, known.Vector);
        var desired = ConflictResolver.MergeRecord(winner, vector, keys);
        bool matches = ActualMatches(snapshot.Local, winner);
        bool current = matches && snapshot.Local?.Record is { } actualRecord && actualRecord.Body.SequenceEqual(desired.Body)
            && actualRecord.Signature.SequenceEqual(desired.Signature) && VersionVector.Compare(snapshot.Local.Vector, vector) == VvOrder.Equal;
        bool sameContent = ContentMatches(snapshot.Local, winner);
        LoserFate fate = snapshot.Local is { Deleted: false } loser && !sameContent
            ? settings.KeepBothCopies || loser.Size > settings.ArchiveMaxFileBytes ? LoserFate.ConflictCopy : LoserFate.Archive
            : LoserFate.None;
        bool blocked = !keys.CanWrite && snapshot.Local is { Record: null } && !matches && !settings.OverwriteLocalChanges;
        return new(winner, desired, vector, !current, !sameContent && !winner.Deleted && winner.Type == 0,
            fate, blocked ? "read-only-local-change" : null);
    }

    private NeededRow? ToNeededRow(FolderPathSnapshot snapshot)
    {
        var decision = Decide(snapshot);
        return decision.RequiresApply ? new(snapshot.PathKey, decision.Winner!.SourceDevice.ToArray(), 0) : null;
    }

    private static bool ActualMatches(LocalFileRow? local, VersionView winner)
    {
        if (!ContentMatches(local, winner)) return false;
        if (local is null || winner.Deleted) return true;
        var body = winner.ExportBody();
        return (winner.Type == 1 || local.MtimeTicks == winner.MtimeTicks) && local.Path == winner.Path
            && (local.Attributes & (uint)(FileAttributes.ReadOnly | FileAttributes.Hidden)) == body.Attributes;
    }
    private static bool ContentMatches(LocalFileRow? local, VersionView winner) => winner.Deleted
        ? local is null || local.Deleted
        : local is { Deleted: false } && local.Type == winner.Type && local.Size == winner.Size
            && (winner.Type != 0 || local.FileHash is not null && local.FileHash.AsSpan().SequenceEqual(winner.FileHash));
}
