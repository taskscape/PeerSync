using Microsoft.Data.Sqlite;

namespace PeerSync.Storage.FolderDb;

public sealed record BlockObservationBatch(int Visited, byte[]? NextHash);
public sealed record BlockSweepCandidate(BlockObjectRow OwnedBlock, long FirstUnreferencedTicks)
{
    public override string ToString() => "Owned block retirement candidate (redacted)";
}
public enum BlockDeletionReason { Age = 1, Capacity = 2 }
public sealed record BlockDeletionIntent(Guid Id, BlockSweepCandidate Candidate, long PreparedTicks, Guid SnapshotId, BlockDeletionReason Reason)
{
    public override string ToString() => "Owned block deletion intent (redacted)";
}
public sealed record BlockDeletionCompletion(bool Completed, bool Referenced, bool CatalogRemoved);

public sealed partial class FolderIndex
{
    public Task<BlockObservationBatch> ObserveBlockOrphansAsync(Guid snapshot, byte[] rootId, long nowTicks, int archiveDays,
        byte[]? afterHash = null, int limit = 1000, CancellationToken cancellationToken = default)
    {
        byte[] root = FreezeSweep(rootId, nowTicks, archiveDays, limit); byte[]? cursor = afterHash?.ToArray();
        if (cursor is not null) FolderMaps.RequireBytes(cursor, 32);
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireSweepSnapshot(connection, transaction, snapshot, root);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT OR IGNORE INTO block_retention_state VALUES($root,$now,$until);";
            command.Parameters.AddWithValue("$root", root); command.Parameters.AddWithValue("$now", nowTicks);
            command.Parameters.AddWithValue("$until", nowTicks + Math.Min(DateTime.MaxValue.Ticks - nowTicks, archiveDays * TimeSpan.TicksPerDay)); command.ExecuteNonQuery();
            command.CommandText = "SELECT hash FROM block_objects WHERE root_id=$root " + (cursor is null ? "" : "AND hash>$after ") + "ORDER BY hash LIMIT $limit;";
            command.Parameters.AddWithValue("$limit", limit); if (cursor is not null) command.Parameters.AddWithValue("$after", cursor);
            var hashes = new List<byte[]>(limit); using (var reader = command.ExecuteReader()) while (reader.Read()) hashes.Add(reader.GetFieldValue<byte[]>(0));
            command.CommandText = "INSERT OR IGNORE INTO block_orphans(hash,first_unreferenced_ticks) SELECT $hash,$now "
                + "WHERE NOT EXISTS(SELECT 1 FROM block_references WHERE snapshot_id=$snapshot AND hash=$hash);";
            command.Parameters.AddWithValue("$snapshot", snapshot.ToString("N")); var hash = command.Parameters.Add("$hash", SqliteType.Blob);
            foreach (var value in hashes) { cancellationToken.ThrowIfCancellationRequested(); hash.Value = value; command.ExecuteNonQuery(); }
            return new BlockObservationBatch(hashes.Count, hashes.Count == 0 ? null : hashes[^1].ToArray());
        }, cancellationToken);
    }

    public IEnumerable<BlockSweepCandidate> ReadBlockSweepCandidates(Guid snapshot, byte[] rootId, long nowTicks, int archiveDays,
        long capacityBytes, int limit = 1000)
    {
        byte[] root = FreezeSweep(rootId, nowTicks, archiveDays, limit); ArgumentOutOfRangeException.ThrowIfNegative(capacityBytes);
        return ReadCandidates();
        IEnumerable<BlockSweepCandidate> ReadCandidates()
        {
            using var connection = database.OpenReader(); using var transaction = connection.BeginTransaction(deferred: true);
            RequireSweepSnapshot(connection, transaction, snapshot, root);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT b.hash,b.length,b.root_id,b.file_id,b.captured_ticks,b.last_used_ticks,o.first_unreferenced_ticks "
                + EligibleSweepFrom + " AND NOT EXISTS(SELECT 1 FROM block_deletions d WHERE d.hash=b.hash) ORDER BY o.first_unreferenced_ticks,b.hash LIMIT $limit;";
            BindSweep(command, snapshot, root, nowTicks, archiveDays, capacityBytes); command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader(); while (reader.Read()) yield return new(ReadOwnedBlock(reader, 0), reader.GetInt64(6));
        }
    }

    /// <summary>Caller holds verified independent native bytes and both gates.
    /// This FULL reservation must return before native deletion begins.</summary>
    public Task<bool> ReserveBlockDeletionAsync(BlockDeletionIntent intent, int archiveDays, long capacityBytes, CancellationToken cancellationToken = default)
    {
        var frozen = FreezeDeletion(intent); _ = FreezeSweep(frozen.Candidate.OwnedBlock.RootId, frozen.PreparedTicks, archiveDays, 1000);
        ArgumentOutOfRangeException.ThrowIfNegative(capacityBytes);
        return database.WriteDurableAsync((connection, transaction) =>
        {
            var row = frozen.Candidate.OwnedBlock; RequireSweepSnapshot(connection, transaction, frozen.SnapshotId, row.RootId);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*) " + EligibleSweepFrom + " AND b.hash=$hash AND b.length=$length AND b.file_id=$file "
                + "AND b.captured_ticks=$captured AND b.last_used_ticks=$used AND o.first_unreferenced_ticks=$first "
                + "AND (SELECT COUNT(*) FROM block_deletions)<1000 AND NOT EXISTS(SELECT 1 FROM block_deletions WHERE hash=b.hash);";
            BindSweep(command, frozen.SnapshotId, row.RootId, frozen.PreparedTicks, archiveDays, capacityBytes); BindOwned(command, frozen.Candidate);
            if ((long)command.ExecuteScalar()! != 1) return false;
            var reason = DeletionReason(frozen.Candidate.FirstUnreferencedTicks, frozen.PreparedTicks, archiveDays);
            if (frozen.Reason != reason) throw new ArgumentException("Deletion reason does not match its policy.", nameof(intent));
            command.CommandText = "INSERT INTO block_deletions VALUES($id,$hash,$length,$root,$file,$captured,$used,$first,$now,$snapshot,$reason);";
            command.Parameters.AddWithValue("$id", frozen.Id.ToString("N")); command.Parameters.AddWithValue("$reason", (int)reason); command.ExecuteNonQuery(); return true;
        }, cancellationToken);
    }

    /// <summary>Revalidate a prepared deletion against a newly activated snapshot.
    /// New references, registration or changed grace invalidate the old intent.</summary>
    public Task<bool> CanContinueBlockDeletionAsync(BlockDeletionIntent intent, Guid freshSnapshot, long nowTicks, int archiveDays,
        long capacityBytes, CancellationToken cancellationToken = default)
    {
        var frozen = FreezeDeletion(intent); _ = FreezeSweep(frozen.Candidate.OwnedBlock.RootId, nowTicks, archiveDays, 1000);
        ArgumentOutOfRangeException.ThrowIfNegative(capacityBytes);
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireSweepSnapshot(connection, transaction, freshSnapshot, frozen.Candidate.OwnedBlock.RootId); RequireDeletion(connection, transaction, frozen);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*) " + EligibleSweepFrom + " AND b.hash=$hash AND b.length=$length AND b.file_id=$file "
                + "AND b.captured_ticks=$captured AND b.last_used_ticks=$used AND o.first_unreferenced_ticks=$first;";
            BindSweep(command, freshSnapshot, frozen.Candidate.OwnedBlock.RootId, nowTicks, archiveDays, capacityBytes); BindOwned(command, frozen.Candidate);
            return (long)command.ExecuteScalar()! == 1;
        }, cancellationToken);
    }

    public Task<bool> CancelBlockDeletionAsync(BlockDeletionIntent intent, Guid freshSnapshot, CancellationToken cancellationToken = default)
    {
        var frozen = FreezeDeletion(intent);
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireSweepSnapshot(connection, transaction, freshSnapshot, frozen.Candidate.OwnedBlock.RootId);
            if (!RequireDeletion(connection, transaction, frozen, allowAbsent: true)) return false;
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "DELETE FROM block_deletions WHERE id=$id;"; command.Parameters.AddWithValue("$id", frozen.Id.ToString("N"));
            command.ExecuteNonQuery(); return true;
        }, cancellationToken);
    }

    /// <summary>Caller proves native absence under the pinned parent. Complete
    /// even if references arrived after a crash, withholding stale readiness.</summary>
    public Task<BlockDeletionCompletion> CompleteBlockDeletionAsync(BlockDeletionIntent intent, Guid freshSnapshot, CancellationToken cancellationToken = default)
    {
        var frozen = FreezeDeletion(intent);
        return database.WriteDurableAsync((connection, transaction) =>
        {
            var row = frozen.Candidate.OwnedBlock; RequireSweepSnapshot(connection, transaction, freshSnapshot, row.RootId);
            if (!RequireDeletion(connection, transaction, frozen, allowAbsent: true)) return new BlockDeletionCompletion(false, false, false);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM block_references WHERE snapshot_id=$snapshot AND hash=$hash);";
            command.Parameters.AddWithValue("$snapshot", freshSnapshot.ToString("N")); BindOwned(command, frozen.Candidate);
            bool referenced = (long)command.ExecuteScalar()! != 0;
            if (referenced)
            {
                // A signed manifest can outlive its mutable chunk hints. Withhold
                // every live local row rather than miss such a publication.
                command.CommandText = "UPDATE local_files SET content_ready=0,flags=flags|4 WHERE type=0 AND deleted=0 AND record_body IS NOT NULL;"; command.ExecuteNonQuery();
            }
            command.CommandText = "DELETE FROM block_objects WHERE hash=$hash AND length=$length AND root_id=$root AND file_id=$file AND captured_ticks=$captured AND last_used_ticks=$used;";
            command.Parameters.AddWithValue("$root", row.RootId); bool removed = command.ExecuteNonQuery() == 1;
            command.CommandText = "DELETE FROM block_deletions WHERE id=$id;"; command.Parameters.AddWithValue("$id", frozen.Id.ToString("N")); command.ExecuteNonQuery();
            return new BlockDeletionCompletion(true, referenced, removed);
        }, cancellationToken);
    }

    public IEnumerable<BlockDeletionIntent> ReadBlockDeletionIntents()
    {
        using var connection = database.OpenReader(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + DeletionColumns + " FROM block_deletions ORDER BY prepared_ticks,id LIMIT 1000;";
        using var reader = command.ExecuteReader(); while (reader.Read()) yield return ReadDeletion(reader);
    }

    public long ReadOwnedBlockBytes()
    {
        using var connection = database.OpenReader(); using var command = connection.CreateCommand(); command.CommandText = "SELECT COALESCE(SUM(length),0) FROM block_objects;";
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>Caller proves an unreferenced catalog path absent under its
    /// pinned parent. This reconciles stale metadata; no native deletion occurs.</summary>
    public Task<bool> ForgetMissingBlockAsync(BlockSweepCandidate candidate, Guid freshSnapshot, CancellationToken cancellationToken = default)
    {
        var frozen = FreezeSweepCandidate(candidate);
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireSweepSnapshot(connection, transaction, freshSnapshot, frozen.OwnedBlock.RootId);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "DELETE FROM block_objects WHERE hash=$hash AND length=$length AND root_id=$root AND file_id=$file AND captured_ticks=$captured AND last_used_ticks=$used "
                + "AND NOT EXISTS(SELECT 1 FROM block_deletions d WHERE d.hash=block_objects.hash) AND NOT EXISTS(SELECT 1 FROM block_references r WHERE r.snapshot_id=$snapshot AND r.hash=block_objects.hash);";
            BindOwned(command, frozen); command.Parameters.AddWithValue("$root", frozen.OwnedBlock.RootId); command.Parameters.AddWithValue("$snapshot", freshSnapshot.ToString("N"));
            return command.ExecuteNonQuery() == 1;
        }, cancellationToken);
    }

    private const string EligibleSweepFrom = "FROM block_objects b JOIN block_orphans o ON o.hash=b.hash JOIN block_retention_state g ON g.root_id=b.root_id "
        + "WHERE b.root_id=$root AND (b.legacy_retention=0 OR g.legacy_until_ticks<=$now) AND o.first_unreferenced_ticks<=$grace "
        + "AND (o.first_unreferenced_ticks<$age OR (SELECT COALESCE(SUM(length),0) FROM block_objects)>$cap) "
        + "AND NOT EXISTS(SELECT 1 FROM block_references r WHERE r.snapshot_id=$snapshot AND r.hash=b.hash)";
    private const string DeletionColumns = "id,hash,length,root_id,file_id,captured_ticks,last_used_ticks,first_unreferenced_ticks,prepared_ticks,snapshot_id,reason";
    private static BlockDeletionReason DeletionReason(long first, long now, int days) => first < Math.Max(0, now - days * TimeSpan.TicksPerDay) ? BlockDeletionReason.Age : BlockDeletionReason.Capacity;
    private static BlockObjectRow ReadOwnedBlock(SqliteDataReader reader, int offset) => new(reader.GetFieldValue<byte[]>(offset), reader.GetInt32(offset + 1), reader.GetFieldValue<byte[]>(offset + 2),
        reader.GetFieldValue<byte[]>(offset + 3), reader.GetInt64(offset + 4), reader.GetInt64(offset + 5));
    private static BlockDeletionIntent ReadDeletion(SqliteDataReader reader) => new(Guid.ParseExact(reader.GetString(0), "N"), new(ReadOwnedBlock(reader, 1), reader.GetInt64(7)),
        reader.GetInt64(8), Guid.ParseExact(reader.GetString(9), "N"), (BlockDeletionReason)reader.GetInt32(10));
    private static void BindOwned(SqliteCommand command, BlockSweepCandidate candidate)
    {
        var row = candidate.OwnedBlock; command.Parameters.AddWithValue("$hash", row.Hash); command.Parameters.AddWithValue("$length", row.Length);
        command.Parameters.AddWithValue("$file", row.FileId); command.Parameters.AddWithValue("$captured", row.CapturedTicks);
        command.Parameters.AddWithValue("$used", row.LastUsedTicks); command.Parameters.AddWithValue("$first", candidate.FirstUnreferencedTicks);
    }
    private static void BindSweep(SqliteCommand command, Guid snapshot, byte[] root, long now, int days, long cap)
    {
        command.Parameters.AddWithValue("$snapshot", snapshot.ToString("N")); command.Parameters.AddWithValue("$root", root); command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$age", Math.Max(0, now - days * TimeSpan.TicksPerDay)); command.Parameters.AddWithValue("$grace", Math.Max(0, now - TimeSpan.TicksPerDay));
        command.Parameters.AddWithValue("$cap", cap);
    }
    private static byte[] FreezeSweep(byte[] root, long now, int days, int limit)
    {
        FolderMaps.RequireBytes(root, 24);
        if (now <= 0 || now > DateTime.MaxValue.Ticks || days is < 1 or > 36500 || limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(now));
        return root.ToArray();
    }
    private static BlockDeletionIntent FreezeDeletion(BlockDeletionIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent); var candidate = FreezeSweepCandidate(intent.Candidate);
        if (intent.Id == Guid.Empty || intent.SnapshotId == Guid.Empty || !Enum.IsDefined(intent.Reason)
            || intent.PreparedTicks < intent.Candidate.FirstUnreferencedTicks || intent.PreparedTicks > DateTime.MaxValue.Ticks) throw new ArgumentException("Invalid block deletion metadata.", nameof(intent));
        return intent with { Candidate = candidate };
    }
    private static BlockSweepCandidate FreezeSweepCandidate(BlockSweepCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate); ArgumentNullException.ThrowIfNull(candidate.OwnedBlock); var row = candidate.OwnedBlock;
        FolderMaps.RequireBytes(row.Hash, 32); FolderMaps.RequireBytes(row.RootId, 24); FolderMaps.RequireBytes(row.FileId, 16);
        if (row.Length is <= 0 or > 8388608 || row.CapturedTicks <= 0 || row.LastUsedTicks < row.CapturedTicks || row.LastUsedTicks > DateTime.MaxValue.Ticks
            || candidate.FirstUnreferencedTicks <= 0 || candidate.FirstUnreferencedTicks > DateTime.MaxValue.Ticks) throw new ArgumentException("Invalid owned block candidate.", nameof(candidate));
        return candidate with { OwnedBlock = row with { Hash = row.Hash.ToArray(), RootId = row.RootId.ToArray(), FileId = row.FileId.ToArray() } };
    }
    private static bool RequireDeletion(SqliteConnection connection, SqliteTransaction transaction, BlockDeletionIntent expected, bool allowAbsent = false)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT " + DeletionColumns + " FROM block_deletions WHERE id=$id;";
        command.Parameters.AddWithValue("$id", expected.Id.ToString("N")); using var reader = command.ExecuteReader();
        if (!reader.Read()) { if (allowAbsent) return false; throw new InvalidOperationException("Block deletion no longer exists."); }
        var actual = ReadDeletion(reader); var a = actual.Candidate.OwnedBlock; var e = expected.Candidate.OwnedBlock;
        if (actual.SnapshotId != expected.SnapshotId || actual.PreparedTicks != expected.PreparedTicks || actual.Reason != expected.Reason
            || actual.Candidate.FirstUnreferencedTicks != expected.Candidate.FirstUnreferencedTicks || a.Length != e.Length || a.CapturedTicks != e.CapturedTicks || a.LastUsedTicks != e.LastUsedTicks
            || !a.Hash.AsSpan().SequenceEqual(e.Hash) || !a.RootId.AsSpan().SequenceEqual(e.RootId) || !a.FileId.AsSpan().SequenceEqual(e.FileId))
            throw new InvalidOperationException("Block deletion changed."); return true;
    }
    private static void RequireSweepSnapshot(SqliteConnection connection, SqliteTransaction transaction, Guid snapshot, byte[] root)
    {
        RequireBlockReferenceRoot(connection, transaction, root); RequireNoReferenceApply(connection, transaction);
        var state = FindReferenceSnapshot(connection, transaction, snapshot);
        if (state is not { Active: true } || !state.RootId.AsSpan().SequenceEqual(root)) throw new InvalidOperationException("Refresh active references before block sweeping.");
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM archive_provenance p WHERE NOT EXISTS(SELECT 1 FROM archive_policy_releases x WHERE x.source_id=p.source_id AND x.root_id=$root) "
            + "AND NOT EXISTS(SELECT 1 FROM block_reference_sources c WHERE c.snapshot_id=$snapshot AND c.source_id=p.source_id)) "
            + "OR EXISTS(SELECT 1 FROM archive a WHERE NOT EXISTS(SELECT 1 FROM archive_provenance p WHERE p.archive_id=a.id)) "
            + "OR EXISTS(SELECT 1 FROM meta m WHERE m.key GLOB '_history-*' AND NOT EXISTS(SELECT 1 FROM history_archive_links h WHERE h.marker_key=m.key AND h.root_id=$root));";
        command.Parameters.AddWithValue("$root", root); command.Parameters.AddWithValue("$snapshot", snapshot.ToString("N"));
        if ((long)command.ExecuteScalar()! != 0) throw new InvalidOperationException("Archive/history coverage is incomplete.");
    }
}
