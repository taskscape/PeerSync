using Google.Protobuf;
using PeerSync.Core.Diagnostics;
using PeerSync.Core.Paths;
using PeerSync.Core.Sync;
using PeerSync.Crypto;
using PeerSync.Engine.Sync;
using PeerSync.Protocol.Records;
using PeerSync.Storage.FolderDb;

namespace PeerSync.Engine.Hashing;

internal static class DirectoryParentRootProof
{
    internal static async Task<DirectoryRebasePass> AdvanceAsync(string rootPath, FolderIndex index, FolderKeys keys, byte[] own,
        DirectoryPublication queued, DirectoryCaptureState draft, int limit, TimeProvider clock, DiagnosticJournal? journal, CancellationToken token)
    {
        var row = index.LocalFiles.Find(queued.Path);
        if (row is not { Type: 1, Deleted: false, Record: not null }) return new(true, false, 0);
        var original = VersionView.Open(row.Record, keys, own);
        if (original.ExportBody().DirectorySnapshotHash.Length != 32) return new(true, false, 0);
        if (limit < 1) return new(false, false, 0);
        if (VersionVector.Compare(original.Vector, row.Vector) != VvOrder.Equal) throw new InvalidDataException("Parent cached vector differs from its signed original.");
        var metadata = ClassOf(original); byte[] metadataHash = ClassHash(metadata); int visited = 0;
        DirectoryParentPreparation prepared;
        using (await FolderReferenceGate.AcquireAsync(index, token).ConfigureAwait(false))
            prepared = await index.BeginDirectoryParentProofAsync(queued, draft, DirectoryMetadataSources.FromVersion(original)!, metadataHash,
                clock.GetUtcNow().UtcTicks, limit, token).ConfigureAwait(false);
        visited += prepared.ResetWork; var proof = prepared.Proof;
        if (proof.Resetting || visited >= limit) return new(false, false, Math.Max(1, visited));
        if (!proof.DiscoveryComplete)
        {
            var page = index.ReadDirectoryParentPage(proof, Math.Min(32, limit - visited)); var accepted = new List<DirectoryMetadataSource>();
            foreach (var candidate in page.Rows)
            {
                token.ThrowIfCancellationRequested(); var version = VersionView.Open(candidate.Record, keys, candidate.DeviceId);
                if (PathRules.Key(version.Path) != proof.PathKey || VersionVector.Compare(version.Vector, candidate.Vector) != VvOrder.Equal)
                    throw new InvalidDataException("Parent candidate differs from its indexed original.");
                if (version.Type == 1 && !version.Deleted && version.ExportBody().DirectorySnapshotHash.Length == 32 && metadata.Equals(ClassOf(version)))
                    accepted.Add(DirectoryMetadataSources.FromVersion(version)!);
            }
            using (await FolderReferenceGate.AcquireAsync(index, token).ConfigureAwait(false))
                proof = await index.AdvanceDirectoryParentDiscoveryAsync(queued, draft, proof, page, accepted, clock.GetUtcNow().UtcTicks, token).ConfigureAwait(false);
            visited += Math.Max(1, page.Rows.Count);
            if (!proof.DiscoveryComplete || visited >= limit) return new(false, false, visited);
        }
        while (visited < limit)
        {
            var root = index.ReadNextDirectoryParentRoot(proof); if (root is null) return new(true, false, Math.Max(1, visited));
            var source = VersionView.Open(root.Source.Record, keys, root.Source.RouteHint); var expected = DirectoryMetadataSources.FromVersion(source);
            if (expected is null || expected.Path != root.Source.Path || expected.Length != root.Source.Length || !expected.Hash.AsSpan().SequenceEqual(root.Source.Hash)
                || !expected.SourceId.AsSpan().SequenceEqual(root.Source.SourceId) || !metadata.Equals(ClassOf(source)))
                throw new InvalidDataException("Parent representative changed its authenticated class.");
            var graph = index.ReadDirectoryGraph(root.Source.SourceId);
            if (graph is null || !graph.Complete || graph.Retiring)
            {
                var advanced = await new DirectorySnapshotGraph(rootPath, index, keys, root.Source.RouteHint, journal)
                    .AdvanceUnderGateAsync(root.Source.Record, (_, _, _) => throw new IOException("Recover parent graph metadata before publication."), limit - visited, token).ConfigureAwait(false);
                visited += Math.Max(1, advanced.Processed);
                if (!advanced.Complete || visited >= limit) return new(false, true, visited);
            }
            var verified = new List<DirectoryGraphMember>(); bool waiting = false; int beforeMembers = visited;
            using var blocks = new DirectorySnapshotBlocks(rootPath, index, keys, root.Source.RouteHint, journal);
            foreach (var member in index.ReadDirectoryGraphMemberPage(root.Source.SourceId, root.Cursor, limit - visited))
            {
                token.ThrowIfCancellationRequested(); visited++;
                if (!await DirectoryChildCausality.IncludesAsync(blocks, index, keys, own, queued.Path, member, token).ConfigureAwait(false)) { waiting = true; break; }
                verified.Add(member);
            }
            await blocks.FlushAsync(token).ConfigureAwait(false);
            using (await FolderReferenceGate.AcquireAsync(index, token).ConfigureAwait(false))
                await index.AdvanceDirectoryParentRootAsync(queued, draft, proof, root, verified, token).ConfigureAwait(false);
            if (visited == beforeMembers) visited++; // Empty roots and blocked prefixes consume a scheduling unit.
            if (waiting) return new(false, true, visited);
        }
        return new(false, false, visited);
    }

    private static FileRecordBody ClassOf(VersionView version)
    {
        var body = version.ExportBody(); body.Vv.Clear(); body.DirectorySnapshotHash = ByteString.Empty; body.DirectorySnapshotLength = 0; return body;
    }
    private static byte[] ClassHash(FileRecordBody body)
    {
        using var hash = Blake3.Hasher.New(); hash.Update("PeerSync directory parent class v1"u8); hash.Update(body.ToByteArray()); return hash.Finalize().AsSpan().ToArray();
    }
}
