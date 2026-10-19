using PeerSync.Core.Paths;
using PeerSync.Core.Sync;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Sync;

/// <summary>Pure policy over authenticated original envelopes. No path rewriting,
/// filesystem mutation or vector merge occurs here.</summary>
public static class CaseConflictPolicy
{
    public static byte[] SourceId(VersionView version)
    {
        ArgumentNullException.ThrowIfNull(version);
        using var hash = Blake3.Hasher.New(); hash.Update("PeerSync case source v1"u8);
        hash.Update(version.Record.Body); hash.Update(version.Record.Signature);
        return hash.Finalize().AsSpan().ToArray();
    }

    public static IReadOnlyList<CasePreservationSource> SelectSources(IReadOnlyList<VersionView> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0) return Array.Empty<CasePreservationSource>();
        var winner = ConflictResolver.SelectWinner(candidates);
        if (winner.Deleted || winner.Type > 1) return Array.Empty<CasePreservationSource>();
        var sources = new Dictionary<string, CasePreservationSource>(StringComparer.Ordinal);
        foreach (var version in candidates)
        {
            if (version.Deleted || version.Type > 1 || PathSpelling.SameLeaf(version.Path, winner.Path)
                || candidates.Any(other => VersionVector.Compare(other.Vector, version.Vector) == VvOrder.Dominates)) continue;
            byte[] id = SourceId(version);
            sources.TryAdd(Convert.ToHexString(id), new(id, version.SourceDevice.ToArray(), version.Path, version.Record));
        }
        return Array.AsReadOnly(sources.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value).ToArray());
    }
}
