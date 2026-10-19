using Microsoft.Data.Sqlite;

namespace PeerSync.Storage.FolderDb;

public sealed record CaptureRetirement(Guid Id, byte[] RootId, byte[] MarkerId, byte[] DataId, byte[] ExpectedHash, int ExpectedLength,
    long CapturedTicks, int PrefixLength, byte[] PrefixHash, long PreparedTicks, Guid SnapshotId, byte[]? ReplacementId = null)
{
    public override string ToString() => "Owned partial capture retirement (redacted)";
}
public sealed record CaptureRetirementCompletion(bool Completed, bool Referenced);

public sealed partial class FolderIndex
{
    /// <summary>Caller holds independent, proof-bound native data/marker guards
    /// and both maintenance gates. Reserve FULL before deleting any prefix.</summary>
    public Task<bool> ReserveCaptureRetirementAsync(CaptureRetirement retirement, CancellationToken cancellationToken = default)
    {
        var frozen = FreezeCaptureRetirement(retirement);
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireSweepSnapshot(connection, transaction, frozen.SnapshotId, frozen.RootId);
            var actual = FindCaptureRetirement(connection, transaction, frozen.Id);
            if (actual is not null) { RequireSameCaptureRetirement(actual, frozen); return false; }
            if (frozen.PreparedTicks - frozen.CapturedTicks < TimeSpan.TicksPerDay) return false;
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO capture_retirements SELECT $id,$root,$marker,$data,$hash,$length,$captured,$prefixLength,$prefixHash,$prepared,$snapshot,$replacement "
                + "WHERE ($replacement IS NOT NULL OR NOT EXISTS(SELECT 1 FROM block_references WHERE snapshot_id=$snapshot AND hash=$hash)) "
                + "AND ($replacement IS NULL OR EXISTS(SELECT 1 FROM block_objects WHERE hash=$hash AND root_id=$root AND length=$length AND file_id=$replacement)) "
                + "AND ((SELECT COUNT(*) FROM capture_retirements)+(SELECT COUNT(*) FROM block_deletions))<1000;";
            BindCaptureRetirement(command, frozen); return command.ExecuteNonQuery() == 1;
        }, cancellationToken);
    }

    public bool HasCaptureRetirement(Guid captureId)
    {
        using var connection = database.OpenReader(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM capture_retirements WHERE id=$id);"; command.Parameters.AddWithValue("$id", captureId.ToString("N"));
        return (long)command.ExecuteScalar()! != 0;
    }

    public IEnumerable<CaptureRetirement> ReadCaptureRetirements()
    {
        using var connection = database.OpenReader(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + CaptureRetirementColumns + " FROM capture_retirements ORDER BY prepared_ticks,id LIMIT 1000;";
        using var reader = command.ExecuteReader(); while (reader.Read()) yield return ReadCaptureRetirement(reader);
    }

    public bool IsCaptureHashReferenced(Guid freshSnapshot, byte[] rootId, byte[] expectedHash)
    {
        FolderMaps.RequireBytes(rootId, 24); FolderMaps.RequireBytes(expectedHash, 32);
        using var connection = database.OpenReader(); using var transaction = connection.BeginTransaction(deferred: true);
        RequireSweepSnapshot(connection, transaction, freshSnapshot, rootId);
        return CaptureHashReferenced(connection, transaction, freshSnapshot, expectedHash);
    }

    public bool IsCaptureReplacementKnown(Guid freshSnapshot, byte[] rootId, byte[] expectedHash, int length, byte[] fileId)
    {
        FolderMaps.RequireBytes(rootId, 24); FolderMaps.RequireBytes(expectedHash, 32); FolderMaps.RequireBytes(fileId, 16);
        using var connection = database.OpenReader(); using var transaction = connection.BeginTransaction(deferred: true);
        RequireSweepSnapshot(connection, transaction, freshSnapshot, rootId);
        return CaptureReplacementKnown(connection, transaction, rootId, expectedHash, length, fileId);
    }

    public Task<bool> CancelCaptureRetirementAsync(CaptureRetirement expected, Guid freshSnapshot, CancellationToken cancellationToken = default)
    {
        var frozen = FreezeCaptureRetirement(expected);
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireSweepSnapshot(connection, transaction, freshSnapshot, frozen.RootId);
            var actual = FindCaptureRetirement(connection, transaction, frozen.Id); if (actual is null) return false;
            RequireSameCaptureRetirement(actual, frozen); DeleteCaptureRetirement(connection, transaction, frozen.Id); return true;
        }, cancellationToken);
    }

    /// <summary>Caller has proved the old prefix and its verified proof absent.
    /// New demand never makes already missing bytes ready or drops needed work.</summary>
    public Task<CaptureRetirementCompletion> CompleteCaptureRetirementAsync(CaptureRetirement expected, Guid freshSnapshot, CancellationToken cancellationToken = default) =>
        CompleteCaptureRetirementCoreAsync(expected, freshSnapshot, false, cancellationToken);

    /// <summary>Caller retains the verified complete replacement's read guard
    /// through this FULL commit. Its catalog identity must still match.</summary>
    public Task<CaptureRetirementCompletion> CompleteProtectedCaptureRetirementAsync(CaptureRetirement expected, Guid freshSnapshot,
        bool replacementRetained, CancellationToken cancellationToken = default) =>
        CompleteCaptureRetirementCoreAsync(expected, freshSnapshot, replacementRetained, cancellationToken);

    private Task<CaptureRetirementCompletion> CompleteCaptureRetirementCoreAsync(CaptureRetirement expected, Guid freshSnapshot, bool replacementRetained, CancellationToken cancellationToken)
    {
        var frozen = FreezeCaptureRetirement(expected);
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireSweepSnapshot(connection, transaction, freshSnapshot, frozen.RootId);
            var actual = FindCaptureRetirement(connection, transaction, frozen.Id); if (actual is null) return new CaptureRetirementCompletion(false, false);
            RequireSameCaptureRetirement(actual, frozen);
            bool referenced = CaptureHashReferenced(connection, transaction, freshSnapshot, frozen.ExpectedHash);
            if (replacementRetained && (frozen.ReplacementId is null || !CaptureReplacementKnown(connection, transaction, frozen.RootId, frozen.ExpectedHash, frozen.ExpectedLength, frozen.ReplacementId)))
                throw new InvalidOperationException("Capture replacement is no longer cataloged.");
            if (referenced && !replacementRetained)
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "UPDATE local_files SET content_ready=0,flags=flags|4 WHERE type=0 AND deleted=0 AND record_body IS NOT NULL;"; command.ExecuteNonQuery();
            }
            DeleteCaptureRetirement(connection, transaction, frozen.Id); return new CaptureRetirementCompletion(true, referenced);
        }, cancellationToken);
    }

    private const string CaptureRetirementColumns = "id,root_id,marker_id,data_id,expected_hash,expected_length,captured_ticks,prefix_length,prefix_hash,prepared_ticks,snapshot_id,replacement_id";
    private static CaptureRetirement ReadCaptureRetirement(SqliteDataReader reader) => new(Guid.ParseExact(reader.GetString(0), "N"), reader.GetFieldValue<byte[]>(1), reader.GetFieldValue<byte[]>(2),
        reader.GetFieldValue<byte[]>(3), reader.GetFieldValue<byte[]>(4), reader.GetInt32(5), reader.GetInt64(6), reader.GetInt32(7), reader.GetFieldValue<byte[]>(8), reader.GetInt64(9), Guid.ParseExact(reader.GetString(10), "N"), reader.IsDBNull(11) ? null : reader.GetFieldValue<byte[]>(11));
    private static CaptureRetirement? FindCaptureRetirement(SqliteConnection connection, SqliteTransaction transaction, Guid id)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT " + CaptureRetirementColumns + " FROM capture_retirements WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString("N")); using var reader = command.ExecuteReader(); return reader.Read() ? ReadCaptureRetirement(reader) : null;
    }
    private static bool CaptureHashReferenced(SqliteConnection connection, SqliteTransaction transaction, Guid snapshot, byte[] hash)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT EXISTS(SELECT 1 FROM block_references WHERE snapshot_id=$snapshot AND hash=$hash);";
        command.Parameters.AddWithValue("$snapshot", snapshot.ToString("N")); command.Parameters.AddWithValue("$hash", hash); return (long)command.ExecuteScalar()! != 0;
    }
    private static bool CaptureReplacementKnown(SqliteConnection connection, SqliteTransaction transaction, byte[] root, byte[] hash, int length, byte[] fileId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM block_objects WHERE hash=$hash AND root_id=$root AND length=$length AND file_id=$file);";
        command.Parameters.AddWithValue("$hash", hash); command.Parameters.AddWithValue("$root", root); command.Parameters.AddWithValue("$length", length); command.Parameters.AddWithValue("$file", fileId);
        return (long)command.ExecuteScalar()! != 0;
    }
    private static void DeleteCaptureRetirement(SqliteConnection connection, SqliteTransaction transaction, Guid id)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "DELETE FROM capture_retirements WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString("N")); command.ExecuteNonQuery();
    }
    private static CaptureRetirement FreezeCaptureRetirement(CaptureRetirement row)
    {
        ArgumentNullException.ThrowIfNull(row); FolderMaps.RequireBytes(row.RootId, 24); FolderMaps.RequireBytes(row.MarkerId, 16); FolderMaps.RequireBytes(row.DataId, 16);
        FolderMaps.RequireBytes(row.ExpectedHash, 32); FolderMaps.RequireBytes(row.PrefixHash, 32);
        if (row.ReplacementId is { } replacement) { FolderMaps.RequireBytes(replacement, 16); if (replacement.AsSpan().SequenceEqual(row.DataId)) throw new ArgumentException("A prefix cannot replace itself.", nameof(row)); }
        if (row.Id == Guid.Empty || row.SnapshotId == Guid.Empty || row.ExpectedLength is <= 0 or > 8388608 || row.PrefixLength < 0 || row.PrefixLength >= row.ExpectedLength
            || row.CapturedTicks <= 0 || row.PreparedTicks < row.CapturedTicks || row.PreparedTicks > DateTime.MaxValue.Ticks) throw new ArgumentException("Invalid capture retirement.", nameof(row));
        return row with { RootId = row.RootId.ToArray(), MarkerId = row.MarkerId.ToArray(), DataId = row.DataId.ToArray(), ExpectedHash = row.ExpectedHash.ToArray(), PrefixHash = row.PrefixHash.ToArray(), ReplacementId = row.ReplacementId?.ToArray() };
    }
    private static void RequireSameCaptureRetirement(CaptureRetirement actual, CaptureRetirement expected)
    {
        if (actual.Id != expected.Id || actual.SnapshotId != expected.SnapshotId || actual.ExpectedLength != expected.ExpectedLength || actual.PrefixLength != expected.PrefixLength
            || actual.CapturedTicks != expected.CapturedTicks || actual.PreparedTicks != expected.PreparedTicks || !actual.RootId.AsSpan().SequenceEqual(expected.RootId)
            || !actual.MarkerId.AsSpan().SequenceEqual(expected.MarkerId) || !actual.DataId.AsSpan().SequenceEqual(expected.DataId)
            || (actual.ReplacementId is null) != (expected.ReplacementId is null) || !actual.ReplacementId.AsSpan().SequenceEqual(expected.ReplacementId)
            || !actual.ExpectedHash.AsSpan().SequenceEqual(expected.ExpectedHash) || !actual.PrefixHash.AsSpan().SequenceEqual(expected.PrefixHash)) throw new InvalidOperationException("Capture retirement changed.");
    }
    private static void BindCaptureRetirement(SqliteCommand command, CaptureRetirement row)
    {
        command.Parameters.AddWithValue("$id", row.Id.ToString("N")); command.Parameters.AddWithValue("$root", row.RootId); command.Parameters.AddWithValue("$marker", row.MarkerId);
        command.Parameters.AddWithValue("$data", row.DataId); command.Parameters.AddWithValue("$hash", row.ExpectedHash); command.Parameters.AddWithValue("$length", row.ExpectedLength);
        command.Parameters.AddWithValue("$captured", row.CapturedTicks); command.Parameters.AddWithValue("$prefixLength", row.PrefixLength); command.Parameters.AddWithValue("$prefixHash", row.PrefixHash);
        command.Parameters.AddWithValue("$prepared", row.PreparedTicks); command.Parameters.AddWithValue("$snapshot", row.SnapshotId.ToString("N"));
        command.Parameters.AddWithValue("$replacement", (object?)row.ReplacementId ?? DBNull.Value);
    }
}
