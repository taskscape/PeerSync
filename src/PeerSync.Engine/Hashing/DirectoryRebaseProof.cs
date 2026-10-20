using System.Buffers.Binary;
using Google.Protobuf;
using PeerSync.Core.Paths;
using PeerSync.Core.Sync;
using PeerSync.Crypto;
using PeerSync.Engine.Sync;
using PeerSync.Protocol.Records;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Hashing;

internal sealed record DirectoryRebasePass(bool Complete, bool Waiting, int Visited);

internal static class DirectoryRebaseProof
{
    internal static FileRecordBody CreateTemplate(VersionView original, byte[] own, long changedTicks, string path, uint attributes)
    {
        if (PathRules.Key(original.Path) != PathRules.Key(path)) throw new InvalidDataException("Rebase metadata changed its canonical directory.");
        var body = original.ExportBody(); body.DirectorySnapshotHash = ByteString.Empty; body.DirectorySnapshotLength = 0;
        body.Path = path; body.Attributes = attributes & (uint)(FileAttributes.ReadOnly | FileAttributes.Hidden);
        body.ModifiedBy = ByteString.CopyFrom(own); body.ChangedTicks = changedTicks;
        var vector = VersionVector.Increment(original.Vector, BinaryPrimitives.ReadUInt64LittleEndian(own));
        body.Vv.Clear(); body.Vv.AddRange(vector.OrderBy(entry => entry.Key).Select(entry => new VvEntry { Device = entry.Key, Counter = entry.Value }));
        return body;
    }
    // Caller owns the operation gate. Original graph metadata is cache-only;
    // availability work runs separately and no missing member implies deletion.
    internal static async Task<DirectoryRebasePass> AdvanceAsync(string rootPath, FolderIndex index, FolderKeys keys, byte[] own,
        DirectoryPublication queued, DirectoryCaptureState draft, int limit, CancellationToken token)
    {
        if (!keys.CanWrite || limit < 1) return new(false, false, 0);
        var original = VersionView.Open(queued.Template, keys, own); byte[] graphId = CaseConflictPolicy.SourceId(original);
        var graph = index.ReadDirectoryGraph(graphId);
        if (graph is null || !graph.Complete || graph.Retiring) return new(false, true, 1);
        var proof = await index.BeginDirectoryRebaseAsync(queued, draft, graphId, token).ConfigureAwait(false);
        if (proof.Complete) return new(true, false, 1);
        using var blocks = new DirectorySnapshotBlocks(rootPath, index, keys, own);
        var verified = new List<DirectoryGraphMember>(); int visited = 0; bool waiting = false;
        foreach (var member in index.ReadDirectoryGraphMemberPage(graphId, proof.Cursor, limit))
        {
            token.ThrowIfCancellationRequested(); visited++;
            if (ApplyState.Parent(member.PathKey) == PathRules.Key(queued.Path))
            {
                var prior = await blocks.ReadMemberAsync(queued.Path, new(member.RecordHash, member.RecordLength),
                    (_, _, _) => throw new IOException("Rebase original metadata is unavailable."), token).ConfigureAwait(false);
                if (PathRules.Key(prior.Path) != member.PathKey) throw new InvalidDataException("Rebase original member path changed.");
                var row = index.LocalFiles.Find(member.PathKey);
                if (row?.Record is null || row.Flags != LocalFileState.None || (!row.Deleted && row.Type == 0 && !row.ContentReady)) { waiting = true; break; }
                var current = VersionView.Open(row.Record, keys, own);
                if (PathRules.Key(current.Path) != member.PathKey || current.Type != row.Type || current.Deleted != row.Deleted
                    || (!current.Deleted && current.Type == 0 && (current.Size != row.Size || row.FileHash is null || !current.FileHash.SequenceEqual(row.FileHash))))
                    throw new InvalidDataException("Rebase signed child does not match its local row.");
                var order = VersionVector.Compare(current.Vector, prior.Vector);
                if (order != VvOrder.Dominates && (order != VvOrder.Equal || !current.Record.Body.SequenceEqual(prior.Record.Body)
                    || !current.Record.Signature.SequenceEqual(prior.Record.Signature))) { waiting = true; break; }
            }
            verified.Add(member);
        }
        await blocks.FlushAsync(token).ConfigureAwait(false);
        var advanced = await index.AdvanceDirectoryRebaseAsync(queued, draft, proof, verified, token).ConfigureAwait(false);
        return new(advanced.Complete, waiting, Math.Max(1, visited));
    }
}
