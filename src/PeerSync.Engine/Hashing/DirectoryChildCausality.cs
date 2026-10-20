using PeerSync.Core.Paths;
using PeerSync.Core.Sync;
using PeerSync.Crypto;
using PeerSync.Engine.Sync;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Hashing;

internal static class DirectoryChildCausality
{
    internal static async Task<bool> IncludesAsync(DirectorySnapshotBlocks blocks, FolderIndex index, FolderKeys keys, byte[] own,
        string parent, DirectoryGraphMember member, CancellationToken token)
    {
        // A checked dominating direct directory/kind/deletion covers its old
        // descendants; the input witness keeps that decision valid on resume.
        if (ApplyState.Parent(member.PathKey) != PathRules.Key(parent)) return true;
        var prior = await blocks.ReadMemberAsync(parent, new(member.RecordHash, member.RecordLength),
            (_, _, _) => throw new IOException("Parent original metadata is unavailable."), token).ConfigureAwait(false);
        if (PathRules.Key(prior.Path) != member.PathKey) throw new InvalidDataException("Parent original member path changed.");
        var row = index.LocalFiles.Find(member.PathKey);
        if (row?.Record is null || row.Flags != LocalFileState.None || (!row.Deleted && row.Type == 0 && !row.ContentReady)) return false;
        var current = VersionView.Open(row.Record, keys, own);
        if (PathRules.Key(current.Path) != member.PathKey || current.Type != row.Type || current.Deleted != row.Deleted
            || (!current.Deleted && current.Type == 0 && (current.Size != row.Size || row.FileHash is null || !current.FileHash.SequenceEqual(row.FileHash))))
            throw new InvalidDataException("Parent signed child does not match its local row.");
        var order = VersionVector.Compare(current.Vector, prior.Vector);
        return order == VvOrder.Dominates || order == VvOrder.Equal && current.Record.Body.SequenceEqual(prior.Record.Body)
            && current.Record.Signature.SequenceEqual(prior.Record.Signature);
    }
}
