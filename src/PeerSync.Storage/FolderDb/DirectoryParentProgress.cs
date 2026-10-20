using Microsoft.Data.Sqlite;
using PeerSync.Core.Paths;
using PeerSync.Core.Sync;

namespace PeerSync.Storage.FolderDb;

public sealed partial class FolderIndex
{
    public DirectoryParentRoot? ReadNextDirectoryParentRoot(DirectoryParentProof expected)
    {
        var proof = FreezeParentProof(expected); using var connection = database.OpenReader(); using var transaction = connection.BeginTransaction(deferred: true);
        var actual = RequireParentState(connection, transaction, proof);
        if (!actual.DiscoveryComplete || actual.RemoteRevision != actual.DiscoveryRevision) throw new InvalidOperationException("Parent discovery is incomplete.");
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT source_id,source_path,record_body,signature,node_hash,node_length,source_device,seen_generation,cursor,complete,proof_input_hash,proof_child_epoch "
            + "FROM directory_parent_roots WHERE path_key=$path AND seen_generation=$generation AND "
            + "(complete=0 OR proof_input_hash IS NULL OR proof_input_hash<>$input OR proof_child_epoch<>$child) ORDER BY node_hash,node_length LIMIT 1;";
        command.Parameters.AddWithValue("$path", proof.PathKey); command.Parameters.AddWithValue("$generation", proof.Generation);
        command.Parameters.AddWithValue("$input", proof.InputHash); command.Parameters.AddWithValue("$child", proof.ChildEpoch); using var reader = command.ExecuteReader();
        return reader.Read() ? ReadParentRoot(reader, actual) : null;
    }

    public Task<DirectoryParentRoot> AdvanceDirectoryParentRootAsync(DirectoryPublication queued, DirectoryCaptureState draft, DirectoryParentProof expected,
        DirectoryParentRoot root, IReadOnlyList<DirectoryGraphMember> verified, CancellationToken cancellationToken = default)
    {
        var publication = FreezePublication(queued); var capture = FreezeCapture(draft); var proof = FreezeParentProof(expected);
        var source = FreezeMetadataSources([root.Source])[0]; var item = root with { Source = source };
        if (verified.Count > 1000) throw new ArgumentOutOfRangeException(nameof(verified));
        var members = verified.Select(row => row with { RecordHash = row.RecordHash.ToArray() }).ToArray();
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireParentInputs(connection, transaction, publication, capture); var actual = RequireParentState(connection, transaction, proof);
            if (!actual.DiscoveryComplete || actual.RemoteRevision != actual.DiscoveryRevision) throw new InvalidOperationException("Parent discovery changed.");
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT source_id,source_path,record_body,signature,node_hash,node_length,source_device,seen_generation,cursor,complete,proof_input_hash,proof_child_epoch "
                + "FROM directory_parent_roots WHERE path_key=$path AND source_id=$source;";
            command.Parameters.AddWithValue("$path", proof.PathKey); command.Parameters.AddWithValue("$source", source.SourceId); DirectoryParentRoot current;
            using (var reader = command.ExecuteReader()) current = reader.Read() ? ReadParentRoot(reader, actual) : throw new InvalidOperationException("Parent root disappeared.");
            if (current.Complete || item.Complete || item.PathKey != proof.PathKey || current.Generation != proof.Generation || current.Generation != item.Generation || current.Cursor != item.Cursor
                || !current.Source.Hash.AsSpan().SequenceEqual(source.Hash) || current.Source.Length != source.Length || current.Source.Path != source.Path
                || !current.Source.Record.Body.SequenceEqual(source.Record.Body) || !current.Source.Record.Signature.SequenceEqual(source.Record.Signature))
                throw new InvalidOperationException("Parent root progress changed.");
            RequireParentGraph(connection, transaction, actual, source);
            command.CommandText = "SELECT path_key,record_hash,record_length FROM directory_graph_members WHERE graph_id=$source "
                + (item.Cursor is null ? "" : "AND path_key>$after ") + "ORDER BY path_key LIMIT $limit;";
            command.Parameters.AddWithValue("$limit", members.Length + 1); if (item.Cursor is not null) command.Parameters.AddWithValue("$after", item.Cursor);
            string? cursor = item.Cursor; bool complete;
            using (var reader = command.ExecuteReader())
            {
                foreach (var member in members)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!reader.Read() || reader.GetString(0) != member.PathKey || reader.GetInt32(2) != member.RecordLength
                        || !reader.GetFieldValue<byte[]>(1).AsSpan().SequenceEqual(member.RecordHash)) throw new InvalidOperationException("Parent root prefix changed.");
                    cursor = member.PathKey;
                }
                complete = !reader.Read();
            }
            command.CommandText = "UPDATE directory_parent_roots SET cursor=$cursor,complete=$complete,proof_input_hash=$input,proof_child_epoch=$child WHERE path_key=$path AND source_id=$source;";
            command.Parameters.AddWithValue("$cursor", (object?)cursor ?? DBNull.Value); command.Parameters.AddWithValue("$complete", complete ? 1 : 0);
            command.Parameters.AddWithValue("$input", proof.InputHash); command.Parameters.AddWithValue("$child", proof.ChildEpoch); command.ExecuteNonQuery();
            return current with { Cursor = cursor, Complete = complete };
        }, cancellationToken);
    }

    /// <summary>Use under the reference gate for final body validation; a new revision cannot reuse this floor.</summary>
    public IReadOnlyDictionary<ulong, ulong> ReadProvenDirectoryParentClock(DirectoryPublication queued)
    {
        var publication = FreezePublication(queued); using var connection = database.OpenReader(); using var transaction = connection.BeginTransaction(deferred: true);
        var capture = FindDirectoryCapture(connection, transaction, PathRules.Key(queued.Path)) ?? throw new InvalidOperationException("Parent capture disappeared.");
        RequireParentInputs(connection, transaction, publication, capture);
        return RequireParentProofReady(connection, transaction, publication, capture).Clock;
    }

    private static DirectoryParentProof RequireParentProofReady(SqliteConnection connection, SqliteTransaction transaction, DirectoryPublication queued, DirectoryCaptureState draft)
    {
        var proof = FindParentProof(connection, transaction, PathRules.Key(queued.Path)) ?? throw new InvalidOperationException("Parent root-set proof is missing.");
        if (proof.Resetting || proof.QueueVersion != queued.Version || proof.LocalSequence != queued.ExpectedLocalSequence || proof.InputEpoch != draft.InputEpoch
            || proof.ChildEpoch != proof.VerifiedChildEpoch || !proof.DiscoveryComplete || proof.RemoteRevision != proof.DiscoveryRevision
            || !proof.RootId.AsSpan().SequenceEqual(queued.RootId) || !proof.NativeId.AsSpan().SequenceEqual(queued.NativeId)
            || !proof.InputHash.AsSpan().SequenceEqual(draft.InputHash)) throw new InvalidOperationException("Parent root-set witness changed.");
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT record_body,signature FROM local_files WHERE path_key=$path AND local_seq=$seq AND type=1 AND deleted=0;";
        command.Parameters.AddWithValue("$path", proof.PathKey); command.Parameters.AddWithValue("$seq", proof.LocalSequence);
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || reader.IsDBNull(0)) throw new InvalidOperationException("Parent original disappeared.");
            using var digest = Blake3.Hasher.New(); digest.Update("PeerSync case source v1"u8); digest.Update(reader.GetFieldValue<byte[]>(0)); digest.Update(reader.GetFieldValue<byte[]>(1));
            if (!digest.Finalize().AsSpan().SequenceEqual(proof.LocalSourceId)) throw new InvalidOperationException("Parent original changed.");
        }
        command.CommandText = "SELECT COUNT(*),SUM(CASE WHEN r.complete=0 OR r.proof_input_hash IS NULL OR r.proof_input_hash<>$input OR r.proof_child_epoch<>$child "
            + "OR g.id IS NULL OR g.complete=0 OR g.retiring=1 OR g.node_hash<>r.node_hash OR g.node_length<>r.node_length OR g.root_id<>$root OR g.directory_path<>r.source_path THEN 1 ELSE 0 END) "
            + "FROM directory_parent_roots r LEFT JOIN directory_graphs g ON g.id=r.source_id WHERE r.path_key=$path AND r.seen_generation=$generation;";
        command.Parameters.AddWithValue("$input", proof.InputHash); command.Parameters.AddWithValue("$child", proof.ChildEpoch);
        command.Parameters.AddWithValue("$root", proof.RootId); command.Parameters.AddWithValue("$generation", proof.Generation);
        using (var reader = command.ExecuteReader())
            if (!reader.Read() || reader.GetInt64(0) == 0 || reader.GetInt64(1) != 0) throw new InvalidOperationException("Parent roots are not completely proven.");
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM directory_parent_roots WHERE path_key=$path AND node_hash=$hash AND node_length=$length AND seen_generation=$generation);";
        command.Parameters.AddWithValue("$hash", proof.LocalHash); command.Parameters.AddWithValue("$length", proof.LocalLength);
        if ((long)command.ExecuteScalar()! == 0) throw new InvalidOperationException("Parent original root is outside its proof set.");
        return proof;
    }
    private static DirectoryParentRoot ReadParentRoot(SqliteDataReader reader, DirectoryParentProof proof)
    {
        bool current = !reader.IsDBNull(10) && reader.GetFieldValue<byte[]>(10).AsSpan().SequenceEqual(proof.InputHash) && reader.GetInt64(11) == proof.ChildEpoch;
        return new(proof.PathKey, ReadParentSource(reader), reader.GetInt64(7), current && !reader.IsDBNull(8) ? reader.GetString(8) : null, current && reader.GetBoolean(9));
    }
    private static void RequireParentGraph(SqliteConnection connection, SqliteTransaction transaction, DirectoryParentProof proof, DirectoryMetadataSource source)
    {
        var graph = FindDirectoryGraph(connection, transaction, source.SourceId);
        if (graph is null || !graph.Complete || graph.Retiring || graph.DirectoryPath != source.Path || graph.Length != source.Length
            || !graph.Hash.AsSpan().SequenceEqual(source.Hash) || !graph.RootId.AsSpan().SequenceEqual(proof.RootId)) throw new InvalidOperationException("Parent original graph is unavailable.");
    }
}
