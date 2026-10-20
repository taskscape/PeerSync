using PeerSync.Core.Paths;

namespace PeerSync.Storage.FolderDb;

public sealed record DirectoryParentSourcePage(IReadOnlyList<DirectoryMetadataSource> Sources, byte[]? NextSourceId, bool More);

public sealed partial class FolderIndex
{
    public DirectoryParentSourcePage ReadDirectoryParentSources(byte[] rootId, byte[]? afterSourceId = null, int limit = 32, int byteBudget = 8 * 1024 * 1024)
    {
        FolderMaps.RequireBytes(rootId, 24); if (afterSourceId is not null) FolderMaps.RequireBytes(afterSourceId, 32);
        if (limit is < 1 or > 1000 || byteBudget is < 1 or > 8 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(limit));
        using var connection = database.OpenReader(); using var transaction = connection.BeginTransaction(deferred: true); RequireBlockReferenceRoot(connection, transaction, rootId);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT r.source_id,r.source_path,r.record_body,r.signature,r.node_hash,r.node_length,r.source_device,length(r.record_body)+64 "
            + "FROM directory_parent_roots r JOIN directory_parent_proofs p ON p.path_key=r.path_key WHERE p.root_id=$root "
            + (afterSourceId is null ? "" : "AND r.source_id>$after ") + "ORDER BY r.source_id LIMIT $limit;";
        command.Parameters.AddWithValue("$root", rootId); if (afterSourceId is not null) command.Parameters.AddWithValue("$after", afterSourceId);
        command.Parameters.AddWithValue("$limit", limit + 1); var sources = new List<DirectoryMetadataSource>(); long bytes = 0; byte[]? cursor = afterSourceId; bool more = false;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            long size = reader.GetInt64(7); if (sources.Count == limit || sources.Count != 0 && size > byteBudget - bytes) { more = true; break; }
            var source = ReadParentSource(reader); sources.Add(source); bytes += size; cursor = source.SourceId;
        }
        return new(sources.AsReadOnly(), cursor?.ToArray(), more);
    }

    /// <summary>Retire cache witnesses only. Never remove a queued publication or authorize native mutation.</summary>
    public Task<int> PruneDirectoryParentProofsAsync(byte[] rootId, long nowTicks, int limit = 1000, CancellationToken cancellationToken = default)
    {
        byte[] root = FreezeSweep(rootId, nowTicks, 1, limit);
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireBlockReferenceRoot(connection, transaction, root); using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.Parameters.AddWithValue("$root", root); command.Parameters.AddWithValue("$cutoff", Math.Max(0, nowTicks - TimeSpan.TicksPerDay)); command.Parameters.AddWithValue("$limit", limit);
            command.CommandText = "DELETE FROM directory_parent_roots WHERE source_id IN (SELECT r.source_id FROM directory_parent_roots r JOIN directory_parent_proofs p ON p.path_key=r.path_key "
                + "WHERE p.root_id=$root AND ((p.discovery_complete=1 AND p.remote_revision=p.discovery_revision AND r.seen_generation<>p.generation) "
                + "OR (p.touched_ticks<=$cutoff AND NOT EXISTS(SELECT 1 FROM directory_publications q WHERE q.path_key=p.path_key))) ORDER BY r.source_id LIMIT $limit);";
            cancellationToken.ThrowIfCancellationRequested(); int count = command.ExecuteNonQuery(); if (count == limit) return count;
            command.Parameters["$limit"].Value = limit - count;
            command.CommandText = "DELETE FROM directory_parent_proofs WHERE path_key IN (SELECT p.path_key FROM directory_parent_proofs p WHERE p.root_id=$root AND p.touched_ticks<=$cutoff "
                + "AND NOT EXISTS(SELECT 1 FROM directory_publications q WHERE q.path_key=p.path_key) AND NOT EXISTS(SELECT 1 FROM directory_parent_roots r WHERE r.path_key=p.path_key) ORDER BY p.touched_ticks,p.path_key LIMIT $limit);";
            return count + command.ExecuteNonQuery();
        }, cancellationToken);
    }
}
