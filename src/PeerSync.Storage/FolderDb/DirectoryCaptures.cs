using Microsoft.Data.Sqlite;
using PeerSync.Core.Paths;

namespace PeerSync.Storage.FolderDb;

public sealed record DirectoryCaptureState(string ParentPath, byte[] RootId, byte[] NativeId, byte[] InputHash, long MemberCount,
    long Accepted, string? LastKey, byte[]? BuilderState, byte[]? Hash, int? Length)
{ public override string ToString() => "Directory capture state (redacted)"; }

public sealed partial class FolderIndex
{
    public Task<DirectoryCaptureState> BeginDirectoryCaptureAsync(string parentPath, byte[] rootId, byte[] nativeId, byte[] inputHash,
        long memberCount, byte[] initialBuilderState, CancellationToken cancellationToken = default)
    {
        string key = PathRules.Key(parentPath); FolderMaps.RequireBytes(rootId, 24); FolderMaps.RequireBytes(nativeId, 16); FolderMaps.RequireBytes(inputHash, 32);
        ArgumentOutOfRangeException.ThrowIfNegative(memberCount);
        if (initialBuilderState is not { Length: 16 }) throw new ArgumentException("Invalid initial directory builder state.", nameof(initialBuilderState));
        byte[] root = rootId.ToArray(), native = nativeId.ToArray(), input = inputHash.ToArray(), initial = initialBuilderState.ToArray();
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireBlockReferenceRoot(connection, transaction, root);
            var before = FindDirectoryCapture(connection, transaction, key);
            if (before is not null && !before.RootId.AsSpan().SequenceEqual(root)) throw new InvalidDataException("Directory capture belongs to another root.");
            bool same = before is not null && before.ParentPath == parentPath && before.MemberCount == memberCount
                && before.NativeId.AsSpan().SequenceEqual(native) && before.InputHash.AsSpan().SequenceEqual(input);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = same ? "UPDATE directory_captures SET last_used_ticks=$now WHERE parent_key=$key;" :
                "INSERT INTO directory_captures(parent_key,parent_path,root_id,native_id,input_hash,member_count,builder_state,last_used_ticks) "
                + "VALUES($key,$path,$root,$native,$input,$count,$state,$now) ON CONFLICT(parent_key) DO UPDATE SET "
                + "parent_path=excluded.parent_path,root_id=excluded.root_id,native_id=excluded.native_id,input_hash=excluded.input_hash,member_count=excluded.member_count,"
                + "accepted=0,last_key=NULL,builder_state=excluded.builder_state,node_hash=NULL,node_length=NULL,last_used_ticks=excluded.last_used_ticks;";
            command.Parameters.AddWithValue("$key", key); command.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
            if (!same)
            {
                command.Parameters.AddWithValue("$path", parentPath); command.Parameters.AddWithValue("$root", root); command.Parameters.AddWithValue("$native", native);
                command.Parameters.AddWithValue("$input", input); command.Parameters.AddWithValue("$count", memberCount); command.Parameters.AddWithValue("$state", initial);
            }
            command.ExecuteNonQuery(); return FindDirectoryCapture(connection, transaction, key)!;
        }, cancellationToken);
    }
    public DirectoryCaptureState? ReadDirectoryCapture(string parentPath)
    { using var connection = database.OpenReader(); return FindDirectoryCapture(connection, null, PathRules.Key(parentPath)); }
    public bool HasDirectoryCaptures()
    {
        using var connection = database.OpenReader(); using var command = connection.CreateCommand(); command.CommandText = "SELECT EXISTS(SELECT 1 FROM directory_captures);";
        return (long)command.ExecuteScalar()! != 0;
    }
    public Task<DirectoryCaptureState> AdvanceDirectoryCaptureAsync(DirectoryCaptureState expected, long accepted, string? lastKey,
        byte[]? builderState, byte[]? hash, int? length, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected); var prior = FreezeCapture(expected); _ = PathRules.Key(prior.ParentPath);
        if (accepted < prior.Accepted || accepted > prior.MemberCount || accepted - prior.Accepted > 1000) throw new ArgumentOutOfRangeException(nameof(accepted));
        string? cursor = lastKey is null ? null : PathRules.Key(lastKey);
        if ((accepted == 0) != (cursor is null) || accepted == prior.Accepted && cursor != prior.LastKey || cursor is not null && (CaptureParent(cursor) != PathRules.Key(prior.ParentPath)
            || prior.LastKey is not null && accepted > prior.Accepted && StringComparer.Ordinal.Compare(cursor, prior.LastKey) <= 0))
            throw new ArgumentException("Invalid directory capture cursor.", nameof(lastKey));
        if (hash is null ? length is not null || builderState is not { Length: >= 16 and <= 50328 }
            : hash.Length != 32 || length is null or < 12 or > 4620 || (length - 12) % 36 != 0 || builderState is not null || accepted != prior.MemberCount)
            throw new ArgumentException("Invalid directory capture progress.", nameof(builderState));
        byte[]? state = builderState?.ToArray(), node = hash?.ToArray();
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireCaptureExpected(connection, transaction, prior);
            if (prior.Hash is not null) throw new InvalidOperationException("Completed directory capture is immutable.");
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE directory_captures SET accepted=$accepted,last_key=$last,builder_state=$state,node_hash=$hash,node_length=$length,last_used_ticks=$now WHERE parent_key=$key;";
            command.Parameters.AddWithValue("$key", PathRules.Key(prior.ParentPath)); command.Parameters.AddWithValue("$accepted", accepted);
            command.Parameters.AddWithValue("$last", (object?)cursor ?? DBNull.Value); command.Parameters.AddWithValue("$state", (object?)state ?? DBNull.Value);
            command.Parameters.AddWithValue("$hash", (object?)node ?? DBNull.Value); command.Parameters.AddWithValue("$length", (object?)length ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks); command.ExecuteNonQuery();
            return FindDirectoryCapture(connection, transaction, PathRules.Key(prior.ParentPath))!;
        }, cancellationToken);
    }
    public Task<int> PruneDirectoryCapturesAsync(byte[] rootId, long nowTicks, int limit = 1000, CancellationToken cancellationToken = default)
    {
        FolderMaps.RequireBytes(rootId, 24); byte[] root = rootId.ToArray();
        if (nowTicks is < 1 or > 3155378975999999999 || limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireBlockReferenceRoot(connection, transaction, root);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "DELETE FROM directory_captures WHERE parent_key IN (SELECT parent_key FROM directory_captures WHERE root_id=$root AND last_used_ticks<=$cutoff ORDER BY last_used_ticks,parent_key LIMIT $limit);";
            command.Parameters.AddWithValue("$root", root); command.Parameters.AddWithValue("$cutoff", Math.Max(0, nowTicks - TimeSpan.TicksPerDay));
            command.Parameters.AddWithValue("$limit", limit); return command.ExecuteNonQuery();
        }, cancellationToken);
    }
    /// <summary>Engine has verified the signed root and pinned native directory.
    /// Publish the local row and release its draft in one FULL transaction.
    /// Caller has already retained old versions and published child content.</summary>
    public Task<LocalFileRow> PublishDirectoryCaptureAsync(DirectoryCaptureState expected, LocalFileRow publication, long expectedLocalSequence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected); ArgumentNullException.ThrowIfNull(publication); ArgumentOutOfRangeException.ThrowIfNegative(expectedLocalSequence);
        var prior = FreezeCapture(expected);
        if (prior.Hash is not { Length: 32 } || prior.Length is null || prior.Accepted != prior.MemberCount || prior.BuilderState is not null)
            throw new ArgumentException("Directory capture is incomplete.", nameof(expected));
        var row = publication with { FileId = publication.FileId?.ToArray(), Vector = new Dictionary<ulong, ulong>(publication.Vector), IndexedChunks = [] };
        FolderMaps.Local.Validate(row);
        if (row.Path != prior.ParentPath || row.Type != 1 || row.Deleted || row.Size != 0 || row.Flags != LocalFileState.None
            || row.FileHash is not null || row.FileId is null || !row.FileId.AsSpan().SequenceEqual(prior.NativeId) || row.Record is null)
            throw new ArgumentException("Directory publication does not match its capture.", nameof(publication));
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireCaptureExpected(connection, transaction, prior); string key = PathRules.Key(prior.ParentPath);
            using var query = connection.CreateCommand(); query.Transaction = transaction;
            query.CommandText = "SELECT local_seq FROM local_files WHERE path_key=$key;"; query.Parameters.AddWithValue("$key", key);
            if ((query.ExecuteScalar() is long sequence ? sequence : 0) != expectedLocalSequence) throw new InvalidOperationException("Directory publication source changed.");
            LocalFiles.WriteBatch(connection, transaction, [row], cancellationToken);
            using var remove = connection.CreateCommand(); remove.Transaction = transaction;
            remove.CommandText = "DELETE FROM directory_captures WHERE parent_key=$key;"; remove.Parameters.AddWithValue("$key", key);
            if (remove.ExecuteNonQuery() != 1) throw new InvalidOperationException("Directory capture disappeared during publication.");
            using var result = connection.CreateCommand(); result.Transaction = transaction;
            result.CommandText = "SELECT " + FolderMaps.LocalColumns + " FROM local_files WHERE path_key=$key;"; result.Parameters.AddWithValue("$key", key);
            using var reader = result.ExecuteReader(); if (!reader.Read()) throw new InvalidOperationException("Directory publication disappeared.");
            return FolderMaps.ReadLocal(reader);
        }, cancellationToken);
    }
    private static DirectoryCaptureState FreezeCapture(DirectoryCaptureState value) => value with
    { RootId = value.RootId.ToArray(), NativeId = value.NativeId.ToArray(), InputHash = value.InputHash.ToArray(), BuilderState = value.BuilderState?.ToArray(), Hash = value.Hash?.ToArray() };
    private static void RequireCaptureExpected(SqliteConnection connection, SqliteTransaction transaction, DirectoryCaptureState expected)
    {
        RequireBlockReferenceRoot(connection, transaction, expected.RootId);
        var actual = FindDirectoryCapture(connection, transaction, PathRules.Key(expected.ParentPath));
        if (actual is null || actual.ParentPath != expected.ParentPath || actual.MemberCount != expected.MemberCount || actual.Accepted != expected.Accepted || actual.LastKey != expected.LastKey
            || !actual.RootId.AsSpan().SequenceEqual(expected.RootId) || !actual.NativeId.AsSpan().SequenceEqual(expected.NativeId) || !actual.InputHash.AsSpan().SequenceEqual(expected.InputHash)
            || !Equal(actual.BuilderState, expected.BuilderState) || !Equal(actual.Hash, expected.Hash) || actual.Length != expected.Length)
            throw new InvalidOperationException("Directory capture progress changed.");
        static bool Equal(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);
    }
    private static DirectoryCaptureState? FindDirectoryCapture(SqliteConnection connection, SqliteTransaction? transaction, string key)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT parent_path,root_id,native_id,input_hash,member_count,accepted,last_key,builder_state,node_hash,node_length FROM directory_captures WHERE parent_key=$key;";
        command.Parameters.AddWithValue("$key", key); using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), reader.GetFieldValue<byte[]>(1), reader.GetFieldValue<byte[]>(2), reader.GetFieldValue<byte[]>(3), reader.GetInt64(4), reader.GetInt64(5),
            reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetFieldValue<byte[]>(7), reader.IsDBNull(8) ? null : reader.GetFieldValue<byte[]>(8), reader.IsDBNull(9) ? null : reader.GetInt32(9)) : null;
    }
    private static string CaptureParent(string path) { int separator = path.LastIndexOf('/'); return separator < 0 ? "" : path[..separator]; }
}
