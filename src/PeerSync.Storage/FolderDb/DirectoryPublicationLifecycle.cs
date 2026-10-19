using Microsoft.Data.Sqlite;
using PeerSync.Core.Paths;

namespace PeerSync.Storage.FolderDb;

public sealed partial class FolderIndex
{
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
        command.CommandText = "SELECT root_id,native_id,record_source_id,input_hash,member_count,input_epoch,touched_ticks FROM directory_publication_cache WHERE path_key=$key;";
        string key = PathRules.Key(path); command.Parameters.AddWithValue("$key", key); using var reader = command.ExecuteReader();
        return reader.Read() ? new(key, reader.GetFieldValue<byte[]>(0), reader.GetFieldValue<byte[]>(1), reader.GetFieldValue<byte[]>(2), reader.GetFieldValue<byte[]>(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6)) : null;
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
