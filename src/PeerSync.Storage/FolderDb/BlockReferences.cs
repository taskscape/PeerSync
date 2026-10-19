using Microsoft.Data.Sqlite;

namespace PeerSync.Storage.FolderDb;

[Flags]
public enum BlockReferenceKind { Current = 1, Needed = 2, History = 4, Archive = 8 }
public sealed record BlockReferenceRow(byte[] Hash, int Length, BlockReferenceKind Kinds);
public sealed record ArchiveReferenceSource(string OriginalPath, string ArchivePath, long Size, byte[] FileHash);
public sealed record BlockReferenceSnapshot(Guid Id, byte[] RootId, long CreatedTicks, bool Active, long References);

public sealed partial class FolderIndex
{
    /// <summary>Caller serializes all reference-changing engine paths. A new
    /// build discards only abandoned staging; the active snapshot remains intact.</summary>
    public Task<BlockReferenceSnapshot> BeginBlockReferenceSnapshotAsync(byte[] rootId, long createdTicks, CancellationToken cancellationToken = default)
    {
        FolderMaps.RequireBytes(rootId, 24); byte[] root = rootId.ToArray(); Guid id = Guid.NewGuid();
        if (createdTicks <= 0 || createdTicks > DateTime.MaxValue.Ticks) throw new ArgumentOutOfRangeException(nameof(createdTicks));
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireBlockReferenceRoot(connection, transaction, root); RequireNoReferenceApply(connection, transaction);
            using var query = connection.CreateCommand(); query.Transaction = transaction;
            query.CommandText = "SELECT root_id FROM block_reference_snapshots WHERE active=1;";
            if (query.ExecuteScalar() is byte[] priorRoot && !priorRoot.AsSpan().SequenceEqual(root))
                throw new InvalidOperationException("Active reference snapshot belongs to another root.");
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "DELETE FROM block_reference_snapshots WHERE active=0; INSERT INTO block_reference_snapshots(id,root_id,created_ticks,active) VALUES($id,$root,$created,0);";
            command.Parameters.AddWithValue("$id", id.ToString("N")); command.Parameters.AddWithValue("$root", root); command.Parameters.AddWithValue("$created", createdTicks);
            command.ExecuteNonQuery(); return new BlockReferenceSnapshot(id, root.ToArray(), createdTicks, false, 0);
        }, cancellationToken);
    }

    /// <summary>Bounded staging batches use ordinary durability. Activation's
    /// FULL commit flushes every prior frame before making the build visible.</summary>
    public Task<int> AppendBlockReferencesAsync(Guid snapshot, IReadOnlyList<BlockReferenceRow> references, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(references);
        if (snapshot == Guid.Empty || references.Count > 1000) throw new ArgumentException("Invalid reference batch.", nameof(references));
        var frozen = references.Select(r => r with { Hash = r.Hash.ToArray() }).ToArray();
        foreach (var row in frozen)
        {
            FolderMaps.RequireBytes(row.Hash, 32);
            if (row.Length is <= 0 or > 8388608 || (int)row.Kinds is <= 0 or > 15) throw new ArgumentException("Invalid block reference.", nameof(references));
        }
        return database.WriteAsync((connection, transaction) =>
        {
            var state = FindReferenceSnapshot(connection, transaction, snapshot) ?? throw new InvalidOperationException("Reference build no longer exists.");
            if (state.Active) throw new InvalidOperationException("Active references are immutable.");
            RequireBlockReferenceRoot(connection, transaction, state.RootId);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO block_references(snapshot_id,hash,length,kinds) VALUES($id,$hash,$length,$kinds) "
                + "ON CONFLICT(snapshot_id,hash) DO UPDATE SET kinds=block_references.kinds|excluded.kinds WHERE block_references.length=excluded.length;";
            command.Parameters.AddWithValue("$id", snapshot.ToString("N")); command.Parameters.Add("$hash", SqliteType.Blob);
            command.Parameters.Add("$length", SqliteType.Integer); command.Parameters.Add("$kinds", SqliteType.Integer);
            foreach (var row in frozen)
            {
                cancellationToken.ThrowIfCancellationRequested(); command.Parameters["$hash"].Value = row.Hash;
                command.Parameters["$length"].Value = row.Length; command.Parameters["$kinds"].Value = (int)row.Kinds;
                if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("Referenced hash has conflicting lengths.");
            }
            return frozen.Length;
        }, cancellationToken);
    }

    /// <summary>Caller has completely validated sources under the folder gate.
    /// Incomplete or failed builds never replace the previous active snapshot.</summary>
    public Task<bool> ActivateBlockReferenceSnapshotAsync(Guid snapshot, CancellationToken cancellationToken = default)
    {
        if (snapshot == Guid.Empty) throw new ArgumentException("Invalid reference snapshot.", nameof(snapshot));
        return database.WriteDurableAsync((connection, transaction) =>
        {
            var state = FindReferenceSnapshot(connection, transaction, snapshot) ?? throw new InvalidOperationException("Reference build no longer exists.");
            RequireBlockReferenceRoot(connection, transaction, state.RootId);
            if (state.Active) return false;
            RequireNoReferenceApply(connection, transaction);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "DELETE FROM block_reference_snapshots WHERE id<>$id; UPDATE block_reference_snapshots SET active=1 WHERE id=$id;";
            command.Parameters.AddWithValue("$id", snapshot.ToString("N")); command.ExecuteNonQuery(); return true;
        }, cancellationToken);
    }

    /// <summary>Resolve authenticated history locations against stable archive
    /// sources, including detached rows, without retaining all names in RAM.</summary>
    public Task<int> AppendBlockArchiveCoverageAsync(Guid snapshot, IReadOnlyList<ArchiveReferenceSource> sources, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (snapshot == Guid.Empty || sources.Count > 1000) throw new ArgumentException("Invalid archive coverage batch.", nameof(sources));
        var frozen = sources.Select(r => r with { FileHash = r.FileHash.ToArray() }).ToArray();
        foreach (var row in frozen) { FolderMaps.RequireBytes(row.FileHash, 32); FolderMaps.Archive.Validate(new(0, row.OriginalPath, row.ArchivePath, 0, row.Size, row.FileHash)); }
        return database.WriteAsync((connection, transaction) =>
        {
            var state = FindReferenceSnapshot(connection, transaction, snapshot) ?? throw new InvalidOperationException("Reference build no longer exists.");
            if (state.Active) throw new InvalidOperationException("Active archive coverage is immutable.");
            RequireBlockReferenceRoot(connection, transaction, state.RootId);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT OR IGNORE INTO block_reference_sources(snapshot_id,source_id) SELECT $id,source_id FROM archive_provenance "
                + "WHERE original_path=$original AND archive_path=$path AND size=$size AND file_hash=$hash AND (root_id IS NULL OR root_id=$root);";
            command.Parameters.AddWithValue("$id", snapshot.ToString("N")); command.Parameters.Add("$original", SqliteType.Text); command.Parameters.Add("$path", SqliteType.Text);
            command.Parameters.AddWithValue("$root", state.RootId);
            command.Parameters.Add("$size", SqliteType.Integer); command.Parameters.Add("$hash", SqliteType.Blob); int count = 0;
            foreach (var row in frozen)
            {
                cancellationToken.ThrowIfCancellationRequested(); command.Parameters["$original"].Value = row.OriginalPath; command.Parameters["$path"].Value = row.ArchivePath;
                command.Parameters["$size"].Value = row.Size; command.Parameters["$hash"].Value = row.FileHash; count += command.ExecuteNonQuery();
            }
            return count;
        }, cancellationToken);
    }

    public long CountUnmanifestedReferenceArchives(Guid snapshot)
    {
        using var connection = database.OpenReader(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT COUNT(*) FROM archive_provenance p WHERE NOT EXISTS(SELECT 1 FROM block_reference_sources c WHERE c.snapshot_id=s.id AND c.source_id=p.source_id)) "
            + "+(SELECT COUNT(*) FROM archive a WHERE NOT EXISTS(SELECT 1 FROM archive_provenance p WHERE p.archive_id=a.id)) FROM block_reference_snapshots s WHERE id=$id;";
        command.Parameters.AddWithValue("$id", snapshot.ToString("N"));
        return command.ExecuteScalar() is long count ? count : throw new InvalidOperationException("Reference snapshot no longer exists.");
    }

    public bool IsBlockArchiveSourceCovered(Guid snapshotId, Guid sourceId)
    {
        using var connection = database.OpenReader(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM block_reference_sources WHERE snapshot_id=$snapshot AND source_id=$source);";
        command.Parameters.AddWithValue("$snapshot", snapshotId.ToString("N")); command.Parameters.AddWithValue("$source", sourceId.ToString("N"));
        return (long)command.ExecuteScalar()! != 0;
    }

    public BlockReferenceSnapshot? ReadActiveBlockReferenceSnapshot()
    {
        using var connection = database.OpenReader(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,root_id,created_ticks,active,(SELECT COUNT(*) FROM block_references WHERE snapshot_id=s.id) FROM block_reference_snapshots s WHERE active=1;";
        using var reader = command.ExecuteReader(); return reader.Read() ? ReadReferenceSnapshot(reader) : null;
    }

    /// <summary>Only active references are exposed. A cleanup caller must hold
    /// the shared folder gate and refresh/validate the snapshot before using it.</summary>
    public IEnumerable<BlockReferenceRow> ReadBlockReferences()
    {
        using var connection = database.OpenReader(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT r.hash,r.length,r.kinds FROM block_references r JOIN block_reference_snapshots s ON s.id=r.snapshot_id WHERE s.active=1 ORDER BY r.hash;";
        using var reader = command.ExecuteReader(); while (reader.Read()) yield return new(reader.GetFieldValue<byte[]>(0), reader.GetInt32(1), (BlockReferenceKind)reader.GetInt32(2));
    }
    private static BlockReferenceSnapshot? FindReferenceSnapshot(SqliteConnection connection, SqliteTransaction transaction, Guid id)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT id,root_id,created_ticks,active,0 FROM block_reference_snapshots WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString("N")); using var reader = command.ExecuteReader(); return reader.Read() ? ReadReferenceSnapshot(reader) : null;
    }
    private static BlockReferenceSnapshot ReadReferenceSnapshot(SqliteDataReader r) => new(Guid.ParseExact(r.GetString(0), "N"), r.GetFieldValue<byte[]>(1), r.GetInt64(2), r.GetBoolean(3), r.GetInt64(4));
    private static void RequireBlockReferenceRoot(SqliteConnection connection, SqliteTransaction transaction, byte[] root)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT value FROM meta WHERE key='_root-identity';";
        if (command.ExecuteScalar() is not byte[] bound || !bound.AsSpan().SequenceEqual(root)) throw new InvalidOperationException("Reference root does not match the bound index.");
    }
    private static void RequireNoReferenceApply(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT EXISTS(SELECT 1 FROM apply_intents);";
        if ((long)command.ExecuteScalar()! != 0) throw new InvalidOperationException("Recover pending application before refreshing references.");
    }
}
