using Microsoft.Data.Sqlite;

namespace PeerSync.Storage.FolderDb;

public readonly record struct CasePreservationRetry(int Attempts, long FailedTicks, long NextTicks);

public sealed partial class FolderIndex
{
    public long? ReadNextCasePreservationTicks(byte[] rootId, long nowTicks)
    {
        byte[] root = FreezeSweep(rootId, nowTicks, 1, MaximumPendingCaseSources);
        using var connection = database.OpenReader(); using var transaction = connection.BeginTransaction(deferred: true);
        RequireBlockReferenceRoot(connection, transaction, root); using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT MIN(CASE WHEN r.source_id IS NULL OR r.failed_ticks>$now OR r.next_ticks<=$now THEN $now ELSE r.next_ticks END) "
            + "FROM case_preservations c LEFT JOIN case_preservation_retries r ON r.source_id=c.source_id WHERE c.completed=0 AND c.root_id=$root;";
        command.Parameters.AddWithValue("$root", root); command.Parameters.AddWithValue("$now", nowTicks);
        return command.ExecuteScalar() is long ticks ? ticks : null;
    }

    public CasePreservationRetry? ReadCasePreservationRetry(byte[] sourceId)
    {
        FolderMaps.RequireBytes(sourceId, 32); using var connection = database.OpenReader(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT attempts,failed_ticks,next_ticks FROM case_preservation_retries WHERE source_id=$id;";
        command.Parameters.AddWithValue("$id", sourceId); using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetInt32(0), reader.GetInt64(1), reader.GetInt64(2)) : null;
    }

    public Task<CasePreservationRetry> RecordCasePreservationFailureAsync(PendingCasePreservation expected, long nowTicks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected); RequireStagingNow(nowTicks);
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequirePendingCase(connection, transaction, expected); using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT attempts,failed_ticks FROM case_preservation_retries WHERE source_id=$id;";
            command.Parameters.AddWithValue("$id", expected.Source.SourceId); int attempt = 1;
            using (var reader = command.ExecuteReader()) if (reader.Read() && reader.GetInt64(1) <= nowTicks) attempt = Math.Min(7, reader.GetInt32(0) + 1);
            long delay = Math.Min(600L, 10L << (attempt - 1)) * TimeSpan.TicksPerSecond;
            var retry = new CasePreservationRetry(attempt, nowTicks, nowTicks + Math.Min(delay, DateTime.MaxValue.Ticks - nowTicks));
            command.CommandText = "INSERT INTO case_preservation_retries(source_id,attempts,failed_ticks,next_ticks) VALUES($id,$attempt,$failed,$next) "
                + "ON CONFLICT(source_id) DO UPDATE SET attempts=excluded.attempts,failed_ticks=excluded.failed_ticks,next_ticks=excluded.next_ticks;";
            command.Parameters.AddWithValue("$attempt", retry.Attempts); command.Parameters.AddWithValue("$failed", retry.FailedTicks);
            command.Parameters.AddWithValue("$next", retry.NextTicks); cancellationToken.ThrowIfCancellationRequested(); command.ExecuteNonQuery(); return retry;
        }, cancellationToken);
    }

    public Task<int> CompactCompletedCaseSourcesAsync(byte[] rootId, long nowTicks, int limit = MaximumPendingCaseSources, CancellationToken cancellationToken = default)
    {
        byte[] root = FreezeSweep(rootId, nowTicks, 1, limit);
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireBlockReferenceRoot(connection, transaction, root); using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE case_preservations SET record_body=NULL,signature=NULL,source_device=NULL WHERE source_id IN ("
                + "SELECT source_id FROM case_preservations WHERE root_id=$root AND completed=1 AND record_body IS NOT NULL AND completed_ticks <= $cutoff AND (retained_until_ticks IS NULL OR retained_until_ticks<=$now) "
                + "ORDER BY completed_ticks,source_id LIMIT $limit);";
            command.Parameters.AddWithValue("$root", root); command.Parameters.AddWithValue("$cutoff", nowTicks - TimeSpan.TicksPerDay);
            command.Parameters.AddWithValue("$now", nowTicks);
            command.Parameters.AddWithValue("$limit", limit); cancellationToken.ThrowIfCancellationRequested(); return command.ExecuteNonQuery();
        }, cancellationToken);
    }

    private static void ClearCaseRetry(SqliteConnection connection, SqliteTransaction transaction, byte[] sourceId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "DELETE FROM case_preservation_retries WHERE source_id=$id;";
        command.Parameters.AddWithValue("$id", sourceId); command.ExecuteNonQuery();
    }
}
