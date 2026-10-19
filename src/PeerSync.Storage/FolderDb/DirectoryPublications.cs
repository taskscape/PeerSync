using Microsoft.Data.Sqlite;
using PeerSync.Core.Paths;
using PeerSync.Core.Sync;

namespace PeerSync.Storage.FolderDb;

public sealed record DirectoryPublication(Guid Version, string Path, byte[] RootId, byte[] NativeId, long ExpectedLocalSequence,
    uint Attributes, SignedRecord Template, byte[] ExpectedKeysHash, long ExpectedChildren, long CreatedTicks, long TouchedTicks)
{
    public long? KeysEpoch { get; init; }
    public bool Paused { get; init; }
    public int Attempts { get; init; }
    public long? FailedTicks { get; init; }
    public long? NextTicks { get; init; }
    public override string ToString() => "Directory publication (redacted)";
}
public sealed record DirectoryPublicationCache(string PathKey, byte[] RootId, byte[] NativeId, byte[] RecordSourceId, byte[] InputHash,
    long MemberCount, long InputEpoch, long TouchedTicks)
{
    public byte[] ExpectedKeysHash { get; init; } = [];
    public override string ToString() => "Directory publication cache (redacted)";
}

public sealed partial class FolderIndex
{
    private const string PublicationColumns = "version,path,root_id,native_id,expected_local_seq,attributes,record_body,signature,expected_keys_hash,expected_children,created_ticks,touched_ticks,keys_epoch,paused,attempts,failed_ticks,next_ticks";
    public Task<DirectoryPublication> StageDirectoryPublicationAsync(DirectoryPublication publication, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication); var row = FreezePublication(publication); ValidatePublication(row);
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequireBlockReferenceRoot(connection, transaction, row.RootId); string key = PathRules.Key(row.Path);
            var before = FindDirectoryPublication(connection, transaction, key);
            if (before is not null && !before.RootId.AsSpan().SequenceEqual(row.RootId)) throw new InvalidDataException("Directory publication belongs to another root.");
            using var prior = connection.CreateCommand(); prior.Transaction = transaction;
            prior.CommandText = "SELECT local_seq FROM local_files WHERE path_key=$path;"; prior.Parameters.AddWithValue("$path", key);
            if ((prior.ExecuteScalar() is long sequence ? sequence : 0) != row.ExpectedLocalSequence) throw new InvalidOperationException("Directory staging source changed.");
            if (before is not null && before.Version == row.Version)
            {
                RequirePublicationExpected(connection, transaction, row);
                if (before.CreatedTicks != row.CreatedTicks) throw new InvalidDataException("Directory publication version was reused.");
                using var touch = connection.CreateCommand(); touch.Transaction = transaction;
                touch.CommandText = "UPDATE directory_publications SET touched_ticks=$now WHERE path_key=$key;";
                touch.Parameters.AddWithValue("$now", row.TouchedTicks); touch.Parameters.AddWithValue("$key", key); touch.ExecuteNonQuery();
                return FindDirectoryPublication(connection, transaction, key)!;
            }
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO directory_publications(path_key,path,parent_key,depth,version,root_id,native_id,expected_local_seq,attributes,record_body,signature,expected_keys_hash,expected_children,created_ticks,touched_ticks) "
                + "VALUES($key,$path,$parent,$depth,$version,$root,$native,$seq,$attributes,$body,$signature,$keys,$count,$created,$touched) ON CONFLICT(path_key) DO UPDATE SET "
                + "path=excluded.path,parent_key=excluded.parent_key,depth=excluded.depth,version=excluded.version,root_id=excluded.root_id,native_id=excluded.native_id,expected_local_seq=excluded.expected_local_seq,"
                + "attributes=excluded.attributes,record_body=excluded.record_body,signature=excluded.signature,expected_keys_hash=excluded.expected_keys_hash,expected_children=excluded.expected_children,"
                + "created_ticks=excluded.created_ticks,touched_ticks=excluded.touched_ticks,keys_epoch=NULL,paused=0,attempts=0,failed_ticks=NULL,next_ticks=NULL;";
            command.Parameters.AddWithValue("$key", key); command.Parameters.AddWithValue("$path", row.Path); command.Parameters.AddWithValue("$parent", CaptureParent(key));
            command.Parameters.AddWithValue("$depth", row.Path.Count(character => character == '/')); command.Parameters.AddWithValue("$version", row.Version.ToString("N"));
            command.Parameters.AddWithValue("$root", row.RootId); command.Parameters.AddWithValue("$native", row.NativeId); command.Parameters.AddWithValue("$seq", row.ExpectedLocalSequence);
            command.Parameters.AddWithValue("$attributes", (long)row.Attributes); command.Parameters.AddWithValue("$body", row.Template.Body.ToArray()); command.Parameters.AddWithValue("$signature", row.Template.Signature.ToArray());
            command.Parameters.AddWithValue("$keys", row.ExpectedKeysHash); command.Parameters.AddWithValue("$count", row.ExpectedChildren); command.Parameters.AddWithValue("$created", row.CreatedTicks);
            command.Parameters.AddWithValue("$touched", row.TouchedTicks); command.ExecuteNonQuery(); return FindDirectoryPublication(connection, transaction, key)!;
        }, cancellationToken);
    }
    public DirectoryPublication? ReadDirectoryPublication(string path)
    { using var connection = database.OpenReader(); return FindDirectoryPublication(connection, null, PathRules.Key(path)); }
    public IReadOnlyList<DirectoryPublication> ReadDueDirectoryPublications(long nowTicks, int limit = 1000, int byteBudget = 8 * 1024 * 1024)
    {
        if (nowTicks is < 1 or > 3155378975999999999 || limit is < 1 or > 1000 || byteBudget is < 1 or > 8 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(limit));
        using var connection = database.OpenReader(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + PublicationColumns + ",length(record_body)+length(signature) FROM directory_publications "
            + "WHERE paused=0 AND (next_ticks IS NULL OR failed_ticks>$now OR next_ticks<=$now) ORDER BY depth DESC,path_key LIMIT $limit;";
        command.Parameters.AddWithValue("$now", nowTicks); command.Parameters.AddWithValue("$limit", limit);
        var rows = new List<DirectoryPublication>(); long bytes = 0; using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            long size = reader.GetInt64(17); if (rows.Count != 0 && size > byteBudget - bytes) break;
            rows.Add(ReadPublication(reader)); bytes = checked(bytes + size);
        }
        return rows.AsReadOnly();
    }
    public bool HasPendingDirectoryPublications()
    {
        using var connection = database.OpenReader(); using var command = connection.CreateCommand(); command.CommandText = "SELECT EXISTS(SELECT 1 FROM directory_publications);";
        return (long)command.ExecuteScalar()! != 0;
    }
    public bool HasPendingDirectoryPublication(string path, bool includeDescendants = false)
    {
        string key = PathRules.Key(path); using var connection = database.OpenReader(); using var command = connection.CreateCommand();
        command.CommandText = "WITH RECURSIVE ancestors(key) AS (VALUES($path) UNION ALL SELECT rtrim(rtrim(key,replace(key,'/','')),'/') FROM ancestors WHERE instr(key,'/')>0) "
            + "SELECT EXISTS(SELECT 1 FROM ancestors a JOIN directory_publications p ON p.path_key=a.key UNION ALL "
            + "SELECT 1 FROM directory_publications WHERE $descendants=1 AND path_key >= $low AND path_key < $high);";
        command.Parameters.AddWithValue("$path", key); command.Parameters.AddWithValue("$descendants", includeDescendants ? 1 : 0);
        command.Parameters.AddWithValue("$low", key + '/'); command.Parameters.AddWithValue("$high", key + '0'); return (long)command.ExecuteScalar()! != 0;
    }
    public bool HasPendingChildDirectoryPublication(string parentPath)
    {
        using var connection = database.OpenReader(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM directory_publications WHERE parent_key=$parent);"; command.Parameters.AddWithValue("$parent", PathRules.Key(parentPath));
        return (long)command.ExecuteScalar()! != 0;
    }
    public Task<DirectoryPublication> BindDirectoryPublicationKeysAsync(DirectoryPublication expected, long epoch, long nowTicks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected); var row = FreezePublication(expected); ValidatePublication(row);
        if (epoch < 0 || nowTicks is < 1 or > 3155378975999999999) throw new ArgumentOutOfRangeException(nameof(epoch));
        return database.WriteDurableAsync((connection, transaction) =>
        {
            RequirePublicationExpected(connection, transaction, row); RequireDirectoryCaptureEpoch(connection, transaction, epoch);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE directory_publications SET keys_epoch=$epoch,touched_ticks=$now WHERE path_key=$key;";
            command.Parameters.AddWithValue("$epoch", epoch); command.Parameters.AddWithValue("$now", nowTicks); command.Parameters.AddWithValue("$key", PathRules.Key(row.Path)); command.ExecuteNonQuery();
            return FindDirectoryPublication(connection, transaction, PathRules.Key(row.Path))!;
        }, cancellationToken);
    }
    public Task<DirectoryPublication> RetryDirectoryPublicationAsync(DirectoryPublication expected, long nowTicks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected); var row = FreezePublication(expected); ValidatePublication(row);
        if (nowTicks is < 1 or > 3155378975999999999) throw new ArgumentOutOfRangeException(nameof(nowTicks));
        return database.WriteDurableAsync((connection, transaction) =>
        {
            var actual = RequirePublicationExpected(connection, transaction, row); int attempt = Math.Min(7, actual.Attempts + 1);
            int seconds = Math.Min(600, 10 << (attempt - 1)); long next = Math.Min(DateTime.MaxValue.Ticks, nowTicks + seconds * TimeSpan.TicksPerSecond);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE directory_publications SET attempts=$attempt,failed_ticks=$now,next_ticks=$next,touched_ticks=$now WHERE path_key=$key;";
            command.Parameters.AddWithValue("$attempt", attempt); command.Parameters.AddWithValue("$now", nowTicks); command.Parameters.AddWithValue("$next", next);
            command.Parameters.AddWithValue("$key", PathRules.Key(row.Path)); command.ExecuteNonQuery(); return FindDirectoryPublication(connection, transaction, PathRules.Key(row.Path))!;
        }, cancellationToken);
    }
    private static DirectoryPublication? FindDirectoryPublication(SqliteConnection connection, SqliteTransaction? transaction, string key)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT " + PublicationColumns + " FROM directory_publications WHERE path_key=$key;"; command.Parameters.AddWithValue("$key", key);
        using var reader = command.ExecuteReader(); return reader.Read() ? ReadPublication(reader) : null;
    }
    private static DirectoryPublication ReadPublication(SqliteDataReader reader) => new(Guid.ParseExact(reader.GetString(0), "N"), reader.GetString(1), reader.GetFieldValue<byte[]>(2), reader.GetFieldValue<byte[]>(3),
        reader.GetInt64(4), checked((uint)reader.GetInt64(5)), new(reader.GetFieldValue<byte[]>(6), reader.GetFieldValue<byte[]>(7)), reader.GetFieldValue<byte[]>(8), reader.GetInt64(9), reader.GetInt64(10), reader.GetInt64(11))
        { KeysEpoch = reader.IsDBNull(12) ? null : reader.GetInt64(12), Paused = reader.GetBoolean(13), Attempts = reader.GetInt32(14), FailedTicks = reader.IsDBNull(15) ? null : reader.GetInt64(15), NextTicks = reader.IsDBNull(16) ? null : reader.GetInt64(16) };
    private static DirectoryPublication FreezePublication(DirectoryPublication row) => row with { RootId = row.RootId.ToArray(), NativeId = row.NativeId.ToArray(), ExpectedKeysHash = row.ExpectedKeysHash.ToArray() };
    private static void ValidatePublication(DirectoryPublication row)
    {
        _ = PathRules.Key(row.Path); FolderMaps.RequireBytes(row.RootId, 24); FolderMaps.RequireBytes(row.NativeId, 16); FolderMaps.RequireBytes(row.ExpectedKeysHash, 32);
        if (row.Version == Guid.Empty || row.ExpectedLocalSequence < 0 || row.ExpectedChildren < 0 || row.Template.BodyLength == 0 || row.CreatedTicks is < 1 or > 3155378975999999999 || row.TouchedTicks is < 1 or > 3155378975999999999)
            throw new ArgumentException("Invalid directory publication.", nameof(row));
    }
    private static DirectoryPublication RequirePublicationExpected(SqliteConnection connection, SqliteTransaction transaction, DirectoryPublication row)
    {
        RequireBlockReferenceRoot(connection, transaction, row.RootId); var actual = FindDirectoryPublication(connection, transaction, PathRules.Key(row.Path));
        if (actual is null || actual.Version != row.Version || actual.Path != row.Path || actual.ExpectedLocalSequence != row.ExpectedLocalSequence || actual.Attributes != row.Attributes
            || actual.ExpectedChildren != row.ExpectedChildren || !actual.RootId.AsSpan().SequenceEqual(row.RootId) || !actual.NativeId.AsSpan().SequenceEqual(row.NativeId)
            || !actual.ExpectedKeysHash.AsSpan().SequenceEqual(row.ExpectedKeysHash) || !actual.Template.Body.SequenceEqual(row.Template.Body) || !actual.Template.Signature.SequenceEqual(row.Template.Signature))
            throw new InvalidOperationException("Directory publication changed.");
        return actual;
    }
}
