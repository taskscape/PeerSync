using PeerSync.Core.Ignore;
using System.Buffers.Binary;
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

/// <summary>Verified incremental selection, serialized with reference
/// maintenance but available during downloads. Dispatch block responses independently of a waiting index handler.
/// Index receive and affected needed rows commit atomically in the DB writer.</summary>
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

    /// <summary>Call only after transport authenticates the folder and sender.
    /// Shares the reference gate with receive and scheduled deletion checks.</summary>
    public Task<RemoteIndexRange> AnnounceIndexRangeAsync(byte[] sourceDevice, long targetSequence, CancellationToken cancellationToken = default)
        => AnnounceIndexRangeCoreAsync(sourceDevice, targetSequence, null, cancellationToken);

    internal Task<RemoteIndexRange> AnnounceAdmittedIndexRangeAsync(byte[] sourceDevice, long targetSequence, Action admission, CancellationToken token)
        => AnnounceIndexRangeCoreAsync(sourceDevice, targetSequence, admission, token);

    private async Task<RemoteIndexRange> AnnounceIndexRangeCoreAsync(byte[] sourceDevice, long targetSequence, Action? admission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceDevice); sourceDevice = sourceDevice.ToArray(); Guid session = Guid.NewGuid();
        try
        {
            using var operation = await FolderReferenceGate.AcquireAsync(index, cancellationToken).ConfigureAwait(false);
            admission?.Invoke();
            var range = admission is null
                ? await index.AnnounceRemoteIndexRangeAsync(sourceDevice, targetSequence, cancellationToken).ConfigureAwait(false)
                : await index.AnnounceRemoteIndexRangeWithAdmissionAsync(sourceDevice, targetSequence, admission, cancellationToken).ConfigureAwait(false);
            journal?.TryWrite(new(range.Complete ? "index.range_complete" : "index.range_pending", "index") { SessionId = session, ItemsVisited = 1 });
            return range;
        }
        catch (Exception error)
        {
            journal?.TryWrite(new("index.range_failed", "index") { SessionId = session, Level = DiagnosticLevel.Warning, ErrorCode = error.GetType().Name, HResult = error.HResult }); throw;
        }
    }

    public Task<RemoteIndexResult> ApplyIndexUpdateAsync(byte[] sourceDevice, long afterSequence, long lastSequence,
        IReadOnlyList<Envelope> records, CancellationToken cancellationToken = default)
        => ApplyIndexCoreAsync(sourceDevice, afterSequence, lastSequence, records, null, cancellationToken);

    internal Task<RemoteIndexResult> ApplyAdmittedIndexUpdateAsync(byte[] sourceDevice, long afterSequence, long lastSequence,
        IReadOnlyList<Envelope> records, Action admission, CancellationToken token)
        => ApplyIndexCoreAsync(sourceDevice, afterSequence, lastSequence, records, admission, token);

    private async Task<RemoteIndexResult> ApplyIndexCoreAsync(byte[] sourceDevice, long afterSequence, long lastSequence,
        IReadOnlyList<Envelope> records, Action? admission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(sourceDevice);
        sourceDevice = sourceDevice.ToArray();
        if (records.Count > 1000) throw new ArgumentException("Remote index batch exceeds 1000 records.", nameof(records));
        records = records.ToArray();
        Guid session = Guid.NewGuid();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var operation = await FolderReferenceGate.AcquireAsync(index, cancellationToken).ConfigureAwait(false);
            admission?.Invoke();
            var rows = new List<RemoteFileRow>(records.Count);
            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var view = VersionView.Open(record, keys, sourceDevice);
                rows.Add(new(sourceDevice.ToArray(), PathRules.Key(view.Path), view.Vector, record));
            }
            var result = admission is null
                ? await index.ApplyRemoteIndexAsync(sourceDevice, afterSequence, lastSequence, rows, ToNeededRow, cancellationToken).ConfigureAwait(false)
                : await index.ApplyRemoteIndexWithAdmissionAsync(sourceDevice, afterSequence, lastSequence, rows, ToNeededRow, admission, cancellationToken).ConfigureAwait(false);
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

    public async Task<int> RecomputeAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths); string[] requested = paths.ToArray();
        using var operation = await FolderReferenceGate.AcquireAsync(index, cancellationToken).ConfigureAwait(false);
        return await index.RecomputeNeededAsync(requested, ToNeededRow, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> RebuildAsync(CancellationToken cancellationToken = default)
    {
        using var operation = await FolderReferenceGate.AcquireAsync(index, cancellationToken).ConfigureAwait(false);
        var paths = index.LocalFiles.ReadAll().Select(r => r.Path).Where(PathRules.IsValid)
            .Concat(index.RemoteFiles.ReadAll().Select(r => r.PathKey)).Concat(index.Needed.ReadAll().Select(r => r.PathKey));
        return await index.RecomputeNeededAsync(paths, ToNeededRow, cancellationToken).ConfigureAwait(false);
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

    internal NeededRow? ToNeededRow(FolderPathSnapshot snapshot)
    {
        // Admission must not depend on a usable native scan cache. Otherwise a
        // withheld local row can suppress preservation while remote rows change.
        var originals = new List<VersionView>();
        if (snapshot.Local?.Record is { } local) originals.Add(VersionView.Open(local, keys, ownDevice));
        originals.AddRange(snapshot.Remote.Select(remote => VersionView.Open(remote.Record, keys, remote.DeviceId)));
        IReadOnlyList<CasePreservationSource> cases = [];
        if (originals.Count != 0)
        {
            var originalWinner = ConflictResolver.SelectWinner(originals);
            if (!ignore.IsIgnored(originalWinner.Path, originalWinner.Type == 1)) cases = CaseConflictPolicy.SelectSources(originals);
        }
        var decision = Decide(snapshot);
        if (!decision.RequiresApply)
        {
            if (cases.Count == 0) return null;
            var source = cases[0];
            return new(snapshot.PathKey, source.SourceDevice, 0)
            { PreservationAdmissionOnly = true, CasePreservations = cases };
        }
        var winner = decision.Winner!; var desired = decision.DesiredRecord!;
        using var hash = Blake3.Hasher.New(); hash.Update("PeerSync pull work v1"u8); hash.Update(desired.Body); hash.Update(desired.Signature); hash.Update(winner.SourceDevice);
        Span<byte> target = stackalloc byte[17]; target.Clear(); BinaryPrimitives.WriteInt64LittleEndian(target, snapshot.Local?.LocalSequence ?? 0);
        BinaryPrimitives.WriteInt64LittleEndian(target[8..], snapshot.Local?.ChangeFileTime ?? 0); target[16] = snapshot.Local?.ContentReady == true ? (byte)1 : (byte)0;
        hash.Update(target); if (snapshot.Local?.FileId is { } id) hash.Update(id);
        int stage = winner.Deleted ? winner.Type == 1 ? 3 : 2 : winner.Type == 1 ? 0 : 1;
        return new(snapshot.PathKey, winner.SourceDevice.ToArray(), 0)
        {
            Work = new(stage, winner.Size, winner.MtimeTicks, hash.Finalize().AsSpan().ToArray()),
            CasePreservations = cases
        };
    }

    private static bool ActualMatches(LocalFileRow? local, VersionView winner)
    {
        if (!ContentMatches(local, winner)) return false;
        if (local is null || winner.Deleted) return true;
        var body = winner.ExportBody();
        return (winner.Type == 1 || local.MtimeTicks == winner.MtimeTicks)
            && PathRules.Key(local.Path) == PathRules.Key(winner.Path) && PathSpelling.SameLeaf(local.Path, winner.Path)
            && (local.Attributes & (uint)(FileAttributes.ReadOnly | FileAttributes.Hidden)) == body.Attributes;
    }
    private static bool ContentMatches(LocalFileRow? local, VersionView winner) => winner.Deleted
        ? local is null || local.Deleted
        : local is { Deleted: false } && local.Type == winner.Type && local.Size == winner.Size
            && (winner.Type != 0 || local.FileHash is not null && local.FileHash.AsSpan().SequenceEqual(winner.FileHash));
}
