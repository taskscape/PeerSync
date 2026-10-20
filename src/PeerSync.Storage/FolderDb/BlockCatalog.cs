using Microsoft.Data.Sqlite;

namespace PeerSync.Storage.FolderDb;

public sealed record BlockObjectRow(byte[] Hash, int Length, byte[] RootId, byte[] FileId, long CapturedTicks, long LastUsedTicks);

public sealed partial class FolderIndex
{
    /// <summary>Caller has verified independent installed bytes under the bound
    /// root. Register ownership durably before publishing content readiness.</summary>
    public Task<int> RegisterBlockObjectsAsync(IReadOnlyList<BlockObjectRow> objects, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objects);
        if (objects.Count > 1000) throw new ArgumentOutOfRangeException(nameof(objects));
        var frozen = objects.Select(row => row with { Hash = row.Hash.ToArray(), RootId = row.RootId.ToArray(), FileId = row.FileId.ToArray() }).ToArray();
        foreach (var row in frozen)
        {
            FolderMaps.RequireBytes(row.Hash, 32); FolderMaps.RequireBytes(row.RootId, 24); FolderMaps.RequireBytes(row.FileId, 16);
            if (row.Length is <= 0 or > 8388608 || row.CapturedTicks <= 0 || row.CapturedTicks > DateTime.MaxValue.Ticks
                || row.LastUsedTicks < row.CapturedTicks || row.LastUsedTicks > DateTime.MaxValue.Ticks) throw new ArgumentException("Invalid block ownership metadata.", nameof(objects));
        }
        return database.WriteDurableAsync((connection, transaction) =>
        {
            using var root = connection.CreateCommand(); root.Transaction = transaction;
            root.CommandText = "SELECT value FROM meta WHERE key='_root-identity';";
            byte[] identity = root.ExecuteScalar() as byte[] ?? throw new InvalidOperationException("Bind the root before catalog registration.");
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO block_objects(hash,length,root_id,file_id,captured_ticks,last_used_ticks,legacy_retention) VALUES($hash,$length,$root,$id,$captured,$used,0) "
                + "ON CONFLICT(hash) DO UPDATE SET length=excluded.length,root_id=excluded.root_id,file_id=excluded.file_id,"
                + "legacy_retention=CASE WHEN block_objects.file_id=excluded.file_id AND block_objects.root_id=excluded.root_id AND block_objects.length=excluded.length THEN block_objects.legacy_retention ELSE 0 END,"
                + "captured_ticks=CASE WHEN block_objects.file_id=excluded.file_id AND block_objects.root_id=excluded.root_id THEN MIN(block_objects.captured_ticks,excluded.captured_ticks) ELSE excluded.captured_ticks END,"
                + "last_used_ticks=CASE WHEN block_objects.file_id=excluded.file_id AND block_objects.root_id=excluded.root_id THEN MAX(block_objects.last_used_ticks,excluded.last_used_ticks) ELSE excluded.last_used_ticks END;";
            command.Parameters.Add("$hash", SqliteType.Blob); command.Parameters.Add("$root", SqliteType.Blob); command.Parameters.Add("$id", SqliteType.Blob);
            command.Parameters.Add("$length", SqliteType.Integer); command.Parameters.Add("$captured", SqliteType.Integer); command.Parameters.Add("$used", SqliteType.Integer);
            foreach (var row in frozen)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!identity.AsSpan().SequenceEqual(row.RootId)) throw new InvalidOperationException("Block root does not match the bound index.");
                command.Parameters["$hash"].Value = row.Hash; command.Parameters["$root"].Value = row.RootId; command.Parameters["$id"].Value = row.FileId;
                command.Parameters["$length"].Value = row.Length; command.Parameters["$captured"].Value = row.CapturedTicks; command.Parameters["$used"].Value = row.LastUsedTicks;
                command.ExecuteNonQuery();
            }
            return frozen.Length;
        }, cancellationToken);
    }

    /// <summary>Catalog predicate only: callers must also verify guarded native
    /// bytes and serialize with the folder's ownership/retirement operations.</summary>
    public bool IsRegisteredBlockObject(byte[] hash, int length, byte[] rootId, byte[] fileId)
    {
        FolderMaps.RequireBytes(hash, 32); FolderMaps.RequireBytes(rootId, 24); FolderMaps.RequireBytes(fileId, 16);
        if (length is <= 0 or > 8388608) throw new ArgumentOutOfRangeException(nameof(length));
        using var connection = database.OpenReader(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM block_objects WHERE hash=$hash AND length=$length AND root_id=$root AND file_id=$file);";
        command.Parameters.AddWithValue("$hash", hash); command.Parameters.AddWithValue("$length", length);
        command.Parameters.AddWithValue("$root", rootId); command.Parameters.AddWithValue("$file", fileId);
        return (long)command.ExecuteScalar()! != 0;
    }

    public IEnumerable<BlockObjectRow> ReadBlockObjects()
    {
        using var connection = database.OpenReader(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT hash,length,root_id,file_id,captured_ticks,last_used_ticks FROM block_objects ORDER BY last_used_ticks,hash;";
        using var reader = command.ExecuteReader();
        while (reader.Read()) yield return new(reader.GetFieldValue<byte[]>(0), reader.GetInt32(1), reader.GetFieldValue<byte[]>(2),
            reader.GetFieldValue<byte[]>(3), reader.GetInt64(4), reader.GetInt64(5));
    }
}
