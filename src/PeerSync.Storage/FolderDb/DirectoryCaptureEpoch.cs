using Microsoft.Data.Sqlite;

namespace PeerSync.Storage.FolderDb;

public sealed partial class FolderIndex
{
    public long ReadDirectoryCaptureEpoch()
    { using var connection = database.OpenReader(); return ReadDirectoryCaptureEpoch(connection, null); }
    /// <summary>Caller has revalidated the exact indexed input manifest under
    /// the folder gate. This is a conditional witness binding, not validation.</summary>
    public Task<DirectoryCaptureState> BindDirectoryCaptureEpochAsync(DirectoryCaptureState expected, long epoch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected); ArgumentOutOfRangeException.ThrowIfNegative(epoch); var prior = FreezeCapture(expected);
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireCaptureExpected(connection, transaction, prior); RequireDirectoryCaptureEpoch(connection, transaction, epoch);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE directory_captures SET input_epoch=$epoch,last_used_ticks=$now WHERE parent_key=$key;";
            command.Parameters.AddWithValue("$key", PeerSync.Core.Paths.PathRules.Key(prior.ParentPath)); command.Parameters.AddWithValue("$epoch", epoch);
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks); command.ExecuteNonQuery();
            return FindDirectoryCapture(connection, transaction, PeerSync.Core.Paths.PathRules.Key(prior.ParentPath))!;
        }, cancellationToken);
    }
    private static long ReadDirectoryCaptureEpoch(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT epoch FROM directory_capture_clock WHERE id=1;";
        return command.ExecuteScalar() is long epoch && epoch >= 0 ? epoch : throw new InvalidDataException("Directory capture epoch is unavailable.");
    }
    private static void RequireDirectoryCaptureEpoch(SqliteConnection connection, SqliteTransaction transaction, long epoch)
    { if (ReadDirectoryCaptureEpoch(connection, transaction) != epoch) throw new InvalidOperationException("Directory capture input changed."); }
}
