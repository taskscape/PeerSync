using Microsoft.Data.Sqlite;
using PeerSync.Core.Paths;
using PeerSync.Core.Sync;

namespace PeerSync.Storage.FolderDb;

public sealed partial class FolderIndex
{
    public DirectoryParentPage ReadDirectoryParentPage(DirectoryParentProof expected, int limit = 32)
    {
        if (limit is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(limit));
        var proof = FreezeParentProof(expected); using var connection = database.OpenReader(); using var transaction = connection.BeginTransaction(deferred: true);
        var actual = RequireParentState(connection, transaction, proof); return ReadParentPage(connection, transaction, actual, limit);
    }

    /// <summary>Admit only authenticated representatives selected from this exact discovery page.</summary>
    public Task<DirectoryParentProof> AdvanceDirectoryParentDiscoveryAsync(DirectoryPublication queued, DirectoryCaptureState draft,
        DirectoryParentProof expected, DirectoryParentPage page, IReadOnlyList<DirectoryMetadataSource> accepted, long nowTicks, CancellationToken cancellationToken = default)
    {
        var publication = FreezePublication(queued); var capture = FreezeCapture(draft); var proof = FreezeParentProof(expected);
        var sources = FreezeMetadataSources(accepted); RequireStagingNow(nowTicks);
        var snapshot = page with { After = page.After?.ToArray(), NextAfter = page.NextAfter?.ToArray(), Rows = page.Rows.Select(row => row with { DeviceId = row.DeviceId.ToArray(), Vector = new Dictionary<ulong, ulong>(row.Vector) }).ToArray() };
        if (page.Limit is < 1 or > 32 || sources.Length > page.Limit) throw new ArgumentOutOfRangeException(nameof(accepted));
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireParentInputs(connection, transaction, publication, capture); var actual = RequireParentState(connection, transaction, proof);
            if (actual.RemoteRevision != actual.DiscoveryRevision || actual.RemoteRevision != snapshot.Revision || actual.DiscoveryComplete)
                throw new InvalidOperationException("Parent discovery revision changed.");
            var current = ReadParentPage(connection, transaction, actual, snapshot.Limit);
            if (current.More != snapshot.More || !SameParentBytes(current.After, snapshot.After) || !SameParentBytes(current.NextAfter, snapshot.NextAfter)
                || current.Rows.Count != snapshot.Rows.Count || current.Rows.Where((row, n) => !row.DeviceId.AsSpan().SequenceEqual(snapshot.Rows[n].DeviceId)
                    || VersionVector.Compare(row.Vector, snapshot.Rows[n].Vector) != VvOrder.Equal || !row.Record.Body.SequenceEqual(snapshot.Rows[n].Record.Body) || !row.Record.Signature.SequenceEqual(snapshot.Rows[n].Record.Signature)).Any())
                throw new InvalidOperationException("Parent discovery page changed.");
            IReadOnlyDictionary<ulong, ulong> clock = actual.Clock;
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (PathRules.Key(source.Path) != proof.PathKey || !current.Rows.Any(row => row.DeviceId.AsSpan().SequenceEqual(source.RouteHint)
                    && row.Record.Body.SequenceEqual(source.Record.Body) && row.Record.Signature.SequenceEqual(source.Record.Signature)))
                    throw new InvalidDataException("Parent source is not in its discovery page.");
                var row = current.Rows.Single(row => row.DeviceId.AsSpan().SequenceEqual(source.RouteHint));
                clock = VersionVector.Merge(clock, row.Vector);
                AddParentRoot(connection, transaction, actual, source, nowTicks);
            }
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE directory_parent_proofs SET discovery_after=$after,discovery_complete=$complete,discovery_clock=$clock,touched_ticks=$now WHERE path_key=$path;";
            command.Parameters.AddWithValue("$after", (object?)current.NextAfter ?? DBNull.Value); command.Parameters.AddWithValue("$complete", current.More ? 0 : 1);
            command.Parameters.AddWithValue("$clock", VersionVector.Serialize(clock));
            command.Parameters.AddWithValue("$now", nowTicks); command.Parameters.AddWithValue("$path", proof.PathKey); command.ExecuteNonQuery();
            return FindParentProof(connection, transaction, proof.PathKey)!;
        }, cancellationToken);
    }

    private static DirectoryParentPage ReadParentPage(SqliteConnection connection, SqliteTransaction transaction, DirectoryParentProof proof, int limit)
    {
        if (proof.Resetting || proof.DiscoveryComplete) throw new InvalidOperationException("Parent discovery is not open.");
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT device_id,path_key,vv,record_body,signature,length(record_body)+64 FROM remote_files WHERE path_key=$path "
            + (proof.DiscoveryAfter is null ? "" : "AND device_id>$after ") + "ORDER BY device_id LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit + 1);
        command.Parameters.AddWithValue("$path", proof.PathKey); if (proof.DiscoveryAfter is not null) command.Parameters.AddWithValue("$after", proof.DiscoveryAfter);
        var rows = new List<RemoteFileRow>(); long bytes = 0; bool more = false; byte[]? after = proof.DiscoveryAfter; using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            long size = reader.GetInt64(5);
            if (rows.Count == limit || rows.Count != 0 && size > 8 * 1024 * 1024 - bytes) { more = true; break; }
            after = reader.GetFieldValue<byte[]>(0); bytes += size;
            rows.Add(new(after, reader.GetString(1), VersionVector.Deserialize(reader.GetFieldValue<byte[]>(2)), new(reader.GetFieldValue<byte[]>(3), reader.GetFieldValue<byte[]>(4))));
        }
        return new(limit, proof.RemoteRevision, proof.DiscoveryAfter?.ToArray(), after?.ToArray(), more, rows.AsReadOnly());
    }

    private static void AddParentRoot(SqliteConnection connection, SqliteTransaction transaction, DirectoryParentProof proof, DirectoryMetadataSource source, long nowTicks)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT source_id,source_path,record_body,signature,node_hash,node_length,source_device FROM directory_parent_roots WHERE path_key=$path AND node_hash=$hash AND node_length=$length;";
        command.Parameters.AddWithValue("$path", proof.PathKey); command.Parameters.AddWithValue("$hash", source.Hash); command.Parameters.AddWithValue("$length", source.Length);
        DirectoryMetadataSource? existing;
        using (var reader = command.ExecuteReader()) existing = reader.Read() ? ReadParentSource(reader) : null;
        if (existing is null)
        {
            command.CommandText = "SELECT COUNT(*) FROM directory_parent_roots;";
            if ((long)command.ExecuteScalar()! >= MaximumDirectoryMetadataSources)
            {
                // Obsolete generations are not publication authority. Evict one
                // oldest stale representative at capacity; never an active root.
                command.CommandText = "DELETE FROM directory_parent_roots WHERE source_id IN (SELECT r.source_id FROM directory_parent_roots r JOIN directory_parent_proofs p ON p.path_key=r.path_key "
                    + "WHERE r.seen_generation<>p.generation ORDER BY r.seen_generation,r.source_id LIMIT 1);";
                if (command.ExecuteNonQuery() != 1) throw new DirectoryMetadataCapacityException();
            }
            command.CommandText = "INSERT INTO directory_parent_roots(path_key,node_hash,node_length,source_id,source_path,source_device,record_body,signature,seen_generation) VALUES($path,$hash,$length,$source,$sourcePath,$device,$body,$signature,$generation);";
            command.Parameters.AddWithValue("$source", source.SourceId); command.Parameters.AddWithValue("$sourcePath", source.Path); command.Parameters.AddWithValue("$device", source.RouteHint);
            command.Parameters.AddWithValue("$body", source.Record.Body.ToArray()); command.Parameters.AddWithValue("$signature", source.Record.Signature.ToArray());
        }
        else command.CommandText = "UPDATE directory_parent_roots SET seen_generation=$generation WHERE path_key=$path AND node_hash=$hash AND node_length=$length;";
        command.Parameters.AddWithValue("$generation", proof.Generation); command.ExecuteNonQuery();
        AdmitMetadataSources(connection, transaction, proof.RootId, [existing ?? source], nowTicks);
    }

    private static DirectoryMetadataSource ReadParentSource(SqliteDataReader reader) => new(reader.GetFieldValue<byte[]>(0), reader.GetString(1),
        new(reader.GetFieldValue<byte[]>(2), reader.GetFieldValue<byte[]>(3)), reader.GetFieldValue<byte[]>(4), reader.GetInt32(5), reader.GetFieldValue<byte[]>(6));
    private static bool SameParentBytes(byte[]? first, byte[]? second) => first is null ? second is null : second is not null && first.AsSpan().SequenceEqual(second);
    private static DirectoryParentProof FreezeParentProof(DirectoryParentProof proof)
    {
        ArgumentNullException.ThrowIfNull(proof); _ = PathRules.Key(proof.PathKey);
        FolderMaps.RequireBytes(proof.RootId, 24); FolderMaps.RequireBytes(proof.NativeId, 16); FolderMaps.RequireBytes(proof.MetadataHash, 32);
        FolderMaps.RequireBytes(proof.InputHash, 32); FolderMaps.RequireBytes(proof.LocalSourceId, 32); FolderMaps.RequireBytes(proof.LocalHash, 32);
        if (proof.DiscoveryAfter is not null) FolderMaps.RequireBytes(proof.DiscoveryAfter, 32);
        return proof with { RootId = proof.RootId.ToArray(), NativeId = proof.NativeId.ToArray(), MetadataHash = proof.MetadataHash.ToArray(), InputHash = proof.InputHash.ToArray(),
            LocalSourceId = proof.LocalSourceId.ToArray(), LocalHash = proof.LocalHash.ToArray(), DiscoveryAfter = proof.DiscoveryAfter?.ToArray(), Clock = new Dictionary<ulong, ulong>(proof.Clock) };
    }
    private static DirectoryParentProof RequireParentState(SqliteConnection connection, SqliteTransaction transaction, DirectoryParentProof expected)
    {
        var actual = FindParentProof(connection, transaction, expected.PathKey) ?? throw new InvalidOperationException("Parent proof disappeared.");
        RequireBlockReferenceRoot(connection, transaction, expected.RootId);
        if (actual.Resetting || expected.Resetting || actual.QueueVersion != expected.QueueVersion || actual.LocalSequence != expected.LocalSequence || actual.InputEpoch != expected.InputEpoch
            || VersionVector.Compare(actual.Clock, expected.Clock) != VvOrder.Equal || actual.Generation != expected.Generation || actual.VerifiedChildEpoch != actual.ChildEpoch || actual.ChildEpoch != expected.ChildEpoch
            || actual.DiscoveryRevision != expected.DiscoveryRevision || actual.DiscoveryComplete != expected.DiscoveryComplete || !SameParentBytes(actual.DiscoveryAfter, expected.DiscoveryAfter)
            || !actual.RootId.AsSpan().SequenceEqual(expected.RootId) || !actual.NativeId.AsSpan().SequenceEqual(expected.NativeId)
            || !actual.MetadataHash.AsSpan().SequenceEqual(expected.MetadataHash) || !actual.InputHash.AsSpan().SequenceEqual(expected.InputHash)
            || !actual.LocalSourceId.AsSpan().SequenceEqual(expected.LocalSourceId) || !actual.LocalHash.AsSpan().SequenceEqual(expected.LocalHash) || actual.LocalLength != expected.LocalLength)
            throw new InvalidOperationException("Parent proof witness changed.");
        return actual;
    }
}
