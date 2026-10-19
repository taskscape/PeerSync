using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using PeerSync.Core.Paths;

namespace PeerSync.Storage.FolderDb;

public sealed record IndexBatch(IReadOnlyList<LocalFileRow> Records, long LastSequence);

public sealed partial class FolderIndex : IAsyncDisposable
{
    internal const string SequenceKey = "_local-sequence";
    private readonly Db database;

    private FolderIndex(Db database)
    {
        this.database = database;
        LocalFiles = new(database, FolderMaps.Local);
        RemoteFiles = new(database, FolderMaps.Remote);
        Needed = new(database, FolderMaps.Needed);
        Chunks = new(database, FolderMaps.Chunks);
        Devices = new(database, FolderMaps.Devices);
        Archive = new(database, FolderMaps.Archive);
        Meta = new(database, FolderMaps.Meta);
    }

    public RowRepository<LocalFileRow> LocalFiles { get; }
    public RowRepository<RemoteFileRow> RemoteFiles { get; }
    public RowRepository<NeededRow> Needed { get; }
    public RowRepository<ChunkRow> Chunks { get; }
    public RowRepository<DeviceRow> Devices { get; }
    public RowRepository<ArchiveRow> Archive { get; }
    public RowRepository<MetaRow> Meta { get; }

    public static async Task<FolderIndex> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        var migrations = new List<DbMigration>();
        foreach (string file in new[] { "0001_initial.sql", "0002_chunk_path.sql", "0003_remote_path.sql", "0004_apply_intents.sql", "0005_content_ready.sql", "0006_archive_version.sql", "0007_block_objects.sql", "0008_archive_retirements.sql", "0009_native_change_time.sql", "0010_block_references.sql", "0011_archive_provenance.sql", "0012_archive_contents.sql", "0013_history_retention.sql", "0014_archive_path.sql", "0015_archive_policy.sql", "0016_block_sweeps.sql", "0017_capture_retirements.sql", "0018_metadata_retirement.sql", "0019_pull_work.sql", "0020_staging_catalog.sql" })
        {
            using var resource = typeof(FolderIndex).Assembly.GetManifestResourceStream("PeerSync.Storage.FolderDb.Migrations." + file)
                ?? throw new InvalidOperationException("Missing folder schema resource.");
            using var reader = new StreamReader(resource);
            string sql = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            migrations.Add(new(migrations.Count + 1, sql));
        }
        var database = await Db.OpenAsync(path, migrations, cancellationToken).ConfigureAwait(false);
        try { var index = new FolderIndex(database); await index.BackfillArchivePathKeysAsync(cancellationToken).ConfigureAwait(false); return index; }
        catch { await database.DisposeAsync().ConfigureAwait(false); throw; }
    }

    /// <summary>Read records and the cursor in one snapshot, including safe advancement over unpublished sequence gaps.</summary>
    public long ReadIndexHighWater()
    {
        using var connection = database.OpenReader();
        using var transaction = connection.BeginTransaction(deferred: true);
        long result = ReadSequence(connection, transaction); transaction.Commit(); return result;
    }

    public IndexBatch ReadIndexBatch(long afterSequence, int limit = 1000)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        using var connection = database.OpenReader();
        using var transaction = connection.BeginTransaction(deferred: true);
        long highWater = ReadSequence(connection, transaction);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT " + FolderMaps.LocalColumns + " FROM local_files WHERE local_seq>$after AND local_seq<=$high AND flags=0 AND record_body IS NOT NULL AND (type<>0 OR deleted=1 OR content_ready=1) ORDER BY local_seq LIMIT $limit;";
        command.Parameters.AddWithValue("$after", afterSequence);
        command.Parameters.AddWithValue("$high", highWater);
        command.Parameters.AddWithValue("$limit", limit);
        var rows = new List<LocalFileRow>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) rows.Add(FolderMaps.ReadLocal(reader));
        long cursor = rows.Count == limit ? rows[^1].LocalSequence : Math.Max(afterSequence, highWater);
        transaction.Commit();
        return new(rows.AsReadOnly(), cursor);
    }

    public IEnumerable<ChunkRow> FindChunks(byte[] hash)
    {
        FolderMaps.RequireBytes(hash, 32);
        return Chunks.ReadWhere("hash=$k0 ORDER BY path_key,file_offset", hash);
    }

    public IEnumerable<RemoteFileRow> RemotesForPath(string recordPath) =>
        RemoteFiles.ReadWhere("path_key=$k0", PathRules.Key(recordPath));

    public IEnumerable<NeededRow> NeededByPriority() => Needed.ReadWhere("1=1 ORDER BY priority DESC,path_key");
    public IEnumerable<ArchiveRow> ArchivesByAge() => Archive.ReadWhere("1=1 ORDER BY archived_ticks,id");
    public IEnumerable<ArchiveRow> ArchivesForVersion(string originalPath, byte[] hash, long size)
    {
        _ = PathRules.Key(originalPath); FolderMaps.RequireBytes(hash, 32); ArgumentOutOfRangeException.ThrowIfNegative(size);
        return Archive.ReadWhere("original_path=$k0 AND file_hash=$k1 AND size=$k2 ORDER BY id", originalPath, hash, size);
    }
    public ValueTask DisposeAsync() => database.DisposeAsync();

    internal static long ReadSequence(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT value FROM meta WHERE key=$key;";
        command.Parameters.AddWithValue("$key", SequenceKey);
        object? value = command.ExecuteScalar();
        if (value is null or DBNull) return 0;
        if (value is not byte[] { Length: 8 } bytes) throw new InvalidDataException("Invalid local sequence metadata.");
        long sequence = BinaryPrimitives.ReadInt64LittleEndian(bytes);
        if (sequence < 0) throw new InvalidDataException("Invalid local sequence metadata.");
        return sequence;
    }
}
