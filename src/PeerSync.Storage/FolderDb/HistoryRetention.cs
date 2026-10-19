using Microsoft.Data.Sqlite;
using PeerSync.Core.Paths;

namespace PeerSync.Storage.FolderDb;

public sealed record HistoryArchiveProtection(byte[] RootId, byte[] FileId, string OriginalPath, string Destination,
    long Size, byte[] FileHash, long RetainedTicks, bool PublicCopy)
{
    public override string ToString() => "History retention protection (metadata redacted)";
}
public sealed record ArchiveRetentionCandidate(ArchiveRow Archive, long RetainedTicks);

public sealed partial class FolderIndex
{
    /// <summary>Caller verifies this exact location under a native guard. A
    /// pending policy reservation prevents reusing that source for new history.</summary>
    public bool CanRetainHistoryArchive(HistoryArchiveProtection protection)
    {
        var frozen = FreezeHistoryProtection(protection); if (frozen.PublicCopy) return true;
        using var connection = database.OpenReader(); return FindRetainableArchiveSource(connection, null, frozen) is not null;
    }

    public bool HasUnaccountedHistory(byte[] rootId)
    {
        FolderMaps.RequireBytes(rootId, 24);
        using var connection = database.OpenReader(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM meta m WHERE m.key GLOB '_history-*' AND NOT EXISTS(SELECT 1 FROM history_archive_links h WHERE h.marker_key=m.key AND h.root_id=$root));";
        command.Parameters.AddWithValue("$root", rootId); return (long)command.ExecuteScalar()! != 0;
    }

    public bool IsHistoryAccounted(string markerKey, byte[] rootId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markerKey); FolderMaps.RequireBytes(rootId, 24);
        using var connection = database.OpenReader(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM history_archive_links WHERE marker_key=$key AND root_id=$root);";
        command.Parameters.AddWithValue("$key", markerKey); command.Parameters.AddWithValue("$root", rootId);
        return (long)command.ExecuteScalar()! != 0;
    }

    public Guid? FindArchiveRetirementSource(Guid retirementId)
    {
        using var connection = database.OpenReader(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_id FROM archive_retirement_sources WHERE retirement_id=$id;";
        command.Parameters.AddWithValue("$id", retirementId.ToString("N"));
        return command.ExecuteScalar() is string source ? Guid.ParseExact(source, "N") : null;
    }

    /// <summary>Caller authenticates the existing marker and holds the verified
    /// location guard. Compare exact recovery bytes before establishing a legacy hold.</summary>
    public Task<bool> AccountExistingHistoryAsync(MetaRow marker, HistoryArchiveProtection protection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(marker);
        if (marker.Value is not { Length: > 0 and <= ApplyIntent.MaxPayloadBytes } || !marker.Key.StartsWith("_history-", StringComparison.Ordinal))
            throw new ArgumentException("Invalid existing history marker.", nameof(marker));
        var frozen = marker with { Value = marker.Value.ToArray() }; var retained = FreezeHistoryProtection(protection);
        return database.WriteDurableAsync((connection, transaction) =>
        {
            using var current = connection.CreateCommand(); current.Transaction = transaction;
            current.CommandText = "SELECT value FROM meta WHERE key=$key;"; current.Parameters.AddWithValue("$key", frozen.Key);
            if (current.ExecuteScalar() is not byte[] bytes || !bytes.AsSpan().SequenceEqual(frozen.Value)) return false;
            WriteHistoryProtection(connection, transaction, frozen, retained); return true;
        }, cancellationToken);
    }

    public IEnumerable<ArchiveRetentionCandidate> ArchivesByRetentionTime(byte[] rootId)
    {
        FolderMaps.RequireBytes(rootId, 24); return ReadArchiveRetentionCandidates(rootId.ToArray());
    }
    private IEnumerable<ArchiveRetentionCandidate> ReadArchiveRetentionCandidates(byte[] root)
    {
        using var connection = database.OpenReader(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT a.id,a.original_path,a.archive_path,a.archived_ticks,a.size,a.file_hash,MAX(a.archived_ticks,COALESCE(MAX(h.retained_ticks),0)) AS retained "
            + "FROM archive a LEFT JOIN archive_provenance p ON p.archive_id=a.id LEFT JOIN history_archive_links h ON h.source_id=p.source_id AND h.root_id=$root "
            + "GROUP BY a.id ORDER BY retained,a.id;";
        command.Parameters.AddWithValue("$root", root); using var reader = command.ExecuteReader();
        while (reader.Read()) yield return new(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetFieldValue<byte[]>(5)), reader.GetInt64(6));
    }

    private static HistoryArchiveProtection FreezeHistoryProtection(HistoryArchiveProtection protection)
    {
        ArgumentNullException.ThrowIfNull(protection); FolderMaps.RequireBytes(protection.RootId, 24); FolderMaps.RequireBytes(protection.FileId, 16);
        FolderMaps.RequireBytes(protection.FileHash, 32); _ = PathRules.Key(protection.OriginalPath);
        if (protection.Size < 0 || protection.RetainedTicks <= 0 || protection.RetainedTicks > DateTime.MaxValue.Ticks)
            throw new ArgumentException("Invalid history retention metadata.", nameof(protection));
        if (protection.PublicCopy)
        {
            if (!PathRules.IsValid(protection.Destination) || PathRules.Key(protection.Destination) == PathRules.Key(protection.OriginalPath))
                throw new ArgumentException("Invalid public history location.", nameof(protection));
        }
        else FolderMaps.Archive.Validate(new(0, protection.OriginalPath, protection.Destination, protection.RetainedTicks, protection.Size, protection.FileHash));
        return protection with { RootId = protection.RootId.ToArray(), FileId = protection.FileId.ToArray(), FileHash = protection.FileHash.ToArray() };
    }

    private static Guid? FindRetainableArchiveSource(SqliteConnection connection, SqliteTransaction? transaction, HistoryArchiveProtection protection)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT p.source_id FROM archive_provenance p WHERE p.archive_id IS NOT NULL AND p.original_path=$original AND p.archive_path=$path "
            + "AND p.size=$size AND p.file_hash=$hash AND (p.root_id IS NULL OR p.root_id=$root) "
            + "AND NOT EXISTS(SELECT 1 FROM archive_retirements r WHERE r.archive_id=p.archive_id AND r.completed_ticks IS NULL) LIMIT 2;";
        command.Parameters.AddWithValue("$original", protection.OriginalPath); command.Parameters.AddWithValue("$path", protection.Destination);
        command.Parameters.AddWithValue("$size", protection.Size); command.Parameters.AddWithValue("$hash", protection.FileHash); command.Parameters.AddWithValue("$root", protection.RootId);
        using var reader = command.ExecuteReader(); if (!reader.Read()) return null; var id = Guid.ParseExact(reader.GetString(0), "N");
        if (reader.Read()) throw new InvalidOperationException("History archive source is ambiguous."); return id;
    }

    private static void WriteHistoryProtection(SqliteConnection connection, SqliteTransaction transaction, MetaRow marker, HistoryArchiveProtection protection)
    {
        RequireBlockReferenceRoot(connection, transaction, protection.RootId);
        Guid? source = protection.PublicCopy ? null : FindRetainableArchiveSource(connection, transaction, protection)
            ?? throw new InvalidOperationException("History archive is missing or reserved for retirement.");
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        if (source is { } retainedSource)
        {
            command.CommandText = "DELETE FROM archive_policy_releases WHERE source_id=$source AND root_id=$root;";
            command.Parameters.AddWithValue("$source", retainedSource.ToString("N")); command.Parameters.AddWithValue("$root", protection.RootId);
            command.ExecuteNonQuery(); command.Parameters.Clear();
        }
        command.CommandText = "INSERT INTO history_archive_links(marker_key,source_id,root_id,file_id,retained_ticks) VALUES($key,$source,$root,$file,$ticks) "
            + "ON CONFLICT(marker_key) DO UPDATE SET source_id=excluded.source_id,root_id=excluded.root_id,file_id=excluded.file_id,retained_ticks=excluded.retained_ticks;";
        command.Parameters.AddWithValue("$key", marker.Key); command.Parameters.AddWithValue("$source", source is { } id ? id.ToString("N") : DBNull.Value);
        command.Parameters.AddWithValue("$root", protection.RootId); command.Parameters.AddWithValue("$file", protection.FileId); command.Parameters.AddWithValue("$ticks", protection.RetainedTicks);
        command.ExecuteNonQuery();
    }
}
