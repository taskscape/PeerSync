using System.Text;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Paths;

namespace PeerSync.Storage.FolderDb;

public sealed record DirectoryChildBatch(IReadOnlyList<LocalFileRow> Rows, string? NextKey, bool More);

public sealed partial class FolderIndex
{
    /// <summary>Bounded exact signed logical children, ordered like C# ordinal
    /// keys including supplementary Unicode. NeedsRescan can retain the last
    /// published independently captured version; ignored/unsupported rows cannot.
    /// Callers authenticate the envelopes and serialize local-index mutation.</summary>
    public DirectoryChildBatch ReadDirectorySnapshotChildren(string parentPath, string? afterKey = null, int limit = 1000, int byteBudget = 8 * 1024 * 1024)
    {
        string parent = PathRules.Key(parentPath); string? after = afterKey is null ? null : PathRules.Key(afterKey);
        if (after is not null && CaptureParent(after) != parent) throw new ArgumentException("Directory child cursor has another parent.", nameof(afterKey));
        if (limit is < 1 or > 1000 || byteBudget is < 1 or > 8 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(limit));
        using var connection = database.OpenReader(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + FolderMaps.LocalColumns + ",length(record_body)+length(signature)+length(vv) FROM local_files "
            + "WHERE parent_key=$parent AND deleted=0 AND (flags&3)=0 AND type IN (0,1) AND record_body IS NOT NULL AND (type<>0 OR content_ready=1) "
            + (after is null ? "" : "AND path_sort_key>$after ") + "ORDER BY path_sort_key LIMIT $limit;";
        command.Parameters.AddWithValue("$parent", parent); command.Parameters.AddWithValue("$limit", limit);
        if (after is not null) command.Parameters.AddWithValue("$after", Encoding.BigEndianUnicode.GetBytes(after));
        var rows = new List<LocalFileRow>(); long bytes = 0; string? cursor = after;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            long size = reader.GetInt64(17);
            // A single bounded signed record may exceed the batch byte budget;
            // never load the next large body merely to decide to defer it.
            if (rows.Count != 0 && size > byteBudget - bytes) return new(rows.AsReadOnly(), cursor, true);
            var row = FolderMaps.ReadLocal(reader); rows.Add(row); cursor = reader.GetString(0); bytes = checked(bytes + size);
        }
        return new(rows.AsReadOnly(), cursor, rows.Count == limit);
    }
    private async Task BackfillLocalDirectorySortKeysAsync(CancellationToken token)
    {
        for (;;)
        {
            token.ThrowIfCancellationRequested(); var keys = new List<string>(1000);
            using (var connection = database.OpenReader())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT path_key FROM local_files WHERE path_sort_key IS NULL ORDER BY path_key LIMIT 1000;";
                using var reader = command.ExecuteReader(); while (reader.Read()) keys.Add(reader.GetString(0));
            }
            if (keys.Count == 0) return;
            await database.WriteDurableAsync((connection, transaction) =>
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "UPDATE local_files SET path_sort_key=$sort WHERE path_key=$path AND path_sort_key IS NULL;";
                command.Parameters.Add("$sort", SqliteType.Blob); command.Parameters.Add("$path", SqliteType.Text); int count = 0;
                foreach (string key in keys)
                {
                    token.ThrowIfCancellationRequested(); command.Parameters["$sort"].Value = Encoding.BigEndianUnicode.GetBytes(key); command.Parameters["$path"].Value = key;
                    count += command.ExecuteNonQuery();
                }
                return count;
            }, token).ConfigureAwait(false);
        }
    }
}
