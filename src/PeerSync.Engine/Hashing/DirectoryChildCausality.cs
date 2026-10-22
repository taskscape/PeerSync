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
        if (order == VvOrder.Dominates || order == VvOrder.Equal && current.Record.Body.SequenceEqual(prior.Record.Body)
            && current.Record.Signature.SequenceEqual(prior.Record.Signature)) return true;

        // A case-clash copy is deliberately outside the canonical parent path.
        // Its durable member ledger binds the exact authenticated graph member
        // to the installed native identity, so it can cover the old child while
        // the canonical path contains a concurrent winner.
        return IncludesPreservedCaseMember(index, keys, own, parent, member, prior);
    }

    private static bool IncludesPreservedCaseMember(FolderIndex index, FolderKeys keys, byte[] own, string parent,
        DirectoryGraphMember member, VersionView prior)
    {
        foreach (var copy in index.ReadCompletedDirectoryCaseMemberCopies(prior.Path))
        {
            if (!member.PathKey.StartsWith(copy.SourcePathKey + '/', StringComparison.Ordinal)) continue;
            var preserved = copy.Member;
            if (preserved.InstalledFileId is not { Length: 16 } fileId
                || preserved.RecordLength != member.RecordLength || !preserved.RecordHash.AsSpan().SequenceEqual(member.RecordHash)
                || preserved.Type != prior.Type || !preserved.MemberSourceId.AsSpan().SequenceEqual(CaseConflictPolicy.SourceId(prior))) continue;

            int rootSegments = copy.SourcePathKey.Count('/') + 1;
            string relative = string.Join('/', prior.Path.Split('/').Skip(rootSegments));
            string expectedDestination = PathRules.Key(copy.DestinationRoot + '/' + relative);
            if (PathRules.Key(preserved.DestinationPath) != expectedDestination) continue;

            var row = index.LocalFiles.Find(preserved.DestinationPath);
            if (row is null || row.Record is null || row.Flags != LocalFileState.None || row.Deleted || row.Type != preserved.Type
                || row.FileId is null || !row.FileId.AsSpan().SequenceEqual(fileId)
                || (row.Type == 0 && (!row.ContentReady || row.Size != prior.Size || row.FileHash is null || !row.FileHash.AsSpan().SequenceEqual(prior.FileHash))))
                continue;

            var installed = VersionView.Open(row.Record, keys, own);
            if (PathRules.Key(installed.Path) != PathRules.Key(preserved.DestinationPath) || installed.Type != prior.Type || installed.Deleted
                || (installed.Type == 0 && (installed.Size != prior.Size || !installed.FileHash.SequenceEqual(prior.FileHash)))) continue;
            return true;
        }
        return false;
    }
}
