using Microsoft.Data.Sqlite;
using PeerSync.Core.Paths;
using PeerSync.Core.Sync;

namespace PeerSync.Storage.FolderDb;

public sealed record DirectoryMetadataSource(byte[] SourceId, string Path, SignedRecord Record, byte[] Hash, int Length, byte[] RouteHint)
{ public override string ToString() => "Directory metadata source (redacted)"; }
public sealed record DirectoryMetadataWork(DirectoryMetadataSource Source, byte[] RootId, long RequestedTicks, long RunOrder,
    int Attempts, long? FailedTicks, long? NextTicks)
{
    public byte[]? RouteAfter { get; init; }
    public override string ToString() => "Directory metadata work (redacted)";
}
public sealed record DirectoryMetadataRoutes(IReadOnlyList<byte[]> Devices, byte[]? NextAfter);
public sealed class DirectoryMetadataCapacityException() : IOException("Directory metadata source capacity is exhausted; retain the enclosing update for retry.");

public sealed partial class FolderIndex
{
    public const int MaximumDirectoryMetadataSources = 4096;
    private const string MetadataSourceColumns = "source_id,root_id,path,record_body,signature,node_hash,node_length,route_hint,requested_ticks,run_order,attempts,failed_ticks,next_ticks,route_after";

    /// <summary>Caller authenticated roots. Admission is FULL and preserves exact envelopes, not rewritten paths.</summary>
    public Task AdmitDirectoryMetadataSourcesAsync(byte[] rootId, IReadOnlyList<DirectoryMetadataSource> sources, long nowTicks, CancellationToken cancellationToken = default)
    {
        byte[] root = FreezeSweep(rootId, nowTicks, 1, 1000); var frozen = FreezeMetadataSources(sources);
        return database.WriteDurableAsync((connection, transaction) =>
        { RequireBlockReferenceRoot(connection, transaction, root); AdmitMetadataSources(connection, transaction, root, frozen, nowTicks); return 0; }, cancellationToken);
    }
    public IReadOnlyList<DirectoryMetadataWork> ReadDueDirectoryMetadataSources(byte[] rootId, long nowTicks, int limit = 32, int byteBudget = 8 * 1024 * 1024)
    {
        byte[] root = FreezeSweep(rootId, nowTicks, 1, limit);
        if (byteBudget is < 1 or > 8 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(byteBudget));
        using var connection = database.OpenReader(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + MetadataSourceColumns + ",length(record_body)+64 FROM directory_metadata_sources WHERE root_id=$root "
            + "AND (next_ticks IS NULL OR failed_ticks>$now OR next_ticks<=$now) ORDER BY run_order,source_id LIMIT $limit;";
        command.Parameters.AddWithValue("$root", root); command.Parameters.AddWithValue("$now", nowTicks); command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader(); var rows = new List<DirectoryMetadataWork>(); long bytes = 0;
        while (reader.Read())
        {
            long size = reader.GetInt64(14); if (rows.Count != 0 && size > byteBudget - bytes) break;
            rows.Add(ReadMetadataSource(reader)); bytes += size;
        }
        return rows.AsReadOnly();
    }
    public DirectoryMetadataWork? ReadDirectoryMetadataSource(byte[] sourceId)
    {
        FolderMaps.RequireBytes(sourceId, 32); using var connection = database.OpenReader(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + MetadataSourceColumns + " FROM directory_metadata_sources WHERE source_id=$id;"; command.Parameters.AddWithValue("$id", sourceId);
        using var reader = command.ExecuteReader(); return reader.Read() ? ReadMetadataSource(reader) : null;
    }
    public long? ReadNextDirectoryMetadataTicks(byte[] rootId, long nowTicks)
    {
        byte[] root = FreezeSweep(rootId, nowTicks, 1, 1000); using var connection = database.OpenReader(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT MIN(CASE WHEN next_ticks IS NULL OR failed_ticks>$now OR next_ticks<=$now THEN $now ELSE next_ticks END) FROM directory_metadata_sources WHERE root_id=$root;";
        command.Parameters.AddWithValue("$root", root); command.Parameters.AddWithValue("$now", nowTicks); return command.ExecuteScalar() is long ticks ? ticks : null;
    }
    public async Task<DirectoryMetadataWork> RecordDirectoryMetadataAttemptAsync(DirectoryMetadataWork expected, long nowTicks, bool failed, byte[]? successfulRoute = null,
        byte[]? routeAfter = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected); RequireStagingNow(nowTicks); if (successfulRoute is not null) FolderMaps.RequireBytes(successfulRoute, 32);
        if (routeAfter is not null) FolderMaps.RequireBytes(routeAfter, 32);
        var frozen = FreezeMetadataWork(expected); byte[]? route = successfulRoute?.ToArray(), after = routeAfter?.ToArray();
        return await database.WriteDurableAsync((connection, transaction) =>
        {
            var actual = RequireMetadataSource(connection, transaction, frozen); using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT last_order FROM directory_metadata_schedule WHERE singleton=1;"; long order = checked((long)command.ExecuteScalar()! + 1);
            command.CommandText = "UPDATE directory_metadata_schedule SET last_order=$order WHERE singleton=1;"; command.Parameters.AddWithValue("$order", order); command.ExecuteNonQuery();
            int attempts = failed ? actual.FailedTicks > nowTicks ? 1 : Math.Min(7, actual.Attempts + 1) : 0;
            long? next = failed ? nowTicks + Math.Min(Math.Min(600L, 10L << (attempts - 1)) * TimeSpan.TicksPerSecond, DateTime.MaxValue.Ticks - nowTicks) : null;
            command.CommandText = "UPDATE directory_metadata_sources SET run_order=$order,route_hint=$hint,route_after=$after,attempts=$attempts,failed_ticks=$failed,next_ticks=$next WHERE source_id=$id;";
            command.Parameters.AddWithValue("$after", (object?)after ?? DBNull.Value);
            command.Parameters.AddWithValue("$hint", route ?? actual.Source.RouteHint); command.Parameters.AddWithValue("$attempts", attempts);
            command.Parameters.AddWithValue("$failed", failed ? nowTicks : DBNull.Value); command.Parameters.AddWithValue("$next", (object?)next ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", actual.Source.SourceId); command.ExecuteNonQuery();
            return actual with { RunOrder = order, Attempts = attempts, FailedTicks = failed ? nowTicks : null, NextTicks = next, RouteAfter = after, Source = actual.Source with { RouteHint = route ?? actual.Source.RouteHint } };
        }, cancellationToken).ConfigureAwait(false);
    }
    public Task CompleteDirectoryMetadataSourceAsync(DirectoryMetadataWork expected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected); var frozen = FreezeMetadataWork(expected);
        return database.WriteDurableAsync((connection, transaction) =>
        {
            var actual = RequireMetadataSource(connection, transaction, frozen); var graph = FindDirectoryGraph(connection, transaction, actual.Source.SourceId);
            if (graph is null || !graph.Complete || graph.Retiring || !graph.RootId.AsSpan().SequenceEqual(actual.RootId) || graph.DirectoryPath != actual.Source.Path
                || graph.Length != actual.Source.Length || !graph.Hash.AsSpan().SequenceEqual(actual.Source.Hash)) throw new InvalidOperationException("Metadata graph is not complete and bound to its source.");
            using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "DELETE FROM directory_metadata_sources WHERE source_id=$id;";
            command.Parameters.AddWithValue("$id", actual.Source.SourceId); if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Metadata completion changed."); return 0;
        }, cancellationToken);
    }
    /// <summary>Cache work expiry only. Required signed owners re-admit missing graphs; this never authorizes native deletion.</summary>
    public Task<int> PruneDirectoryMetadataSourcesAsync(byte[] rootId, long nowTicks, int limit = 1000, CancellationToken cancellationToken = default)
    {
        byte[] root = FreezeSweep(rootId, nowTicks, 1, limit);
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireBlockReferenceRoot(connection, transaction, root); using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "DELETE FROM directory_metadata_sources WHERE source_id IN (SELECT source_id FROM directory_metadata_sources WHERE root_id=$root AND requested_ticks<=$cutoff ORDER BY requested_ticks,source_id LIMIT $limit);";
            command.Parameters.AddWithValue("$root", root); command.Parameters.AddWithValue("$cutoff", nowTicks - TimeSpan.TicksPerDay); command.Parameters.AddWithValue("$limit", limit);
            return command.ExecuteNonQuery();
        }, cancellationToken);
    }
    private static void AdmitMetadataSources(SqliteConnection connection, SqliteTransaction transaction, byte[] root, IReadOnlyList<DirectoryMetadataSource> sources, long nowTicks)
    {
        foreach (var source in sources)
        {
            using var query = connection.CreateCommand(); query.Transaction = transaction; query.CommandText = "SELECT " + MetadataSourceColumns + " FROM directory_metadata_sources WHERE source_id=$id;";
            query.Parameters.AddWithValue("$id", source.SourceId); DirectoryMetadataWork? prior;
            using (var reader = query.ExecuteReader()) prior = reader.Read() ? ReadMetadataSource(reader) : null;
            if (prior is not null)
            {
                RequireMetadataBinding(prior, new(source, root, nowTicks, 0, 0, null, null));
                query.CommandText = "UPDATE directory_metadata_sources SET requested_ticks=$now WHERE source_id=$id;"; query.Parameters.AddWithValue("$now", nowTicks); query.ExecuteNonQuery(); continue;
            }
            var graph = FindDirectoryGraph(connection, transaction, source.SourceId);
            if (graph is not null)
            {
                if (!graph.RootId.AsSpan().SequenceEqual(root) || graph.DirectoryPath != source.Path || graph.Length != source.Length || !graph.Hash.AsSpan().SequenceEqual(source.Hash))
                    throw new InvalidDataException("Metadata graph source binding changed.");
                if (graph.Complete && !graph.Retiring) continue;
            }
            query.CommandText = "SELECT COUNT(*) FROM directory_metadata_sources WHERE root_id=$root;"; query.Parameters.AddWithValue("$root", root);
            if ((long)query.ExecuteScalar()! >= MaximumDirectoryMetadataSources) throw new DirectoryMetadataCapacityException();
            query.CommandText = "INSERT INTO directory_metadata_sources(source_id,root_id,path,record_body,signature,node_hash,node_length,route_hint,requested_ticks) VALUES($id,$root,$path,$body,$signature,$hash,$length,$hint,$now);";
            query.Parameters.AddWithValue("$path", source.Path); query.Parameters.AddWithValue("$body", source.Record.Body.ToArray()); query.Parameters.AddWithValue("$signature", source.Record.Signature.ToArray());
            query.Parameters.AddWithValue("$hash", source.Hash); query.Parameters.AddWithValue("$length", source.Length); query.Parameters.AddWithValue("$hint", source.RouteHint); query.Parameters.AddWithValue("$now", nowTicks); query.ExecuteNonQuery();
        }
    }
    private static DirectoryMetadataSource[] FreezeMetadataSources(IReadOnlyList<DirectoryMetadataSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources); if (sources.Count > MaximumDirectoryMetadataSources) throw new DirectoryMetadataCapacityException();
        var rows = sources.Select(source => source with { SourceId = source.SourceId.ToArray(), Hash = source.Hash.ToArray(), RouteHint = source.RouteHint.ToArray() }).ToArray();
        foreach (var row in rows)
        {
            _ = PathRules.Key(row.Path); FolderMaps.RequireBytes(row.SourceId, 32); FolderMaps.RequireBytes(row.Hash, 32); FolderMaps.RequireBytes(row.RouteHint, 32);
            if (row.Record.BodyLength == 0 || row.Length is < 12 or > 4620 || (row.Length - 12) % 36 != 0) throw new ArgumentException("Invalid directory metadata source.", nameof(sources));
            using var digest = Blake3.Hasher.New(); digest.Update("PeerSync case source v1"u8); digest.Update(row.Record.Body); digest.Update(row.Record.Signature);
            if (!digest.Finalize().AsSpan().SequenceEqual(row.SourceId)) throw new InvalidDataException("Metadata envelope does not match its source ID.");
        }
        return rows;
    }
    private static DirectoryMetadataWork FreezeMetadataWork(DirectoryMetadataWork work)
    { FolderMaps.RequireBytes(work.RootId, 24); if (work.RouteAfter is not null) FolderMaps.RequireBytes(work.RouteAfter, 32); return work with { RootId = work.RootId.ToArray(), Source = FreezeMetadataSources([work.Source])[0], RouteAfter = work.RouteAfter?.ToArray() }; }
    private static DirectoryMetadataWork ReadMetadataSource(SqliteDataReader reader) => new(new(reader.GetFieldValue<byte[]>(0), reader.GetString(2), new(reader.GetFieldValue<byte[]>(3), reader.GetFieldValue<byte[]>(4)),
        reader.GetFieldValue<byte[]>(5), reader.GetInt32(6), reader.GetFieldValue<byte[]>(7)), reader.GetFieldValue<byte[]>(1), reader.GetInt64(8), reader.GetInt64(9), reader.GetInt32(10),
        reader.IsDBNull(11) ? null : reader.GetInt64(11), reader.IsDBNull(12) ? null : reader.GetInt64(12)) { RouteAfter = reader.IsDBNull(13) ? null : reader.GetFieldValue<byte[]>(13) };

    public DirectoryMetadataRoutes ReadDirectoryMetadataRoutes(byte[] rootId, byte[]? afterDevice = null)
    {
        FolderMaps.RequireBytes(rootId, 24); if (afterDevice is not null) FolderMaps.RequireBytes(afterDevice, 32);
        using var connection = database.OpenReader(); using var transaction = connection.BeginTransaction(deferred: true); RequireBlockReferenceRoot(connection, transaction, rootId);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT device_id FROM devices " + (afterDevice is null ? "" : "WHERE device_id>$after ") + "ORDER BY device_id LIMIT 7;";
        if (afterDevice is not null) command.Parameters.AddWithValue("$after", afterDevice);
        using var reader = command.ExecuteReader(); var rows = new List<byte[]>(); bool more = false;
        while (reader.Read()) { if (rows.Count == 6) { more = true; break; } rows.Add(reader.GetFieldValue<byte[]>(0)); }
        return new(rows.AsReadOnly(), more ? rows[^1].ToArray() : null);
    }
    private static DirectoryMetadataWork RequireMetadataSource(SqliteConnection connection, SqliteTransaction transaction, DirectoryMetadataWork expected)
    {
        RequireBlockReferenceRoot(connection, transaction, expected.RootId); using var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT " + MetadataSourceColumns + " FROM directory_metadata_sources WHERE source_id=$id;"; query.Parameters.AddWithValue("$id", expected.Source.SourceId);
        using var reader = query.ExecuteReader(); var actual = reader.Read() ? ReadMetadataSource(reader) : throw new InvalidOperationException("Metadata source no longer exists.");
        RequireMetadataBinding(actual, expected); return actual;
    }
    private static void RequireMetadataBinding(DirectoryMetadataWork actual, DirectoryMetadataWork expected)
    {
        var a = actual.Source; var b = expected.Source;
        if (!actual.RootId.AsSpan().SequenceEqual(expected.RootId) || a.Path != b.Path || a.Length != b.Length || !a.Hash.AsSpan().SequenceEqual(b.Hash)
            || !a.Record.Body.SequenceEqual(b.Record.Body) || !a.Record.Signature.SequenceEqual(b.Record.Signature)) throw new InvalidDataException("Directory metadata source binding changed.");
    }
}
