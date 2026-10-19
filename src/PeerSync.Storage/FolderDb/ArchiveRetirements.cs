using Microsoft.Data.Sqlite;

namespace PeerSync.Storage.FolderDb;

public enum ArchiveRetirementReason { Age = 1, Capacity = 2 }
public sealed record ArchiveRetirement(Guid Id, ArchiveRow Archive, byte[] RootId, byte[] FileId,
    ArchiveRetirementReason Reason, long PreparedTicks, long? CompletedTicks = null);

public sealed partial class FolderIndex
{
    // This is a policy authorization prepared under a verified native file
    // guard, not proof that an arbitrary missing archive was deleted by us.
    public Task<bool> ReserveArchiveRetirementAsync(ArchiveRetirement retirement, CancellationToken cancellationToken = default)
    {
        var frozen = FreezeRetirement(retirement);
        if (frozen.CompletedTicks is not null) throw new ArgumentException("New retirement is already completed.", nameof(retirement));
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireRetirementRoot(connection, transaction, frozen.RootId);
            var prior = FindRetirement(connection, transaction, frozen.Id);
            if (prior is not null) { RequireSameRetirement(prior, frozen); return false; }
            RequireArchiveRow(connection, transaction, frozen.Archive, allowAbsent: false);
            using var pending = connection.CreateCommand(); pending.Transaction = transaction;
            pending.CommandText = "SELECT COUNT(*) FROM apply_intents;";
            if ((long)pending.ExecuteScalar()! != 0) throw new InvalidOperationException("Recover pending application before archive retirement.");
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO archive_retirements(id,archive_id,original_path,archive_path,archived_ticks,size,file_hash,root_id,file_id,reason,prepared_ticks) "
                + "VALUES($id,$archive,$original,$path,$archived,$size,$hash,$root,$file,$reason,$prepared);";
            BindRetirement(command, frozen); command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO archive_retirement_sources(retirement_id,source_id) SELECT $id,source_id FROM archive_provenance WHERE archive_id=$archive AND (root_id IS NULL OR root_id=$root);";
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Archive policy source is missing."); return true;
        }, cancellationToken);
    }

    /// <summary>Caller has proven the authorized target absent under its pinned
    /// parent. Keep completion and exact-row removal atomic and FULL durable.</summary>
    public Task<bool> CompleteArchiveRetirementAsync(ArchiveRetirement expected, long completedTicks, CancellationToken cancellationToken = default)
    {
        var frozen = FreezeRetirement(expected);
        if (completedTicks < frozen.PreparedTicks || completedTicks > DateTime.MaxValue.Ticks)
            throw new ArgumentOutOfRangeException(nameof(completedTicks));
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireRetirementRoot(connection, transaction, frozen.RootId);
            var actual = FindRetirement(connection, transaction, frozen.Id);
            if (actual is null) return false;
            RequireSameRetirement(actual, frozen);
            if (actual.CompletedTicks is not null) return false;
            RequireArchiveRow(connection, transaction, frozen.Archive, allowAbsent: true);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "DELETE FROM archive WHERE id=$archive; UPDATE archive_retirements SET completed_ticks=$completed WHERE id=$id;";
            command.Parameters.AddWithValue("$archive", frozen.Archive.Id); command.Parameters.AddWithValue("$id", frozen.Id.ToString("N"));
            command.Parameters.AddWithValue("$completed", completedTicks); command.ExecuteNonQuery(); return true;
        }, cancellationToken);
    }

    public IEnumerable<ArchiveRetirement> ReadArchiveRetirements()
    {
        using var connection = database.OpenReader(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + RetirementColumns + " FROM archive_retirements ORDER BY prepared_ticks,id;";
        using var reader = command.ExecuteReader(); while (reader.Read()) yield return ReadRetirement(reader);
    }

    private const string RetirementColumns = "id,archive_id,original_path,archive_path,archived_ticks,size,file_hash,root_id,file_id,reason,prepared_ticks,completed_ticks";
    private static ArchiveRetirement ReadRetirement(SqliteDataReader r) => new(Guid.ParseExact(r.GetString(0), "N"),
        new(r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetInt64(5), r.GetFieldValue<byte[]>(6)),
        r.GetFieldValue<byte[]>(7), r.GetFieldValue<byte[]>(8), (ArchiveRetirementReason)r.GetInt32(9), r.GetInt64(10), r.IsDBNull(11) ? null : r.GetInt64(11));
    private static ArchiveRetirement? FindRetirement(SqliteConnection connection, SqliteTransaction transaction, Guid id)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT " + RetirementColumns + " FROM archive_retirements WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString("N")); using var reader = command.ExecuteReader(); return reader.Read() ? ReadRetirement(reader) : null;
    }
    private static ArchiveRetirement FreezeRetirement(ArchiveRetirement row)
    {
        ArgumentNullException.ThrowIfNull(row); FolderMaps.Archive.Validate(row.Archive);
        FolderMaps.RequireBytes(row.RootId, 24); FolderMaps.RequireBytes(row.FileId, 16);
        if (row.Id == Guid.Empty || row.Archive.Id <= 0 || row.Archive.FileHash is not { Length: 32 } || !Enum.IsDefined(row.Reason)
            || row.PreparedTicks <= 0 || row.PreparedTicks > DateTime.MaxValue.Ticks
            || row.CompletedTicks is { } completed && (completed < row.PreparedTicks || completed > DateTime.MaxValue.Ticks))
            throw new ArgumentException("Invalid archive retirement.", nameof(row));
        return row with { Archive = row.Archive with { FileHash = row.Archive.FileHash.ToArray() }, RootId = row.RootId.ToArray(), FileId = row.FileId.ToArray() };
    }
    private static void RequireSameRetirement(ArchiveRetirement actual, ArchiveRetirement expected)
    {
        if (actual.Id != expected.Id || actual.Reason != expected.Reason || actual.PreparedTicks != expected.PreparedTicks
            || !SameArchive(actual.Archive, expected.Archive) || !actual.RootId.AsSpan().SequenceEqual(expected.RootId)
            || !actual.FileId.AsSpan().SequenceEqual(expected.FileId)) throw new InvalidOperationException("Archive retirement changed.");
    }
    internal static bool SameArchive(ArchiveRow a, ArchiveRow b) => a.Id == b.Id && a.OriginalPath == b.OriginalPath && a.ArchivePath == b.ArchivePath
        && a.ArchivedTicks == b.ArchivedTicks && a.Size == b.Size && a.FileHash is not null && b.FileHash is not null && a.FileHash.AsSpan().SequenceEqual(b.FileHash);
    private static void RequireArchiveRow(SqliteConnection connection, SqliteTransaction transaction, ArchiveRow expected, bool allowAbsent)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT id,original_path,archive_path,archived_ticks,size,file_hash FROM archive WHERE id=$id;";
        command.Parameters.AddWithValue("$id", expected.Id); using var reader = command.ExecuteReader();
        if (!reader.Read()) { if (allowAbsent) return; throw new InvalidOperationException("Archive row disappeared before retirement."); }
        if (!SameArchive(FolderMaps.Archive.Read(reader), expected)) throw new InvalidOperationException("Archive row changed during retirement.");
    }
    private static void RequireRetirementRoot(SqliteConnection connection, SqliteTransaction transaction, byte[] expected)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT value FROM meta WHERE key='_root-identity';";
        if (command.ExecuteScalar() is not byte[] identity || !identity.AsSpan().SequenceEqual(expected)) throw new InvalidOperationException("Archive retirement root does not match the index.");
    }
    private static void BindRetirement(SqliteCommand command, ArchiveRetirement row)
    {
        command.Parameters.AddWithValue("$id", row.Id.ToString("N")); command.Parameters.AddWithValue("$archive", row.Archive.Id);
        command.Parameters.AddWithValue("$original", row.Archive.OriginalPath); command.Parameters.AddWithValue("$path", row.Archive.ArchivePath);
        command.Parameters.AddWithValue("$archived", row.Archive.ArchivedTicks); command.Parameters.AddWithValue("$size", row.Archive.Size);
        command.Parameters.AddWithValue("$hash", row.Archive.FileHash!); command.Parameters.AddWithValue("$root", row.RootId);
        command.Parameters.AddWithValue("$file", row.FileId); command.Parameters.AddWithValue("$reason", (int)row.Reason); command.Parameters.AddWithValue("$prepared", row.PreparedTicks);
    }
}
