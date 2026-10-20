namespace PeerSync.Storage.FolderDb;

public sealed partial class FolderIndex
{
    /// <summary>Retire obsolete copy witnesses only; never authorizes native deletion or source release.</summary>
    public Task<int> RetireDirectoryCaseWitnessesAsync(byte[] rootId, long nowTicks, int limit = 1000, CancellationToken cancellationToken = default)
    {
        byte[] root = FreezeSweep(rootId, nowTicks, 1, limit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 1000);
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireBlockReferenceRoot(connection, transaction, root);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM apply_intents);";
            if ((long)command.ExecuteScalar()! != 0) return 0;
            command.CommandText = "DELETE FROM directory_case_members WHERE (source_id,root_file_id,member_key) IN ("
                + "SELECT m.source_id,m.root_file_id,m.member_key FROM directory_case_members m JOIN case_preservations c ON c.source_id=m.source_id "
                + "WHERE c.root_id=$root AND m.touched_ticks<=$cutoff AND ((c.completed=1 AND c.completed_ticks<=$cutoff) "
                + "OR (c.completed=0 AND (c.installed_file_id IS NULL OR c.installed_file_id<>m.root_file_id))) "
                + "ORDER BY m.touched_ticks,m.source_id,m.root_file_id,m.member_key LIMIT $limit);";
            command.Parameters.AddWithValue("$root", root); command.Parameters.AddWithValue("$cutoff", nowTicks - TimeSpan.TicksPerDay);
            command.Parameters.AddWithValue("$limit", limit); cancellationToken.ThrowIfCancellationRequested(); int retired = command.ExecuteNonQuery();
            if (retired == limit) return retired;
            command.Parameters["$limit"].Value = limit - retired;
            command.CommandText = "DELETE FROM directory_case_progress WHERE (source_id,root_file_id) IN ("
                + "SELECT p.source_id,p.root_file_id FROM directory_case_progress p JOIN case_preservations c ON c.source_id=p.source_id "
                + "WHERE c.root_id=$root AND ((c.completed=1 AND c.completed_ticks<=$cutoff) "
                + "OR (c.completed=0 AND (c.installed_file_id IS NULL OR c.installed_file_id<>p.root_file_id))) "
                + "AND NOT EXISTS(SELECT 1 FROM directory_case_members m WHERE m.source_id=p.source_id AND m.root_file_id=p.root_file_id) "
                + "ORDER BY p.source_id,p.root_file_id LIMIT $limit);";
            cancellationToken.ThrowIfCancellationRequested(); return retired + command.ExecuteNonQuery();
        }, cancellationToken);
    }
}
