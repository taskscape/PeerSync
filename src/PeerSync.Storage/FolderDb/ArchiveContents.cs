using Microsoft.Data.Sqlite;

namespace PeerSync.Storage.FolderDb;

public sealed record ArchiveContentSnapshot(Guid Id, Guid SourceId, byte[] RootId, byte[] FileId, long Size,
    byte[] FileHash, long VerifiedTicks, bool Active, long NextOffset, int ChunkCount)
{
    public const int MaximumChunks = 100000;
    public override string ToString() => "Archive content protection (metadata redacted)";
}

public sealed partial class FolderIndex
{
    /// <summary>Caller has verified the exact source bytes under a native read
    /// guard and durably registered their independent blocks. This does not authorize deletion.</summary>
    public Task<ArchiveContentSnapshot> BeginArchiveContentAsync(ArchiveProvenance source, byte[] rootId,
        byte[] fileId, long verifiedTicks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source); FolderMaps.RequireBytes(rootId, 24); FolderMaps.RequireBytes(fileId, 16);
        FolderMaps.Archive.Validate(source.Archive);
        if (source.SourceId == Guid.Empty || source.Archive.Id <= 0 || source.Archive.FileHash is not { Length: 32 }
            || verifiedTicks <= 0 || verifiedTicks > DateTime.MaxValue.Ticks) throw new ArgumentException("Invalid archive content source.", nameof(source));
        if (source.RootId is not null) FolderMaps.RequireBytes(source.RootId, 24);
        byte[] root = rootId.ToArray(), file = fileId.ToArray(); Guid id = Guid.NewGuid();
        var frozen = source with { Archive = source.Archive with { FileHash = source.Archive.FileHash.ToArray() }, RootId = source.RootId?.ToArray() };
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireBlockReferenceRoot(connection, transaction, root); RequireNoReferenceApply(connection, transaction);
            if (frozen.RootId is not null && !frozen.RootId.AsSpan().SequenceEqual(root)) throw new InvalidOperationException("Archive source belongs to another root.");
            RequireArchiveContentSource(connection, transaction, frozen);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "DELETE FROM archive_contents WHERE source_id=$source AND active=0; "
                + "INSERT INTO archive_contents(id,source_id,root_id,file_id,size,file_hash,verified_ticks,active) VALUES($id,$source,$root,$file,$size,$hash,$ticks,0);";
            command.Parameters.AddWithValue("$source", frozen.SourceId.ToString("N")); command.Parameters.AddWithValue("$id", id.ToString("N"));
            command.Parameters.AddWithValue("$root", root); command.Parameters.AddWithValue("$file", file); command.Parameters.AddWithValue("$size", frozen.Archive.Size);
            command.Parameters.AddWithValue("$hash", frozen.Archive.FileHash!); command.Parameters.AddWithValue("$ticks", verifiedTicks); command.ExecuteNonQuery();
            return new ArchiveContentSnapshot(id, frozen.SourceId, root.ToArray(), file.ToArray(), frozen.Archive.Size, frozen.Archive.FileHash!.ToArray(), verifiedTicks, false, 0, 0);
        }, cancellationToken);
    }

    /// <summary>Bounded ordered chunks remain invisible until FULL activation.</summary>
    public Task<int> AppendArchiveContentChunksAsync(Guid contentId, IReadOnlyList<LocalChunk> chunks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        if (contentId == Guid.Empty || chunks.Count > 1000) throw new ArgumentException("Invalid archive chunk batch.", nameof(chunks));
        var frozen = chunks.Select(c => c with { Hash = c.Hash.ToArray() }).ToArray();
        foreach (var chunk in frozen)
        {
            FolderMaps.RequireBytes(chunk.Hash, 32);
            if (chunk.Offset < 0 || chunk.Length is <= 0 or > 8388608) throw new ArgumentException("Invalid archive chunk.", nameof(chunks));
        }
        return database.WriteAsync((connection, transaction) =>
        {
            var state = FindArchiveContent(connection, transaction, contentId) ?? throw new InvalidOperationException("Archive content build disappeared.");
            if (state.Active) throw new InvalidOperationException("Active archive content is immutable.");
            RequireBlockReferenceRoot(connection, transaction, state.RootId);
            long next = state.NextOffset; int count = checked(state.ChunkCount + frozen.Length);
            if (count > ArchiveContentSnapshot.MaximumChunks) throw new InvalidDataException("Archive manifest exceeds the chunk limit.");
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO archive_content_chunks(content_id,file_offset,hash,length) VALUES($id,$offset,$hash,$length);";
            command.Parameters.AddWithValue("$id", contentId.ToString("N")); command.Parameters.Add("$offset", SqliteType.Integer);
            command.Parameters.Add("$hash", SqliteType.Blob); command.Parameters.Add("$length", SqliteType.Integer);
            foreach (var chunk in frozen)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (chunk.Offset != next || chunk.Length > state.Size - next) throw new InvalidDataException("Archive chunks are not a complete ordered prefix.");
                next += chunk.Length; command.Parameters["$offset"].Value = chunk.Offset; command.Parameters["$hash"].Value = chunk.Hash;
                command.Parameters["$length"].Value = chunk.Length; command.ExecuteNonQuery();
            }
            command.Parameters.Clear(); command.CommandText = "UPDATE archive_contents SET next_offset=$next,chunk_count=$count WHERE id=$id;";
            command.Parameters.AddWithValue("$next", next); command.Parameters.AddWithValue("$count", count); command.Parameters.AddWithValue("$id", contentId.ToString("N"));
            command.ExecuteNonQuery(); return frozen.Length;
        }, cancellationToken);
    }

    public Task<bool> ActivateArchiveContentAsync(Guid contentId, CancellationToken cancellationToken = default)
    {
        if (contentId == Guid.Empty) throw new ArgumentException("Invalid archive content ID.", nameof(contentId));
        return database.WriteDurableAsync((connection, transaction) =>
        {
            var state = FindArchiveContent(connection, transaction, contentId) ?? throw new InvalidOperationException("Archive content build disappeared.");
            RequireBlockReferenceRoot(connection, transaction, state.RootId); if (state.Active) return false;
            RequireNoReferenceApply(connection, transaction);
            if (state.NextOffset != state.Size) throw new InvalidOperationException("Archive content is incomplete.");
            using var source = connection.CreateCommand(); source.Transaction = transaction;
            source.CommandText = "SELECT COUNT(*) FROM archive_provenance WHERE source_id=$source AND size=$size AND file_hash=$hash AND (root_id IS NULL OR root_id=$root);";
            source.Parameters.AddWithValue("$source", state.SourceId.ToString("N")); source.Parameters.AddWithValue("$size", state.Size);
            source.Parameters.AddWithValue("$hash", state.FileHash); source.Parameters.AddWithValue("$root", state.RootId);
            if ((long)source.ExecuteScalar()! != 1) throw new InvalidOperationException("Archive content source changed.");
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "DELETE FROM archive_contents WHERE source_id=$source AND id<>$id; UPDATE archive_contents SET active=1 WHERE id=$id;";
            command.Parameters.AddWithValue("$source", state.SourceId.ToString("N")); command.Parameters.AddWithValue("$id", contentId.ToString("N")); command.ExecuteNonQuery(); return true;
        }, cancellationToken);
    }

    public ArchiveContentSnapshot? FindActiveArchiveContent(Guid sourceId)
    {
        using var connection = database.OpenReader(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + ArchiveContentColumns + " FROM archive_contents WHERE source_id=$source AND active=1;";
        command.Parameters.AddWithValue("$source", sourceId.ToString("N")); using var reader = command.ExecuteReader(); return reader.Read() ? ReadArchiveContent(reader) : null;
    }

    public IEnumerable<LocalChunk> ReadArchiveContentChunks(Guid contentId)
        => ReadArchiveContentChunksCore(contentId, active: true);

    public IEnumerable<LocalChunk> ReadPendingArchiveContentChunks(Guid contentId)
        => ReadArchiveContentChunksCore(contentId, active: false);

    public IEnumerable<ArchiveContentSnapshot> ReadPendingArchiveContents()
    {
        using var connection = database.OpenReader(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + ArchiveContentColumns + " FROM archive_contents WHERE active=0 ORDER BY source_id;";
        using var reader = command.ExecuteReader(); while (reader.Read()) yield return ReadArchiveContent(reader);
    }

    private IEnumerable<LocalChunk> ReadArchiveContentChunksCore(Guid contentId, bool active)
    {
        using var connection = database.OpenReader(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT c.file_offset,c.length,c.hash FROM archive_content_chunks c JOIN archive_contents a ON a.id=c.content_id WHERE a.id=$id AND a.active=$active ORDER BY c.file_offset;";
        command.Parameters.AddWithValue("$id", contentId.ToString("N")); command.Parameters.AddWithValue("$active", active ? 1 : 0); using var reader = command.ExecuteReader();
        while (reader.Read()) yield return new(reader.GetInt64(0), reader.GetInt32(1), reader.GetFieldValue<byte[]>(2));
    }

    /// <summary>Called only after all active content chunks have been added to the same staged reference snapshot.</summary>
    public Task<bool> AppendBlockArchiveContentCoverageAsync(Guid snapshotId, Guid contentId, CancellationToken cancellationToken = default) =>
        database.WriteAsync((connection, transaction) =>
        {
            var snapshot = FindReferenceSnapshot(connection, transaction, snapshotId) ?? throw new InvalidOperationException("Reference build disappeared.");
            if (snapshot.Active) throw new InvalidOperationException("Active reference coverage is immutable.");
            RequireBlockReferenceRoot(connection, transaction, snapshot.RootId);
            var content = FindArchiveContent(connection, transaction, contentId) ?? throw new InvalidOperationException("Archive content disappeared.");
            if (!content.Active || !content.RootId.AsSpan().SequenceEqual(snapshot.RootId)) throw new InvalidOperationException("Archive content does not belong to this snapshot.");
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT OR IGNORE INTO block_reference_sources(snapshot_id,source_id) SELECT $id,p.source_id FROM archive_provenance p "
                + "WHERE p.source_id=$source AND p.size=$size AND p.file_hash=$hash AND (p.root_id IS NULL OR p.root_id=$root);";
            command.Parameters.AddWithValue("$id", snapshotId.ToString("N")); command.Parameters.AddWithValue("$source", content.SourceId.ToString("N"));
            command.Parameters.AddWithValue("$size", content.Size); command.Parameters.AddWithValue("$hash", content.FileHash); command.Parameters.AddWithValue("$root", content.RootId);
            return command.ExecuteNonQuery() == 1;
        }, cancellationToken);

    private const string ArchiveContentColumns = "id,source_id,root_id,file_id,size,file_hash,verified_ticks,active,next_offset,chunk_count";
    private static ArchiveContentSnapshot? FindArchiveContent(SqliteConnection connection, SqliteTransaction transaction, Guid contentId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT " + ArchiveContentColumns + " FROM archive_contents WHERE id=$id;";
        command.Parameters.AddWithValue("$id", contentId.ToString("N")); using var reader = command.ExecuteReader(); return reader.Read() ? ReadArchiveContent(reader) : null;
    }
    private static ArchiveContentSnapshot ReadArchiveContent(SqliteDataReader r) => new(Guid.ParseExact(r.GetString(0), "N"), Guid.ParseExact(r.GetString(1), "N"),
        r.GetFieldValue<byte[]>(2), r.GetFieldValue<byte[]>(3), r.GetInt64(4), r.GetFieldValue<byte[]>(5), r.GetInt64(6), r.GetBoolean(7), r.GetInt64(8), r.GetInt32(9));

    private static void RequireArchiveContentSource(SqliteConnection connection, SqliteTransaction transaction, ArchiveProvenance expected)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM archive_provenance WHERE source_id=$source AND original_archive_id=$archive AND original_path=$original "
            + "AND archive_path=$path AND archived_ticks=$ticks AND size=$size AND file_hash=$hash AND root_id IS $origin;";
        command.Parameters.AddWithValue("$source", expected.SourceId.ToString("N")); command.Parameters.AddWithValue("$archive", expected.Archive.Id);
        command.Parameters.AddWithValue("$original", expected.Archive.OriginalPath); command.Parameters.AddWithValue("$path", expected.Archive.ArchivePath);
        command.Parameters.AddWithValue("$ticks", expected.Archive.ArchivedTicks); command.Parameters.AddWithValue("$size", expected.Archive.Size);
        command.Parameters.AddWithValue("$hash", expected.Archive.FileHash!); command.Parameters.AddWithValue("$origin", (object?)expected.RootId ?? DBNull.Value);
        if ((long)command.ExecuteScalar()! != 1) throw new InvalidOperationException("Archive protection source changed.");
    }
}
