using System.Collections.ObjectModel;
using PeerSync.Core.Paths;
using PeerSync.Core.Settings;
using PeerSync.Core.Sync;
using PeerSync.Crypto;
using PeerSync.Protocol.Records;
using PeerSync.Protocol.Signing;
using Envelope = PeerSync.Core.Sync.SignedRecord;

namespace PeerSync.Engine.Sync;

public enum Outcome { Ignore, ApplyRemote, KeepLocal, MergeVectorsOnly }
public enum LoserFate { None, Archive, ConflictCopy }
public sealed record Resolution(Outcome Outcome, VersionView Winner, LoserFate LocalLoserFate,
    IReadOnlyDictionary<ulong, ulong> Vector);

public static class ConflictResolver
{
    public static Resolution Resolve(VersionView local, VersionView remote, FolderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        var winner = SelectWinner([local, remote]);
        var vector = new ReadOnlyDictionary<ulong, ulong>(VersionVector.Merge(local.Vector, remote.Vector));
        VvOrder order = VersionVector.Compare(remote.Vector, local.Vector);
        if (order == VvOrder.DominatedBy || (order == VvOrder.Equal && ReferenceEquals(winner, local)))
            return new(Outcome.Ignore, local, LoserFate.None, vector);
        if (order == VvOrder.Dominates)
            return new(Outcome.ApplyRemote, remote, SameContent(local, remote) ? LoserFate.None : FateOf(local, settings), vector);
        if (SameContent(local, remote)) return new(Outcome.MergeVectorsOnly, winner, LoserFate.None, vector);
        return ReferenceEquals(winner, local)
            ? new(Outcome.KeepLocal, local, LoserFate.None, vector)
            : new(Outcome.ApplyRemote, remote, FateOf(local, settings), vector);
    }

    /// <summary>Choose only among causally maximal records, independent of enumeration order.</summary>
    public static VersionView SelectWinner(IReadOnlyList<VersionView> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0) throw new ArgumentException("At least one version is required.", nameof(candidates));
        string key = PathRules.Key(candidates[0].Path);
        foreach (var candidate in candidates)
            if (PathRules.Key(candidate.Path) != key) throw new ArgumentException("Versions must refer to one path key.", nameof(candidates));
        VersionView? winner = null;
        foreach (var candidate in candidates)
        {
            if (candidates.Any(other => VersionVector.Compare(other.Vector, candidate.Vector) == VvOrder.Dominates)) continue;
            if (winner is null || CompareStable(candidate, winner) > 0) winner = candidate;
        }
        return winner ?? throw new InvalidDataException("No causally maximal version.");
    }

    public static IReadOnlyDictionary<ulong, ulong> MergeVectors(IEnumerable<VersionView> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var merged = new Dictionary<ulong, ulong>();
        foreach (var candidate in candidates) merged = VersionVector.Merge(merged, candidate.Vector);
        return new ReadOnlyDictionary<ulong, ulong>(merged);
    }

    /// <summary>RO retains the exact original. RW signs a new envelope only when the vector changes.</summary>
    public static Envelope MergeRecord(VersionView winner, IReadOnlyDictionary<ulong, ulong> vector, FolderKeys keys)
    {
        ArgumentNullException.ThrowIfNull(winner);
        ArgumentNullException.ThrowIfNull(vector);
        ArgumentNullException.ThrowIfNull(keys);
        if (VersionVector.Compare(vector, winner.Vector) is VvOrder.Concurrent or VvOrder.DominatedBy)
            throw new ArgumentException("A merged vector must include the winner.", nameof(vector));
        if (!keys.CanWrite || VersionVector.Compare(vector, winner.Vector) == VvOrder.Equal) return winner.Record.WithLocalSequence(0);
        var body = winner.ExportBody();
        var entries = body.Vv.ToDictionary(entry => entry.Device);
        body.Vv.Clear(); body.Vv.AddRange(vector.Where(v => v.Value != 0).OrderBy(v => v.Key)
            .Select(v =>
            {
                var entry = entries.GetValueOrDefault(v.Key) ?? new VvEntry { Device = v.Key };
                entry.Counter = v.Value; return entry;
            }));
        return SignedRecordFactory.Create(body, keys);
    }

    public static bool SameContent(VersionView first, VersionView second) => first.Deleted == second.Deleted
        && (first.Deleted || (first.Type == second.Type && first.Size == second.Size && first.FileHash.SequenceEqual(second.FileHash)));

    public static LoserFate FateOf(VersionView loser, FolderSettings settings) => loser.Deleted ? LoserFate.None
        : settings.KeepBothCopies || loser.Size > settings.ArchiveMaxFileBytes ? LoserFate.ConflictCopy : LoserFate.Archive;

    private static int CompareStable(VersionView first, VersionView second)
    {
        int time = first.EffectiveChangeTicks.CompareTo(second.EffectiveChangeTicks);
        if (time != 0) return time;
        int author = first.ModifiedBy.SequenceCompareTo(second.ModifiedBy);
        return author != 0 ? author : first.Record.Body.SequenceCompareTo(second.Record.Body);
    }
}
