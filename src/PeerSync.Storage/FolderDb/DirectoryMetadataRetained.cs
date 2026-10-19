namespace PeerSync.Storage.FolderDb;

public sealed record DirectoryMetadataRetainedPage(IReadOnlyList<CasePreservationSource> Rows, byte[]? NextSourceId, bool More);

public sealed partial class FolderIndex
{
    public DirectoryMetadataRetainedPage ReadRetainedDirectoryMetadata(byte[] rootId, long nowTicks, byte[]? afterSourceId = null, int limit = 32, int byteBudget = 8 * 1024 * 1024)
    {
        byte[] root = FreezeSweep(rootId, nowTicks, 1, limit);
        if (byteBudget is < 1 or > 8 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(byteBudget));
        if (afterSourceId is not null) FolderMaps.RequireBytes(afterSourceId, 32); byte[]? cursor = afterSourceId?.ToArray();
        using var connection = database.OpenReader(); using var transaction = connection.BeginTransaction(deferred: true); RequireBlockReferenceRoot(connection, transaction, root);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT source_id,source_device,path_key,length(record_body)+64,record_body,signature FROM case_preservations "
            + "WHERE root_id=$root AND completed=1 AND retained_until_ticks IS NOT NULL AND retained_until_ticks>$now AND record_body IS NOT NULL "
            + (cursor is null ? "" : "AND source_id>$after ") + "ORDER BY source_id LIMIT $limit;";
        command.Parameters.AddWithValue("$root", root); command.Parameters.AddWithValue("$now", nowTicks); command.Parameters.AddWithValue("$limit", limit + 1);
        if (cursor is not null) command.Parameters.AddWithValue("$after", cursor);
        var rows = new List<CasePreservationSource>(); long bytes = 0; bool more = false; byte[]? next = cursor; using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            long size = reader.GetInt64(3);
            if (rows.Count == limit || rows.Count != 0 && size > byteBudget - bytes) { more = true; break; }
            next = reader.GetFieldValue<byte[]>(0);
            rows.Add(new(next, reader.GetFieldValue<byte[]>(1), reader.GetString(2), new(reader.GetFieldValue<byte[]>(4), reader.GetFieldValue<byte[]>(5)))); bytes += size;
        }
        return new(rows.AsReadOnly(), next, more);
    }
}
