using Microsoft.Data.Sqlite;

namespace PeerSync.Storage.FolderDb;

public sealed partial class FolderIndex
{
    /// <summary>Caller authenticates the exact marker under both maintenance
    /// gates. Public observation is a verified local publication, never absence.</summary>
    public Task<bool> RetireHistoryMarkerAsync(MetaRow marker, byte[] rootId, long cutoffTicks, long nowTicks,
        bool publicObserved = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(marker); byte[] root = FreezePolicyRoot(rootId, cutoffTicks, nowTicks);
        if (!marker.Key.StartsWith("_history-", StringComparison.Ordinal) || marker.Value is not { Length: > 0 and <= ApplyIntent.MaxPayloadBytes })
            throw new ArgumentException("Invalid history marker.", nameof(marker));
        var frozen = marker with { Value = marker.Value.ToArray() };
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireBlockReferenceRoot(connection, transaction, root); RequireNoReferenceApply(connection, transaction);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "DELETE FROM meta WHERE key=$key AND value=$value AND EXISTS(SELECT 1 FROM history_archive_links h "
                + "WHERE h.marker_key=meta.key AND h.root_id=$root AND ((h.source_id IS NULL AND $observed=1 AND h.retained_ticks<$cutoff) "
                + "OR (h.source_id IS NOT NULL AND (h.retained_ticks<$cutoff OR EXISTS(SELECT 1 FROM archive_retirement_sources b "
                + "JOIN archive_retirements r ON r.id=b.retirement_id WHERE b.source_id=h.source_id AND r.root_id=$root "
                + "AND r.completed_ticks IS NOT NULL AND r.completed_ticks<=$now AND r.prepared_ticks>=h.retained_ticks)))));";
            command.Parameters.AddWithValue("$key", frozen.Key); command.Parameters.AddWithValue("$value", frozen.Value);
            command.Parameters.AddWithValue("$root", root); command.Parameters.AddWithValue("$cutoff", cutoffTicks);
            command.Parameters.AddWithValue("$now", nowTicks); command.Parameters.AddWithValue("$observed", publicObserved ? 1 : 0);
            return command.ExecuteNonQuery() == 1;
        }, cancellationToken);
    }

    /// <summary>Durable policy release changes reference protection only. It
    /// never authorizes native deletion or guesses ownership from a missing row.</summary>
    public Task<bool> ReleaseArchiveSourceAsync(Guid sourceId, byte[] rootId, long cutoffTicks, long nowTicks,
        CancellationToken cancellationToken = default)
    {
        if (sourceId == Guid.Empty) throw new ArgumentException("Invalid archive source.", nameof(sourceId));
        byte[] root = FreezePolicyRoot(rootId, cutoffTicks, nowTicks);
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireBlockReferenceRoot(connection, transaction, root); RequireNoReferenceApply(connection, transaction);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM meta m WHERE m.key GLOB '_history-*' AND NOT EXISTS(SELECT 1 FROM history_archive_links h WHERE h.marker_key=m.key AND h.root_id=$root));";
            command.Parameters.AddWithValue("$root", root);
            if ((long)command.ExecuteScalar()! != 0) throw new InvalidOperationException("Account legacy history before policy release.");
            command.CommandText = "INSERT OR IGNORE INTO archive_policy_releases(source_id,root_id,released_ticks,reason,witness_id) "
                + "SELECT p.source_id,$root,$now,CASE WHEN p.archived_ticks>0 AND p.archived_ticks<$cutoff THEN 1 ELSE 2 END, "
                + "(SELECT r.id FROM archive_retirement_sources b JOIN archive_retirements r ON r.id=b.retirement_id "
                + "WHERE b.source_id=p.source_id AND r.root_id=$root AND r.completed_ticks IS NOT NULL AND r.completed_ticks<=$now ORDER BY r.completed_ticks DESC LIMIT 1) "
                + "FROM archive_provenance p WHERE p.source_id=$source AND (p.root_id IS NULL OR p.root_id=$root) "
                + "AND NOT EXISTS(SELECT 1 FROM history_archive_links h WHERE h.source_id=p.source_id) "
                + "AND NOT EXISTS(SELECT 1 FROM archive_retirement_sources b JOIN archive_retirements r ON r.id=b.retirement_id WHERE b.source_id=p.source_id AND r.completed_ticks IS NULL) "
                + "AND ((p.archived_ticks>0 AND p.archived_ticks<$cutoff) OR EXISTS(SELECT 1 FROM archive_retirement_sources b JOIN archive_retirements r ON r.id=b.retirement_id "
                + "WHERE b.source_id=p.source_id AND r.root_id=$root AND r.completed_ticks IS NOT NULL AND r.completed_ticks<=$now));";
            command.Parameters.AddWithValue("$source", sourceId.ToString("N")); command.Parameters.AddWithValue("$now", nowTicks);
            command.Parameters.AddWithValue("$cutoff", cutoffTicks); return command.ExecuteNonQuery() == 1;
        }, cancellationToken);
    }

    public bool IsArchiveSourceReleased(Guid sourceId, byte[] rootId)
    {
        FolderMaps.RequireBytes(rootId, 24); using var connection = database.OpenReader(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM archive_policy_releases WHERE source_id=$source AND root_id=$root);";
        command.Parameters.AddWithValue("$source", sourceId.ToString("N")); command.Parameters.AddWithValue("$root", rootId);
        return (long)command.ExecuteScalar()! != 0;
    }

    private static byte[] FreezePolicyRoot(byte[] rootId, long cutoffTicks, long nowTicks)
    {
        FolderMaps.RequireBytes(rootId, 24);
        if (nowTicks <= 0 || nowTicks > DateTime.MaxValue.Ticks || cutoffTicks < 0 || cutoffTicks > nowTicks)
            throw new ArgumentOutOfRangeException(nameof(cutoffTicks));
        return rootId.ToArray();
    }
}
