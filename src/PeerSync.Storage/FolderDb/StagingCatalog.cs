using Microsoft.Data.Sqlite;
using PeerSync.Core.Paths;

namespace PeerSync.Storage.FolderDb;

public enum StagingObjectState { Parked, Retiring, Protected }

/// <summary>Trusted local ownership witness, never a protocol message or log payload.
/// Registration freezes caller buffers; readers return independent values.</summary>
public sealed record StagedObject(Guid Id, byte[] RootId, byte[] FileId, string TargetPath, string TemporaryPath,
    bool IsDirectory, long ParkedTicks, long Length, byte[]? ContentHash, long ChangeFileTime,
    StagingObjectState State = StagingObjectState.Parked)
{
    public override string ToString() => "Owned staging object (redacted)";
}

public sealed partial class FolderIndex
{
    private const string StagingColumns = "id,root_id,file_id,target_path,temp_path,kind,parked_ticks,file_length,content_hash,change_filetime,state";

    /// <summary>Caller holds the verified native object under its live apply
    /// intent. FULL catalog commit precedes abandonment/defer of that intent.
    /// This records ownership and grace only; it never authorizes deletion.</summary>
    public Task<StagedObject> ParkStagingAsync(ApplyIntent expectedIntent, StagedObject staging, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedIntent); var row = FreezeStaging(staging);
        if (row.State == StagingObjectState.Retiring || PathRules.Key(row.TargetPath) != expectedIntent.PathKey)
            throw new ArgumentException("Staging must match its owning apply intent.", nameof(staging));
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireBlockReferenceRoot(connection, transaction, row.RootId);
            var actual = ReadIntent(connection, transaction, expectedIntent.Id) ?? throw new InvalidOperationException("Staging has no active apply intent.");
            RequireSameIntent(actual, expectedIntent);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT " + StagingColumns + " FROM staged_objects WHERE root_id=$root AND file_id=$file AND temp_key=$tempkey;";
            command.Parameters.AddWithValue("$root", row.RootId); command.Parameters.AddWithValue("$file", row.FileId); command.Parameters.AddWithValue("$temp", row.TemporaryPath);
            command.Parameters.AddWithValue("$tempkey", row.TemporaryPath.ToUpperInvariant());
            StagedObject? old; using (var reader = command.ExecuteReader()) old = reader.Read() ? ReadStagedObject(reader) : null;
            if (old?.State == StagingObjectState.Retiring) throw new InvalidOperationException("Recover staging retirement before reusing its object.");
            if (old is not null)
            {
                if (PathRules.Key(old.TargetPath) != PathRules.Key(row.TargetPath) || old.IsDirectory != row.IsDirectory)
                    throw new InvalidOperationException("Staging ownership cannot change target or kind.");
                bool unchanged = old.Length == row.Length
                    && old.State == row.State && old.ChangeFileTime == row.ChangeFileTime && StagingHashEqual(old.ContentHash, row.ContentHash);
                row = row with { Id = old.Id, TemporaryPath = old.TemporaryPath, ParkedTicks = unchanged ? old.ParkedTicks : row.ParkedTicks };
                command.Parameters["$temp"].Value = row.TemporaryPath;
            }
            command.CommandText = "INSERT INTO staged_objects(id,root_id,file_id,path_key,target_path,temp_path,temp_key,kind,parked_ticks,file_length,content_hash,change_filetime,state) "
                + "VALUES($id,$root,$file,$path,$target,$temp,$tempkey,$kind,$parked,$length,$hash,$change,$state) ON CONFLICT(root_id,file_id,temp_key) DO UPDATE SET "
                + "path_key=excluded.path_key,target_path=excluded.target_path,kind=excluded.kind,parked_ticks=excluded.parked_ticks,"
                + "file_length=excluded.file_length,content_hash=excluded.content_hash,change_filetime=excluded.change_filetime,state=excluded.state;";
            command.Parameters.AddWithValue("$id", row.Id.ToString("N")); command.Parameters.AddWithValue("$path", PathRules.Key(row.TargetPath));
            command.Parameters.AddWithValue("$target", row.TargetPath); command.Parameters.AddWithValue("$kind", row.IsDirectory ? 1 : 0);
            command.Parameters.AddWithValue("$parked", row.ParkedTicks); command.Parameters.AddWithValue("$length", row.Length);
            command.Parameters.AddWithValue("$hash", (object?)row.ContentHash ?? DBNull.Value); command.Parameters.AddWithValue("$change", row.ChangeFileTime);
            command.Parameters.AddWithValue("$state", (int)row.State);
            cancellationToken.ThrowIfCancellationRequested(); command.ExecuteNonQuery(); return FreezeStaging(row);
        }, cancellationToken);
    }

    public IReadOnlyList<StagedObject> ReadReusableStaging(byte[] rootId, string targetPath, bool directory, int limit = 64)
    {
        FolderMaps.RequireBytes(rootId, 24); string key = PathRules.Key(targetPath);
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        using var connection = database.OpenReader(); using var transaction = connection.BeginTransaction(deferred: true);
        RequireBlockReferenceRoot(connection, transaction, rootId); using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT " + StagingColumns + " FROM staged_objects WHERE root_id=$root AND path_key=$path AND kind=$kind AND state=0 ORDER BY serial DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$root", rootId); command.Parameters.AddWithValue("$path", key); command.Parameters.AddWithValue("$kind", directory ? 1 : 0); command.Parameters.AddWithValue("$limit", limit);
        var rows = new List<StagedObject>(); using var reader = command.ExecuteReader(); while (reader.Read()) rows.Add(ReadStagedObject(reader)); return rows.AsReadOnly();
    }

    public bool IsCatalogedStaging(byte[] rootId, byte[] fileId, string temporaryPath)
    {
        FolderMaps.RequireBytes(rootId, 24); FolderMaps.RequireBytes(fileId, 16); RequireStagingTemporary(temporaryPath);
        using var connection = database.OpenReader(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM staged_objects WHERE root_id=$root AND file_id=$file AND temp_key=$tempkey);";
        command.Parameters.AddWithValue("$root", rootId); command.Parameters.AddWithValue("$file", fileId); command.Parameters.AddWithValue("$tempkey", temporaryPath.ToUpperInvariant());
        return (long)command.ExecuteScalar()! != 0;
    }

    private static StagedObject FreezeStaging(StagedObject row)
    {
        ArgumentNullException.ThrowIfNull(row); FolderMaps.RequireBytes(row.RootId, 24); FolderMaps.RequireBytes(row.FileId, 16);
        _ = PathRules.Key(row.TargetPath); RequireStagingTemporary(row.TemporaryPath);
        if (StagingParentKey(row.TargetPath) != StagingParentKey(row.TemporaryPath) || row.Id == Guid.Empty || row.ParkedTicks is <= 0 || row.ParkedTicks > DateTime.MaxValue.Ticks
            || row.Length < 0 || row.ChangeFileTime < 0 || !Enum.IsDefined(row.State)) throw new ArgumentException("Invalid staging witness.", nameof(row));
        if (row.IsDirectory)
        { if (row.ContentHash is not null || row.Length != 0) throw new ArgumentException("Directories have no content hash.", nameof(row)); }
        else FolderMaps.RequireBytes(row.ContentHash!, 32);
        return row with { RootId = row.RootId.ToArray(), FileId = row.FileId.ToArray(), ContentHash = row.ContentHash?.ToArray() };
    }
    private static void RequireStagingTemporary(string path)
    {
        ArgumentNullException.ThrowIfNull(path); int slash = path.LastIndexOf('/'); string name = path[(slash + 1)..];
        if (path.Length > 4352 || name.Length != 37 || !name.EndsWith(".!psp", StringComparison.Ordinal) || !Guid.TryParseExact(name[..32], "N", out _)
            || slash >= 0 && !PathRules.IsValid(path[..slash])) throw new ArgumentException("Invalid owned staging path.", nameof(path));
    }
    private static string StagingParentKey(string path)
    { int slash = path.LastIndexOf('/'); return slash < 0 ? "" : PathRules.Key(path[..slash]); }
    private static bool StagingHashEqual(byte[]? left, byte[]? right) => left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);
    private static StagedObject ReadStagedObject(SqliteDataReader reader) => FreezeStaging(new(Guid.ParseExact(reader.GetString(0), "N"), reader.GetFieldValue<byte[]>(1),
        reader.GetFieldValue<byte[]>(2), reader.GetString(3), reader.GetString(4), reader.GetBoolean(5), reader.GetInt64(6), reader.GetInt64(7),
        reader.IsDBNull(8) ? null : reader.GetFieldValue<byte[]>(8), reader.GetInt64(9), (StagingObjectState)reader.GetInt32(10)));
}
