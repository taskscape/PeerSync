using Microsoft.Data.Sqlite;
using PeerSync.Core.Paths;

namespace PeerSync.Storage.FolderDb;

public sealed partial class FolderIndex
{
    public IReadOnlyList<string> ReadDirectoryPublicationPaths(string? afterKey = null, int limit = 1000)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        string? after = afterKey is null ? null : PathRules.Key(afterKey);
        using var connection = database.OpenReader(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT path_key FROM directory_publications " + (after is null ? "" : "WHERE path_key>$after ") + "ORDER BY path_key LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit); if (after is not null) command.Parameters.AddWithValue("$after", after);
        var paths = new List<string>(); using var reader = command.ExecuteReader(); while (reader.Read()) paths.Add(reader.GetString(0)); return paths.AsReadOnly();
    }
    /// <summary>Caller proved this private work obsolete through a complete
    /// native scan. Discards metadata only, never native bytes or signed rows.</summary>
    public Task DiscardDirectoryPublicationAsync(DirectoryPublication expected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected); var row = FreezePublication(expected); ValidatePublication(row);
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireBlockReferenceRoot(connection, transaction, row.RootId); RequirePublicationExpected(connection, transaction, row);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "DELETE FROM directory_captures WHERE parent_key=$key AND root_id=$root; DELETE FROM directory_publications WHERE path_key=$key AND version=$version;";
            string key = PathRules.Key(row.Path);
            command.Parameters.AddWithValue("$key", key); command.Parameters.AddWithValue("$root", row.RootId);
            command.Parameters.AddWithValue("$version", row.Version.ToString("N")); command.ExecuteNonQuery();
            WakeWaitingParentPublication(connection, transaction, key);
            return 0;
        }, cancellationToken);
    }
    public Task<bool> PauseDirectoryPublicationAsync(DirectoryPublication expected, bool paused, long nowTicks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected); var row = FreezePublication(expected); ValidatePublication(row);
        if (nowTicks is < 1 or > 3155378975999999999) throw new ArgumentOutOfRangeException(nameof(nowTicks));
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequirePublicationExpected(connection, transaction, row); using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE directory_publications SET paused=$paused,touched_ticks=$now WHERE path_key=$key;";
            command.Parameters.AddWithValue("$paused", paused ? 1 : 0); command.Parameters.AddWithValue("$now", nowTicks); command.Parameters.AddWithValue("$key", PathRules.Key(row.Path));
            return command.ExecuteNonQuery() == 1;
        }, cancellationToken);
    }
    public long? ReadNextDirectoryPublicationTicks(byte[] rootId, long nowTicks)
    {
        FolderMaps.RequireBytes(rootId, 24); if (nowTicks is < 1 or > 3155378975999999999) throw new ArgumentOutOfRangeException(nameof(nowTicks));
        using var connection = database.OpenReader(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT MIN(CASE WHEN next_ticks IS NULL OR failed_ticks>$now OR next_ticks<=$now THEN $now ELSE next_ticks END) FROM directory_publications WHERE root_id=$root AND paused=0;";
        command.Parameters.AddWithValue("$root", rootId); command.Parameters.AddWithValue("$now", nowTicks); return command.ExecuteScalar() is long value ? value : null;
    }
    public DirectoryPublicationCache? ReadDirectoryPublicationCache(string path)
    {
        using var connection = database.OpenReader(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT root_id,native_id,record_source_id,input_hash,member_count,input_epoch,touched_ticks,expected_keys_hash FROM directory_publication_cache WHERE path_key=$key;";
        string key = PathRules.Key(path); command.Parameters.AddWithValue("$key", key); using var reader = command.ExecuteReader();
        return reader.Read() ? new DirectoryPublicationCache(key, reader.GetFieldValue<byte[]>(0), reader.GetFieldValue<byte[]>(1), reader.GetFieldValue<byte[]>(2), reader.GetFieldValue<byte[]>(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6))
            { ExpectedKeysHash = reader.GetFieldValue<byte[]>(7) } : null;
    }
    public Task<int> PruneDirectoryPublicationsAsync(byte[] rootId, long nowTicks, int limit = 1000, CancellationToken cancellationToken = default)
    {
        FolderMaps.RequireBytes(rootId, 24); byte[] root = rootId.ToArray();
        if (nowTicks is < 1 or > 3155378975999999999 || limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireBlockReferenceRoot(connection, transaction, root); int count = Delete("directory_publications", limit);
            if (count < limit) count += Delete("directory_publication_cache", limit - count);
            return count;
            int Delete(string table, int remaining)
            {
                cancellationToken.ThrowIfCancellationRequested(); using var command = connection.CreateCommand(); command.Transaction = transaction;
                // Both table identifiers are fixed literals above.
                command.CommandText = $"DELETE FROM {table} WHERE path_key IN (SELECT path_key FROM {table} WHERE root_id=$root AND touched_ticks<=$cutoff ORDER BY touched_ticks,path_key LIMIT $limit);";
                command.Parameters.AddWithValue("$root", root); command.Parameters.AddWithValue("$cutoff", Math.Max(0, nowTicks - TimeSpan.TicksPerDay));
                command.Parameters.AddWithValue("$limit", remaining); return command.ExecuteNonQuery();
            }
        }, cancellationToken);
    }
}
